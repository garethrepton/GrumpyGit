namespace GrumpyGit.Core.Agents;

/// <summary>
/// Which review module is in use. Persisted by name, so the order here is free to change
/// and a value written by a newer build reads back as <see cref="None"/> on an older one —
/// which is the right failure: the feature is off, and nothing else in the client cares.
/// </summary>
public enum ReviewModuleId
{
    /// <summary>No module. The client is exactly the git client it was before any of this.</summary>
    None,

    /// <summary>llama.cpp in this process, against a GGUF on this disk.</summary>
    Local,

    /// <summary>GitHub Copilot CLI, already installed and already signed in.</summary>
    Copilot,

    /// <summary>Claude Code CLI, already installed and already signed in.</summary>
    ClaudeCode,
}

/// <summary>
/// How a module gets its answer. The distinction earns its place because it decides what
/// has to be true before the module can work, and the two answers have nothing in common:
/// one needs gigabytes of disk, the other needs an executable on PATH and a session.
/// </summary>
public enum ReviewModuleKind
{
    /// <summary>Weights loaded into this process. Needs disk and memory; needs no account.</summary>
    InProcess,

    /// <summary>
    /// A coding agent the user installed and signed in to, driven as a child process. Needs
    /// no account handling <em>here</em> — that is the entire point, and it is the same
    /// argument as Git Credential Manager: the tool that owns the credential keeps it.
    /// </summary>
    ExternalCli,
}

/// <summary>
/// One module the user can choose at first run or in settings.
/// </summary>
/// <param name="Id">Stable identity, persisted by name.</param>
/// <param name="Name">What the user sees.</param>
/// <param name="Tagline">One line on the trade-off, for choosing between them.</param>
/// <param name="Requires">
/// What has to be true before it will work, said plainly enough to act on. Shown under the
/// name in the picker, so a user knows before choosing rather than after.
/// </param>
/// <param name="SendsCodeOffMachine">
/// Whether choosing this module means the diff leaves this computer.
///
/// <strong>This is the single most important field in the file</strong>, and it is a field
/// rather than a comment so that no screen can show a module without being able to say it.
/// The client's original position was that nothing leaves except git's own traffic; two of
/// these modules change that, deliberately and only when the user picks one.
/// </param>
/// <param name="Executable">
/// Command name for an <see cref="ReviewModuleKind.ExternalCli"/> module, resolved on PATH.
/// A bare name, never a path from settings or a repository — see <see cref="AgentProcess"/>.
/// </param>
public sealed record ReviewModule(
    ReviewModuleId Id,
    string Name,
    string Tagline,
    string Requires,
    ReviewModuleKind Kind,
    bool SendsCodeOffMachine,
    string? Executable = null,
    string? InstallHint = null)
{
    /// <summary>Badge text for the review panel — the user should always know who answered.</summary>
    public string Badge => Id switch
    {
        ReviewModuleId.Local => "LOCAL",
        ReviewModuleId.Copilot => "COPILOT",
        ReviewModuleId.ClaudeCode => "CLAUDE CODE",
        _ => string.Empty,
    };

    /// <summary>
    /// The privacy sentence, in the words a user would use. Deliberately blunt for the
    /// modules that send code away — a euphemism here would be the failure.
    /// </summary>
    public string PrivacyLine => SendsCodeOffMachine
        ? "The diff is sent to this agent's service, under your own account and its terms."
        : "Nothing leaves this computer. The diff never goes further than this process.";
}

/// <summary>
/// The modules this build offers.
///
/// A hard-coded list, like <see cref="LocalModel.ModelCatalogue"/> and for the same reason:
/// a fixed list is a fixed surface. There is no plug-in directory, no discovery, and no way
/// for a repository or a settings file to name a fourth module — the two CLI entries below
/// resolve a bare command name on PATH and nothing else.
///
/// All three are opt-in and none is a default. A user who wants a git client with no
/// language model anywhere near it picks nothing, and every trace of the feature stays off.
/// </summary>
public static class ReviewModuleCatalogue
{
    public static readonly ReviewModule Local = new(
        ReviewModuleId.Local,
        Name: "Local model",
        Tagline: "A model running on this machine. Slower and blunter, and the only one that reads your code without anyone else seeing it.",
        Requires: "A few gigabytes of disk for the weights, and patience on a machine with no GPU.",
        Kind: ReviewModuleKind.InProcess,
        SendsCodeOffMachine: false);

    /// <summary>
    /// GitHub Copilot CLI, driven in its programmatic mode.
    ///
    /// The best fit of the three for this codebase, and the reason is what it does
    /// <em>not</em> add. No HTTP client, no API surface, no token: the CLI is already
    /// installed and already signed in, and it owns the credential exactly as Git Credential
    /// Manager owns git's. This client launches a process and reads its stdout — the same
    /// thing it already does for every git command — so the whole integration is one file
    /// and no new package.
    /// </summary>
    public static readonly ReviewModule Copilot = new(
        ReviewModuleId.Copilot,
        Name: "GitHub Copilot",
        Tagline: "Uses the Copilot CLI you have already signed in to. Fast, no download, and by far the best readings of the three.",
        Requires: "GitHub Copilot CLI installed and signed in, and a Copilot subscription on your account.",
        Kind: ReviewModuleKind.ExternalCli,
        SendsCodeOffMachine: true,
        Executable: "copilot",
        InstallHint: "npm install -g @github/copilot, then run copilot and sign in.");

    public static readonly ReviewModule ClaudeCode = new(
        ReviewModuleId.ClaudeCode,
        Name: "Claude Code",
        Tagline: "Uses the Claude Code CLI you have already signed in to. The most thorough reader; costs a turn of your plan per file.",
        Requires: "Claude Code installed and signed in, on a Claude plan or an API key it already holds.",
        Kind: ReviewModuleKind.ExternalCli,
        SendsCodeOffMachine: true,
        Executable: "claude",
        InstallHint: "npm install -g @anthropic-ai/claude-code, then run claude and sign in.");

    /// <summary>
    /// Offered in the order a first-run picker should read them: the one most people should
    /// take first, then the thorough one, then the private one. Not a ranking of quality —
    /// <see cref="Local"/> is last because it is the one that costs disk and time, and a
    /// list has to start somewhere.
    /// </summary>
    public static IReadOnlyList<ReviewModule> All { get; } = [Copilot, ClaudeCode, Local];

    public static ReviewModule? Find(ReviewModuleId id) => All.FirstOrDefault(m => m.Id == id);

    /// <summary>
    /// Parses a persisted module name. Anything unrecognised — a hand-edited settings file,
    /// or a value written by a newer build — is <see cref="ReviewModuleId.None"/> rather
    /// than an error, so a bad string turns the feature off instead of failing startup.
    /// </summary>
    public static ReviewModuleId Parse(string? name) =>
        Enum.TryParse<ReviewModuleId>(name, ignoreCase: true, out var id) && Find(id) is not null
            ? id
            : ReviewModuleId.None;
}
