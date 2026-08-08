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
        //
        // Denial is one rule per kind rather than a wildcard: the CLI takes patterns of the
        // form kind(argument) and rejects "*" outright — "Invalid rule format: *" before the
        // session even starts. These three are the whole of the kind list bar MCP servers,
        // which name themselves and so are shut off by the flag below instead.
        "--deny-tool", "shell",
        "--deny-tool", "write",
        "--deny-tool", "url",

        // The built-in GitHub MCP server is the one tool source no deny rule can name in
        // advance, and a review has no business reaching for an API.
        "--disable-builtin-mcps",

        // The prompt is the whole of the input. Instruction files belong to whoever wrote
        // them, and the working directory being ours is not a reason to let a user-level
        // AGENTS.md quietly rewrite what a review says.
        "--no-custom-instructions",

        // Nothing is watching stdin for an answer. Without this a question is a hang.
        "--no-ask-user",

        // Without this the CLI trails a stats block — duration, token counts, a resume
        // command — onto stdout, and stdout is what the review parser reads. It would land
        // in the review text as if the model had written it.
        "--silent",
    ];
}
