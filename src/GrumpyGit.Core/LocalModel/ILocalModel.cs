namespace GrumpyGit.Core.LocalModel;

/// <summary>
/// A language model running inside this process.
///
/// The seam exists for two reasons. The obvious one is testing: every consumer can be
/// exercised against a fake that answers instantly, so no test needs a gigabyte of
/// weights. The other is the boundary itself — this is the only type allowed to hold a
/// native inference handle, in the same way <see cref="Git.IGitService"/> is the only
/// type allowed to launch git.
///
/// In-process by design. A model served over localhost would be just as private in
/// practice, but it would put an HTTP client in this codebase, and "local" would become a
/// setting someone could point elsewhere rather than a property of the build.
/// </summary>
public interface ILocalModel
{
    /// <summary>
    /// True when a model is loaded and <see cref="CompleteAsync"/> will answer. False
    /// before the first load, when no model file is configured, or after a load failed —
    /// the caller shows nothing rather than an error, since this is an optional feature.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Why the last load failed, or null if it has not. Exists because the alternative was
    /// worse than it looked: a model that will not load produces no review and no error, so
    /// a user whose machine cannot hold the weights they just downloaded sees an empty panel
    /// and no way to find out why.
    /// </summary>
    string? LoadError { get; }

    /// <summary>
    /// Loads the model if it is not loaded already. Safe to call repeatedly and from
    /// several callers at once; the work happens once. Returns false when no model is
    /// configured or the file could not be loaded.
    /// </summary>
    Task<bool> EnsureLoadedAsync(CancellationToken ct = default);

    /// <summary>
    /// Runs one completion. <paramref name="partial"/> receives text as it is generated so
    /// a caller can show the answer arriving rather than a spinner; it is optional.
    ///
    /// Implementations are not required to be safe against concurrent calls — callers
    /// serialise. <see cref="DiffReviewService"/> is the one that does.
    /// </summary>
    Task<string> CompleteAsync(
        ModelPrompt prompt,
        LocalModelOptions options,
        IProgress<string>? partial = null,
        CancellationToken ct = default);
}

/// <summary>
/// What to send the model, before any runtime's chat template is applied. Kept as two
/// parts rather than one blob because every local runtime wants the system turn
/// separately — pre-formatting them together would bake one model's template into a
/// prompt builder that has no business knowing about it.
/// </summary>
/// <param name="System">Standing instruction — the same for every review.</param>
/// <param name="User">The diff, rendered to fit the budget.</param>
public sealed record ModelPrompt(string System, string User);

/// <summary>
/// What to ask of one completion. Kept here rather than on the implementation so the
/// caller's intent — "short answer, low creativity" — survives a change of runtime.
/// </summary>
/// <param name="MaxTokens">
/// Ceiling on generated tokens. A review that runs long is a review nobody reads, and on
/// CPU every token is wall-clock the user is waiting for.
/// </param>
/// <param name="Temperature">
/// Low by default. This is a code review, not prose: the same diff should produce the
/// same reading twice, and invention is the failure mode that would kill the feature.
/// </param>
public sealed record LocalModelOptions(int MaxTokens = 400, float Temperature = 0.2f)
{
    public static readonly LocalModelOptions Review = new();

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
    public static LocalModelOptions ForReview(int changeCount) =>
        new(MaxTokens: Math.Clamp(160 + changeCount * 22, 400, 900));
}
