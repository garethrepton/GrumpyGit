using System.Text;
using CliWrap;
using CliWrap.EventStream;

namespace GrumpyGit.Core.Agents;

/// <summary>
/// An <see cref="IReviewAgent"/> backed by a coding agent the user already installed and
/// already signed in to, driven one prompt at a time as a child process.
///
/// The shape is the same argument as Git Credential Manager, and it is the reason these
/// modules cost this codebase almost nothing: <strong>there is no API client here, no
/// token, and no new package</strong>. The CLI holds the credential, the CLI talks to its
/// service, and this application launches a process and reads its stdout — which it already
/// does for every git command. Everything specific to one vendor is the argument list its
/// subclass builds, and that is the whole of the difference between them.
///
/// The trade is stated plainly wherever the module is chosen or shown: the diff leaves this
/// machine. That is not a detail to bury (<see cref="ReviewModule.SendsCodeOffMachine"/>).
/// </summary>
public abstract class CliReviewAgent : IReviewAgent
{
    /// <summary>
    /// Ceiling on the prompt handed over as a command-line argument.
    ///
    /// Windows caps a command line at 32,767 characters and fails the whole launch when it
    /// is exceeded, with an error that says nothing about prompts. The review budget is
    /// 12,000 characters of diff, so this never fires in practice — it is here so that if it
    /// ever does, the user reads a sentence about the change being too large rather than a
    /// Win32 error code.
    /// </summary>
    private const int MaxPromptChars = 24_000;

    private readonly ReviewModule _module;
    private readonly string _workingDirectory;
    private readonly SemaphoreSlim _probeGate = new(1, 1);

    private string? _executablePath;
    private bool _probeFailed;

    protected CliReviewAgent(ReviewModule module, string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(module);

        if (module.Executable is null)
            throw new ArgumentException("Module has no executable.", nameof(module));

        _module = module;
        _workingDirectory = workingDirectory;
    }

    public ReviewModuleId Module => _module.Id;

    /// <summary>
    /// True as long as this module was chosen. Whether the CLI is actually installed is
    /// <see cref="EnsureLoadedAsync"/>'s answer, not this one — a user who picked Copilot
    /// and has not installed it should see the panel say so, not see no panel.
    /// </summary>
    public bool IsConfigured => true;

    public bool IsReady => _executablePath is not null;

    public string? LoadError { get; private set; }

    public async Task<bool> EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_executablePath is not null) return true;
        if (_probeFailed) return false;

        await _probeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_executablePath is not null) return true;
            if (_probeFailed) return false;

            var command = _module.Executable!;
            var resolved = AgentProcess.Resolve(command);

            if (resolved is null)
            {
                // Latched: PATH does not change under a running process, so re-probing on
                // every diff would spend a filesystem sweep to reach the same answer.
                _probeFailed = true;
                LoadError = AgentProcess.ResolvesToShimOnly(command)
                    ? $"Found {command} on PATH, but only as a script shim. Grumpy will not pass a diff " +
                      "through a shell — install the standalone build and it will be used automatically."
                    : $"{_module.Name} is not installed. {_module.InstallHint}";
                return false;
            }

            Directory.CreateDirectory(_workingDirectory);
            _executablePath = resolved;
            return true;
        }
        catch (OperationCanceledException)
        {
            // Cancelled probe is not a failed one — a later review may try again.
            return false;
        }
        catch (Exception ex)
        {
            _probeFailed = true;
            LoadError = ex.Message;
            return false;
        }
        finally
        {
            _probeGate.Release();
        }
    }

    public async Task<string> CompleteAsync(
        ModelPrompt prompt,
        ReviewOptions options,
        IProgress<string>? partial = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(options);

        if (!await EnsureLoadedAsync(ct).ConfigureAwait(false) || _executablePath is null)
            throw new InvalidOperationException(LoadError ?? $"{_module.Name} is not available.");

        var stdin = StandardInput(prompt);

        // Only what actually travels as a command line is measured. A module that feeds the
        // prompt on stdin has no limit worth enforcing here, and applying one anyway would
        // decline a review it could perfectly well have done.
        if (stdin is null && prompt.System.Length + prompt.User.Length > MaxPromptChars)
            throw new InvalidOperationException(
                "This change is too large to send to an external agent in one prompt.");

        var command = AgentProcess
            .Start(_executablePath, _workingDirectory)
            .WithArguments(BuildArguments(prompt, options));

        if (stdin is not null)
            command = command.WithStandardInputPipe(PipeSource.FromString(stdin));

        var answer = new StringBuilder();
        var errors = new StringBuilder();

        await foreach (var evt in command.ListenAsync(ct).ConfigureAwait(false))
        {
            switch (evt)
            {
                case StandardOutputCommandEvent stdout:
                    answer.AppendLine(stdout.Text);

                    // Reported as the whole answer so far rather than the delta, matching
                    // the in-process module: the panel binds one string, and reassembling
                    // deltas in the UI is a second place to get the ordering wrong.
                    partial?.Report(AgentProcess.Clean(answer.ToString()));
                    break;

                case StandardErrorCommandEvent stderr:
                    errors.AppendLine(stderr.Text);
                    break;

                case ExitedCommandEvent exited when exited.ExitCode != 0:
                    throw new InvalidOperationException(FailureMessage(exited.ExitCode, errors.ToString()));
            }
        }

        return AgentProcess.Clean(answer.ToString()).Trim();
    }

    /// <summary>
    /// The vendor-specific part, and the only thing a subclass has to supply. Every element
    /// travels as its own argument — nothing is concatenated into a command line, and the
    /// prompt is one element however much punctuation the diff contains (commandment 5).
    /// </summary>
    protected abstract IReadOnlyList<string> BuildArguments(ModelPrompt prompt, ReviewOptions options);

    /// <summary>
    /// The prompt to feed on stdin, or null to pass it as an argument instead.
    ///
    /// Stdin is the better answer wherever the CLI accepts it: the diff never appears on a
    /// command line at all, so neither Windows' 32k limit nor any question about who parses
    /// the quoting can arise. Not every CLI supports it, which is the only reason this is a
    /// choice rather than the rule.
    /// </summary>
    protected virtual string? StandardInput(ModelPrompt prompt) => null;

    /// <summary>
    /// Turns a non-zero exit into something worth reading. The overwhelmingly common cause
    /// is a CLI that is installed but not signed in, and its own stderr says so far better
    /// than any guess here would — so that is what is shown, trimmed to a line.
    /// </summary>
    private string FailureMessage(int exitCode, string stderr)
    {
        var reason = AgentProcess.Clean(stderr)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        return reason is { Length: > 0 }
            ? $"{_module.Name} failed: {reason}"
            : $"{_module.Name} exited with code {exitCode}. It may need signing in again.";
    }
}
