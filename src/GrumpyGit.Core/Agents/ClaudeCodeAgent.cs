namespace GrumpyGit.Core.Agents;

/// <summary>
/// Claude Code in print mode.
///
/// Same bargain as <see cref="CopilotCliAgent"/> — an installed, already-signed-in CLI
/// driven as a child process, so no credential, no client and no package land in this
/// codebase — and the same cost: <strong>the diff goes to Anthropic</strong>, under the
/// user's own plan.
///
/// It differs in two ways worth the flags below. It takes the prompt on stdin, so the diff
/// never touches a command line at all. And it is an agent rather than a completion
/// endpoint, so left alone it would read project instruction files, load MCP servers, keep
/// a transcript, and generally do far more than answer the question — every flag here is
/// switching one of those off.
/// </summary>
public sealed class ClaudeCodeAgent : CliReviewAgent
{
    public ClaudeCodeAgent(string workingDirectory)
        : base(ReviewModuleCatalogue.ClaudeCode, workingDirectory)
    {
    }

    protected override IReadOnlyList<string> BuildArguments(ModelPrompt prompt, ReviewOptions options) =>
        Arguments(prompt);

    /// <summary>
    /// The argument list, as a pure function of the prompt — see
    /// <see cref="CopilotCliAgent.Arguments"/> for why it is shaped this way.
    /// </summary>
    public static IReadOnlyList<string> Arguments(ModelPrompt prompt) =>
    [
        "--print",
        "--output-format", "text",

        // Replaces the agentic system prompt rather than appending to it. Appending leaves
        // a coding agent that has been told to also produce a review; replacing leaves a
        // reviewer, which is the only thing this panel can parse.
        "--system-prompt", prompt.System,

        // The transcript would otherwise put the user's diff in a session file on disk,
        // written by a process this application started. Nothing here caches source, and
        // that has to hold for what we launch as well as for what we run (commandment 9).
        "--no-session-persistence",

        // The user's own MCP servers, skills and plugins are configured for their work, not
        // for a git client's review panel. Loading them would make the answer depend on a
        // machine's setup and hand the diff to whatever else was configured there.
        "--strict-mcp-config",
        "--disable-slash-commands",

        // Belt and braces next to the empty working directory: the tools that would reach
        // the filesystem, a shell or the network are named off. Variadic, so nothing
        // positional may follow it — which is fine, because the prompt goes on stdin.
        "--disallowed-tools", "Bash", "Edit", "Write", "NotebookEdit", "WebFetch", "WebSearch", "Task",
    ];

    protected override string? StandardInput(ModelPrompt prompt) => prompt.User;
}
