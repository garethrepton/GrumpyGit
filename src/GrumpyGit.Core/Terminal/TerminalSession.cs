using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GrumpyGit.Core.Terminal;

/// <summary>
/// Owns the lifetime of one shell attached to a pseudo console: starting it in a given
/// working directory, pumping its output, forwarding keystrokes, and making sure the
/// process is gone when we are.
///
/// <para>
/// Parsing is deliberately *not* done here. The session hands out raw decoded text via
/// <see cref="OutputReceived"/> and the caller feeds it to a <see cref="TerminalScreen"/>
/// on whichever thread owns the screen — which keeps the screen single-threaded without
/// any locking, and keeps this class testable as pure process plumbing.
/// </para>
/// </summary>
public sealed class TerminalSession : IDisposable
{
    /// <summary>
    /// The default Windows shell. <c>powershell.exe</c> rather than <c>pwsh.exe</c>
    /// because it ships with the OS and so is always present; <c>-NoProfile</c> because a
    /// slow or noisy user profile turning the panel into a five-second blank rectangle is
    /// a worse first impression than missing a few aliases.
    /// </summary>
    public const string DefaultWindowsShell = "powershell.exe -NoLogo -NoProfile";

    /// <summary>The ETX byte. ConPTY turns this into a real CTRL_C_EVENT for the child.</summary>
    private const string InterruptSequence = "\x03";

    // Every live session, so that an abrupt shutdown still closes the pseudo consoles.
    // Without this a hard exit could leave the shell parented to nothing.
    private static readonly List<TerminalSession> Live = new();
    private static readonly object LiveLock = new();

    static TerminalSession()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposeAll();
    }

    private readonly ConPtyTerminal _terminal;
    private readonly Task _readLoop;
    private readonly CancellationTokenSource _stopping = new();
    private bool _disposed;

    private TerminalSession(ConPtyTerminal terminal, string workingDirectory)
    {
        _terminal = terminal;
        WorkingDirectory = workingDirectory;
        IsRunning = true;

        lock (LiveLock) Live.Add(this);

        _readLoop = Task.Run(ReadLoop);
    }

    /// <summary>Raised on the reader thread whenever output arrives. Marshal before touching UI.</summary>
    public event EventHandler<string>? OutputReceived;

    /// <summary>Raised on the reader thread when the shell ends, with a human-readable reason.</summary>
    public event EventHandler<string>? Exited;

    public string WorkingDirectory { get; }

    public bool IsRunning { get; private set; }

    /// <summary>
    /// Starts a shell rooted at <paramref name="workingDirectory"/>.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">Not running on Windows.</exception>
    /// <exception cref="InvalidOperationException">The directory is unusable — see the message.</exception>
    public static TerminalSession Start(
        string workingDirectory,
        int columns = 120,
        int rows = 30,
        string? shellCommandLine = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "The embedded terminal uses ConPTY, which is Windows-only.");

        ValidateWorkingDirectory(workingDirectory);

        var terminal = new ConPtyTerminal(
            ClampDimension(columns),
            ClampDimension(rows),
            workingDirectory,
            shellCommandLine ?? DefaultWindowsShell);

        return new TerminalSession(terminal, workingDirectory);
    }

    /// <summary>
    /// The working directory becomes the shell's cwd, so it must be a directory we are
    /// willing to hand to a process verbatim. UNC paths are refused outright: launching a
    /// shell against <c>\\server\share</c> makes Windows authenticate to that host as the
    /// current user before anything is typed, which is not a side effect that should fall
    /// out of opening a repository tab.
    /// </summary>
    /// <remarks>
    /// These throw <see cref="InvalidOperationException"/> rather than
    /// <see cref="ArgumentException"/> because the messages are shown to the user verbatim
    /// in the panel header, and ArgumentException appends "(Parameter '…')" to whatever it
    /// is handed.
    /// </remarks>
    private static void ValidateWorkingDirectory(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            throw new InvalidOperationException("Open a repository to start a terminal.");

        if (workingDirectory.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException("UNC paths are not supported by the terminal.");

        if (!Path.IsPathRooted(workingDirectory))
            throw new InvalidOperationException("Relative paths are not supported by the terminal.");

        if (!Directory.Exists(workingDirectory))
            throw new InvalidOperationException($"'{workingDirectory}' no longer exists.");
    }

    // ConPTY sizes are 16-bit, and a zero in either axis makes CreatePseudoConsole fail.
    private static short ClampDimension(int value) => (short)Math.Clamp(value, 1, short.MaxValue);

    /// <summary>Sends text to the shell exactly as if it had been typed.</summary>
    public void Send(string text)
    {
        if (_disposed || string.IsNullOrEmpty(text)) return;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            _terminal.Input.Write(bytes, 0, bytes.Length);
            _terminal.Input.Flush();
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    /// <summary>
    /// Interrupts whatever is running. Writing ETX to the pseudo console is what makes
    /// this a genuine interrupt rather than a kill: conhost raises CTRL_C_EVENT against
    /// the child's process group, so the running command dies and the shell survives.
    /// </summary>
    public void SendInterrupt() => Send(InterruptSequence);

    /// <summary>
    /// Tells the shell how many character cells it has. Without this the shell assumes the
    /// size it was created with, and line editing wraps in the wrong places as soon as the
    /// panel is resized.
    /// </summary>
    public void Resize(int columns, int rows)
    {
        if (_disposed) return;
        try { _terminal.Resize(ClampDimension(columns), ClampDimension(rows)); }
        catch (InvalidOperationException) { }
    }

    private void ReadLoop()
    {
        var bytes = new byte[4096];
        var chars = new char[4096];

        // A stateful decoder, because a 4 KB read can land mid-way through a multi-byte
        // sequence. Decoding each chunk independently would corrupt every such character.
        var decoder = Encoding.UTF8.GetDecoder();
        var reason = "Terminal process exited";

        try
        {
            while (true)
            {
                var read = _terminal.Output.Read(bytes, 0, bytes.Length);
                if (read <= 0) break;

                var count = decoder.GetChars(bytes, 0, read, chars, 0);
                if (count > 0 && !_stopping.IsCancellationRequested)
                    OutputReceived?.Invoke(this, new string(chars, 0, count));
            }
        }
        catch (Exception) when (_stopping.IsCancellationRequested)
        {
            // Teardown closed the pipe out from under the read. Expected.
        }
        catch (IOException)
        {
            reason = "Terminal connection lost";
        }
        catch (ObjectDisposedException)
        {
            reason = "Terminal connection lost";
        }

        IsRunning = false;

        if (!_stopping.IsCancellationRequested)
            Exited?.Invoke(this, reason);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        IsRunning = false;

        lock (LiveLock) Live.Remove(this);

        // Cancel first so the reader stops raising events, but do not try to stop it
        // reading: closing a pseudo console blocks until its output has been drained, so
        // a reader that has already given up would deadlock this call.
        _stopping.Cancel();

        try { _terminal.Dispose(); }
        catch (Exception) { /* teardown is best-effort; the handles are going away regardless */ }

        // Bounded, because the wait is only to keep the reader from outliving the panel —
        // it is not load-bearing, and the UI thread is usually the one calling us.
        try { _readLoop.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }

        _stopping.Dispose();
    }

    private static void DisposeAll()
    {
        TerminalSession[] sessions;
        lock (LiveLock) sessions = Live.ToArray();

        foreach (var session in sessions)
        {
            try { session.Dispose(); }
            catch (Exception) { /* shutting down anyway */ }
        }
    }
}
