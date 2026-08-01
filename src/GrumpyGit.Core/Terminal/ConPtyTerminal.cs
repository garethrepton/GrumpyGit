using System;
using System.IO;
using System.Runtime.InteropServices;

namespace GrumpyGit.Core.Terminal;

/// <summary>
/// High-level wrapper: spawns a shell process attached to a Windows Pseudo Console (ConPTY).
/// Exposes <see cref="Input"/> (write keystrokes) and <see cref="Output"/> (read VT sequences).
/// </summary>
public sealed class ConPtyTerminal : IDisposable
{
    private readonly PseudoConsolePipe _inputPipe;
    private readonly PseudoConsolePipe _outputPipe;
    private readonly PseudoConsole _pseudoConsole;
    private readonly IntPtr _processHandle;
    private readonly IntPtr _threadHandle;
    private readonly IntPtr _attributeList;
    private bool _disposed;

    /// <summary>Write to this stream to send keystrokes to the shell.</summary>
    public FileStream Input { get; }

    /// <summary>Read from this stream to receive VT-encoded terminal output.</summary>
    public FileStream Output { get; }

    public ConPtyTerminal(short cols, short rows, string workingDirectory, string shell = "powershell.exe")
    {
        _inputPipe = new PseudoConsolePipe();
        _outputPipe = new PseudoConsolePipe();

        // ConPTY reads from inputPipe.ReadSide, writes to outputPipe.WriteSide
        _pseudoConsole = PseudoConsole.Create(
            _inputPipe.ReadSide, _outputPipe.WriteSide, cols, rows);

        // Do NOT close these handles — CreatePseudoConsole does not duplicate them.
        // ConPTY needs inputPipe.ReadSide to read our input and outputPipe.WriteSide
        // to write shell output. They are cleaned up in Dispose() via the pipe objects.

        // App writes keystrokes to inputPipe.WriteSide → shell stdin
        Input = new FileStream(_inputPipe.WriteSide, FileAccess.Write);
        // App reads terminal output from outputPipe.ReadSide ← shell stdout
        Output = new FileStream(_outputPipe.ReadSide, FileAccess.Read);

        // Spawn the shell process attached to the pseudo console
        _attributeList = CreateAttributeList(_pseudoConsole.Handle);
        var (processHandle, threadHandle) = StartProcess(shell, workingDirectory, _attributeList);
        _processHandle = processHandle;
        _threadHandle = threadHandle;
    }

    public void Resize(short cols, short rows) => _pseudoConsole.Resize(cols, rows);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Close the pseudo console first — signals the shell to exit
        _pseudoConsole.Dispose();

        // Give the shell process a chance to exit gracefully, then force-kill
        if (_processHandle != IntPtr.Zero)
        {
            if (WaitForSingleObject(_processHandle, 2000) != WAIT_OBJECT_0)
                TerminateProcess(_processHandle, 0);
            CloseHandle(_processHandle);
        }

        if (_threadHandle != IntPtr.Zero)
            CloseHandle(_threadHandle);

        if (_attributeList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(_attributeList);
            Marshal.FreeHGlobal(_attributeList);
        }

        Input?.Dispose();
        Output?.Dispose();
        _inputPipe?.Dispose();
        _outputPipe?.Dispose();
    }

    private static IntPtr CreateAttributeList(IntPtr pseudoConsoleHandle)
    {
        var size = IntPtr.Zero;
        // First call to get required size
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);

        var attributeList = Marshal.AllocHGlobal(size);
        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref size))
                throw new InvalidOperationException(
                    $"InitializeProcThreadAttributeList failed: {Marshal.GetLastWin32Error()}");

            if (!UpdateProcThreadAttribute(
                    attributeList, 0,
                    (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    pseudoConsoleHandle,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero, IntPtr.Zero))
            {
                throw new InvalidOperationException(
                    $"UpdateProcThreadAttribute failed: {Marshal.GetLastWin32Error()}");
            }

            return attributeList;
        }
        catch
        {
            Marshal.FreeHGlobal(attributeList);
            throw;
        }
    }

    private static (IntPtr process, IntPtr thread) StartProcess(
        string command, string workingDir, IntPtr attributeList)
    {
        var startupInfo = new STARTUPINFOEX
        {
            StartupInfo = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFOEX>()
            },
            lpAttributeList = attributeList
        };

        if (!CreateProcessW(
                null, command,
                IntPtr.Zero, IntPtr.Zero,
                false,
                EXTENDED_STARTUPINFO_PRESENT,
                IntPtr.Zero,
                workingDir,
                ref startupInfo,
                out var processInfo))
        {
            throw new InvalidOperationException(
                $"CreateProcessW failed: {Marshal.GetLastWin32Error()}");
        }

        return (processInfo.hProcess, processInfo.hThread);
    }

    // ── Constants ──────────────────────────────────────────────────────────────

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint WAIT_OBJECT_0 = 0;

    // ── Structs ────────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX, dwY, dwXSize, dwYSize;
        public int dwXCountChars, dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    // ── P/Invoke ───────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList, int dwAttributeCount,
        int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute,
        IntPtr lpValue, IntPtr cbSize,
        IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName, string lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);
}
