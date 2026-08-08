namespace GrumpyGit.Core.Agents;

/// <summary>
/// GitHub Copilot CLI in its programmatic mode.
///
/// The module this client is best suited to carry, and the reason is subtraction. It adds
/// no package, no HTTP client, no token handling and no account UI — the CLI is installed,
/// the CLI is signed in, and the CLI owns the credential, which is the same position this
/// codebase already takes with Git Credential Manager and defends in <c>CLAUDE.md</c>. The
/// whole integration is the argument list below.
///
/// What it does add is the thing that must never be soft-pedalled: <strong>the diff goes to
/// GitHub</strong>, under the user's own account and subscription. That is why choosing a
/// module is a decision the user makes at first run rather than a default they discover.
/// </summary>
public sealed class CopilotCliAgent : CliReviewAgent
{
    public CopilotCliAgent(string workingDirectory)
        : base(ReviewModuleCatalogue.Copilot, workingDirectory)
    {
    }

    /// <summary>
    /// The prompt travels as one argument, because this CLI has no way to take it on stdin
    /// (github/copilot-cli#1046 asks for one and it is still open). That is safe here only
    /// because <see cref="AgentProcess"/> refuses to launch anything but a real executable
    /// image: with no <c>cmd.exe</c> in the chain, nothing re-parses the quoting, and a diff
    /// full of <c>"</c> and <c>&amp;</c> stays a single argument.
    /// </summary>
    protected override IReadOnlyList<string> BuildArguments(ModelPrompt prompt, ReviewOptions options) =>
        Arguments(prompt);

    /// <summary>
    /// The argument list, as a pure function of the prompt. Public and static because it is
    /// the whole of what this module does differently, and a test that can read it is worth
    /// more than one that can only observe a process it must not actually launch.
    /// </summary>
    public static IReadOnlyList<string> Arguments(ModelPrompt prompt) =>
    [
        // This CLI has no system-turn flag, so the standing instruction is folded into the
        // one prompt. Same shape as the Gemma fallback in the local module: state the
        // format the runtime actually offers rather than inventing a slot it does not have.
        "--prompt", $"{prompt.System}\n\n{prompt.User}",

        // Deny every tool. This is a read of text that has already been handed over — it has
        // no business running a shell, editing a file or reaching for the repository. Deny
        // beats allow-nothing here because the CLI documents deny as taking precedence over
        // both --allow-tool and --allow-all, so no later flag or config can widen it.
        "--deny-tool", "*",

        // Nothing is watching stdin for an answer. Without this a question is a hang.
        "--no-ask-user",
    ];
}
