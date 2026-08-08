namespace GrumpyGit.Core.Agents;

/// <summary>
/// Something that can read a diff and answer in words.
///
/// This is the seam every review module plugs into, and the reason it is worth having is
/// that <see cref="LocalModel.DiffReviewService"/> — the caching, the queueing, the
/// chunking, the parsing — has nothing to do with <em>where</em> the answer comes from. One
/// implementation loads weights into this process; the others hand the prompt to a coding
/// agent the user already has installed. The service cannot tell them apart, and neither
/// can the panel.
///
/// It is also the boundary itself: an implementation of this interface is the only place
/// allowed to hold a native inference handle or to launch an agent process, in the same way
/// <see cref="Git.IGitService"/> is the only place allowed to launch git.
///
/// <strong>Whether a prompt leaves the machine is a property of the module, not of this
/// interface</strong> — see <see cref="ReviewModule.SendsCodeOffMachine"/>. Every consumer
/// that puts an answer on screen has to say which module produced it, because "a model read
/// your diff" and "GitHub read your diff" are not the same sentence.
/// </summary>
public interface IReviewAgent
{
    /// <summary>Which module this is, so the UI can name it without being told separately.</summary>
    ReviewModuleId Module { get; }

    /// <summary>
    /// True when the user has set this module up at all — a model file chosen, or a CLI
    /// found on PATH. False means the feature is simply off and the panel should not exist;
    /// it is not an error state.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// True when <see cref="CompleteAsync"/> will answer. False before the first load, when
    /// nothing is configured, or after a load failed — the caller shows nothing rather than
    /// an error, since this is an optional feature.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Why the last load failed, or null if it has not. Exists because the alternative was
    /// worse than it looked: an agent that will not start produces no review and no error, so
    /// a user whose machine cannot hold the weights they just downloaded — or who has not
    /// signed the CLI in — sees an empty panel and no way to find out why.
    /// </summary>
    string? LoadError { get; }

    /// <summary>
    /// Gets ready if it is not ready already: loads the weights, or finds and version-checks
    /// the executable. Safe to call repeatedly and from several callers at once; the work
    /// happens once. Returns false when the module is not configured or could not start.
    /// </summary>
    Task<bool> EnsureLoadedAsync(CancellationToken ct = default);

    /// <summary>
    /// Runs one completion. <paramref name="partial"/> receives text as it is generated so
    /// a caller can show the answer arriving rather than a spinner; it is optional.
    ///
    /// Implementations are not required to be safe against concurrent calls — callers
    /// serialise. <see cref="LocalModel.DiffReviewService"/> is the one that does.
    /// </summary>
    Task<string> CompleteAsync(
        ModelPrompt prompt,
        ReviewOptions options,
        IProgress<string>? partial = null,
        CancellationToken ct = default);
}

/// <summary>
/// What to send the agent, before any runtime's chat template is applied. Kept as two parts
/// rather than one blob because every local runtime wants the system turn separately, and
/// every CLI wants it as its own flag — pre-formatting them together would bake one
/// module's convention into a prompt builder that has no business knowing about it.
/// </summary>
/// <param name="System">Standing instruction — the same for every review.</param>
/// <param name="User">The diff, rendered to fit the budget.</param>
public sealed record ModelPrompt(string System, string User);

/// <summary>
/// What to ask of one completion. Kept here rather than on the implementation so the
/// caller's intent — "short answer, low creativity" — survives a change of module.
/// </summary>
/// <param name="MaxTokens">
/// Ceiling on generated tokens. A review that runs long is a review nobody reads, and on
/// CPU every token is wall-clock the user is waiting for. A hosted agent may have no way to
/// honour this exactly; it is a request, not a guarantee.
/// </param>
/// <param name="Temperature">
/// Low by default. This is a code review, not prose: the same diff should produce the same
/// reading twice, and invention is the failure mode that would kill the feature.
/// </param>
public sealed record ReviewOptions(int MaxTokens = 400, float Temperature = 0.2f)
{
    public static readonly ReviewOptions Review = new();

    /// <summary>
    /// A ceiling sized to the file in front of it.
    ///
    /// The reply carries a summary, a risk line and <em>one line per change</em>, so a fixed
    /// budget is only ever right for one size of diff. At 400 tokens a file with twenty
    /// changes runs out around the eighth, and every change after that renders with no
    /// reading against it — which looks like the model declining to comment rather than
    /// never having been given room to.
    ///
    /// Twenty-two tokens a change is the twelve-word instruction plus its label. The upper
    /// bound is wall-clock rather than correctness: on a CPU these are seconds each, and a
    /// reply long enough to need more than this is one nobody reads to the end of.
    /// </summary>
    public static ReviewOptions ForReview(int changeCount) =>
        new(MaxTokens: Math.Clamp(160 + changeCount * 22, 400, 900));
}
