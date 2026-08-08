using System.Security.Cryptography;
using System.Text;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.LocalModel;

/// <summary>How a review turned out. The UI shows the first three and hides the fourth.</summary>
public enum DiffReviewState
{
    /// <summary>Text is present, whether just generated or served from the cache.</summary>
    Complete,

    /// <summary>Superseded before it finished — the user moved to another file.</summary>
    Cancelled,

    /// <summary>The model was reached and failed. Text carries the reason.</summary>
    Failed,

    /// <summary>No model configured or loadable. The feature is simply off.</summary>
    Unavailable,

    /// <summary>
    /// Too much changed to be worth asking. Distinct from a failure: nothing went wrong,
    /// the answer would just have been slow and vague.
    /// </summary>
    TooLarge,
}

/// <param name="Text">An explanation when <paramref name="State"/> is not Complete; otherwise empty.</param>
/// <param name="Result">The parsed review. <see cref="DiffReviewResult.Empty"/> unless Complete.</param>
public sealed record DiffReview(DiffReviewState State, string Text, DiffReviewResult Result)
{
    public static readonly DiffReview Unavailable =
        new(DiffReviewState.Unavailable, string.Empty, DiffReviewResult.Empty);

    public static readonly DiffReview Cancelled =
        new(DiffReviewState.Cancelled, string.Empty, DiffReviewResult.Empty);

    public static DiffReview Failed(string reason) =>
        new(DiffReviewState.Failed, reason, DiffReviewResult.Empty);

    public static readonly DiffReview TooLarge =
        new(DiffReviewState.TooLarge, string.Empty, DiffReviewResult.Empty);
}

/// <summary>
/// Reviews a diff with the local model, one at a time.
///
/// Every diff the user opens asks for a review, so this has to hold three lines at once:
/// a single inference runs at a time (a model context is not re-entrant), a review the
/// user has navigated away from must not hold the gate, and returning to a file already
/// reviewed must be instant.
///
/// The cache lives in memory and dies with the process. That is deliberate rather than
/// lazy: the key is derived from the user's own source, and the value is a description of
/// it — neither belongs in a file this application writes (commandment 9).
/// </summary>
public sealed class DiffReviewService
{
    private readonly ILocalModel _model;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();

    /// <summary>
    /// Last summary produced for each path, so a changeset reading can be handed what is
    /// already known about its files. Separate from <see cref="_cache"/>, which is keyed by
    /// the diff's content rather than its path.
    /// </summary>
    private readonly Dictionary<string, string> _summaryByPath = new(StringComparer.Ordinal);
    private readonly int _cacheCapacity;

    /// <summary>
    /// Beyond this many changed lines a file is declined rather than reviewed. Chosen to
    /// sit above the prompt budget: past this point the model is being shown a fraction of
    /// the change and asked to generalise, which is exactly when it starts inventing.
    /// </summary>
    public const int MaxChangedLines = 800;

    public DiffReviewService(ILocalModel model, int cacheCapacity = 200)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (cacheCapacity < 1)
            throw new ArgumentOutOfRangeException(nameof(cacheCapacity));

        _model = model;
        _cacheCapacity = cacheCapacity;
    }

    /// <summary>True when a review would be attempted at all — drives whether the panel appears.</summary>
    public bool IsAvailable => _model.IsReady;

    /// <summary>
    /// The review already held for this diff, or null. Lets a caller paint a cached answer
    /// in the same frame as the diff, instead of flashing "reviewing…" for something it
    /// already has.
    /// </summary>
    public DiffReviewResult? TryGetCached(string path, ParsedDiff diff)
    {
        var key = CacheKey(path, diff);
        lock (_cache)
        {
            // The model's raw reply is what is cached, and parsing happens on the way out.
            // A change to the parser then applies to everything already reviewed, instead
            // of needing the model run again to benefit from it.
            return _cache.TryGetValue(key, out var cached) ? DiffReviewParser.Parse(cached, diff) : null;
        }
    }

    /// <summary>
    /// Reviews one file's diff. Cancel <paramref name="ct"/> when the user moves on: a
    /// superseded request that has not reached the model never runs at all, and one that
    /// has stops between tokens.
    /// </summary>
    public async Task<DiffReview> ReviewAsync(
        string path,
        ParsedDiff diff,
        FileChangeSummary? summary = null,
        IProgress<string>? partial = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(diff);

        if (diff.Hunks.Count == 0)
            return DiffReview.Unavailable;

        // A wholesale rewrite is the worst case for this feature: slowest to generate,
        // most likely to be truncated by the prompt budget, and least likely to say
        // anything a reader could not see. Declining is a better answer than a minute of
        // CPU spent on a vague one.
        if (ChangedLineCount(diff) > MaxChangedLines)
            return DiffReview.TooLarge;

        var key = CacheKey(path, diff);
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached))
                return Complete(cached, diff);
        }

        if (!await _model.EnsureLoadedAsync(ct).ConfigureAwait(false))
            // "Unavailable" hides the panel, which is right when no model is configured and
            // wrong when one is and would not load — that user has a downloaded model and
            // an empty panel, and no way to find out which of the two they are looking at.
            return _model.LoadError is { Length: > 0 } error
                ? DiffReview.Failed(error)
                : DiffReview.Unavailable;

        // Queue behind whatever is generating. Cancellation is observed here too, so a
        // request the user has already navigated past leaves without taking its turn.
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return DiffReview.Cancelled;
        }

        try
        {
            // Re-check: while this request waited, the same diff may have been reviewed by
            // the request ahead of it — the user flicking to a file and back does exactly
            // that.
            lock (_cache)
            {
                if (_cache.TryGetValue(key, out var cached))
                    return Complete(cached, diff);
            }

            var prompt = DiffReviewPrompt.Build(path, diff, summary);
            var text = await _model
                .CompleteAsync(prompt, LocalModelOptions.ForReview(DiffNotebook.Split(diff).Count), partial, ct)
                .ConfigureAwait(false);

            text = text.Trim();
            if (text.Length == 0)
                return DiffReview.Failed("The model returned nothing.");

            var parsed = DiffReviewParser.Parse(text, diff);

            if (parsed.HasSummary)
            {
                lock (_cache)
                    _summaryByPath[path] = parsed.Summary;
            }

            // A reply that parses to nothing at all is a failed review, not an empty one:
            // the model ignored the format, and showing a blank panel would read as "this
            // diff is unremarkable" rather than "ask again".
            if (!parsed.HasSummary && parsed.ChangeNotes.Count == 0 && !parsed.HasIssues)
                return DiffReview.Failed("The model's reply could not be read.");

            Remember(key, text);
            return new DiffReview(DiffReviewState.Complete, string.Empty, parsed);
        }
        catch (OperationCanceledException)
        {
            return DiffReview.Cancelled;
        }
        catch (Exception ex)
        {
            // The message names the failure, never the diff — this string reaches the UI
            // and must not carry the user's source with it.
            return DiffReview.Failed(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reads a whole changeset — a commit, or everything in the working tree — for
    /// orientation rather than for defects. Shares the gate with per-file review, so it
    /// queues behind or ahead of one rather than competing with it for the model.
    /// </summary>
    /// <summary>
    /// The one-line summary already held for a path, if any file's review in the cache
    /// happens to be for it. Used to feed the changeset pass what is already known; a miss
    /// is the normal case and costs nothing.
    /// </summary>
    public string? TryGetCachedSummary(string path)
    {
        lock (_cache)
        {
            return _summaryByPath.TryGetValue(path, out var summary) ? summary : null;
        }
    }

    public async Task<ChangeSetReviewResult?> ReviewChangeSetAsync(
        string title,
        IReadOnlyList<ChangeSetFile> files,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count == 0)
            return null;

        var key = ChangeSetCacheKey(title, files);
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached))
                return ChangeSetReviewPrompt.Parse(cached, files);
        }

        if (!await _model.EnsureLoadedAsync(ct).ConfigureAwait(false))
            return null;

        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            lock (_cache)
            {
                if (_cache.TryGetValue(key, out var cached))
                    return ChangeSetReviewPrompt.Parse(cached, files);
            }

            var prompt = ChangeSetReviewPrompt.Build(title, files);
            var text = (await _model
                .CompleteAsync(prompt, LocalModelOptions.Review, null, ct)
                .ConfigureAwait(false)).Trim();

            if (text.Length == 0)
                return null;

            var parsed = ChangeSetReviewPrompt.Parse(text, files);
            if (!parsed.HasSummary && parsed.Watch.Count == 0)
                return null;

            Remember(key, text);
            return parsed;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Identity of a changeset reading: its title and the shape of every file in it. Two
    /// commits that touch the same paths by the same amounts genuinely deserve the same
    /// answer, and the title separates them when they do not.
    /// </summary>
    private static string ChangeSetCacheKey(string title, IReadOnlyList<ChangeSetFile> files)
    {
        var material = new StringBuilder();
        material.Append("cs").Append(ChangeSetReviewPrompt.Version).Append(' ').Append(title);

        foreach (var file in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            material.Append(' ').Append(file.Path)
                .Append('+').Append(file.Added)
                .Append('-').Append(file.Removed);

            if (file.KnownSummary is { Length: > 0 } known)
                material.Append('~').Append(known);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())));
    }

    private static int ChangedLineCount(ParsedDiff diff) =>
        diff.Hunks.Sum(h => h.Lines.Count(l => l.Type is DiffLineType.Added or DiffLineType.Removed));

    private static DiffReview Complete(string reply, ParsedDiff diff) =>
        new(DiffReviewState.Complete, string.Empty, DiffReviewParser.Parse(reply, diff));

    private void Remember(string key, string text)
    {
        lock (_cache)
        {
            if (_cache.ContainsKey(key))
                return;

            // Oldest-first eviction rather than true LRU: a session's worth of reviews is
            // small, and the failure mode of evicting a still-wanted entry is one repeated
            // inference, not a wrong answer.
            if (_cacheOrder.Count >= _cacheCapacity)
                _cache.Remove(_cacheOrder.Dequeue());

            _cache[key] = text;
            _cacheOrder.Enqueue(key);
        }
    }

    /// <summary>
    /// Identity of a review: the file, its changed lines, and the prompt that produced it.
    /// Hashed rather than kept whole so the cache does not hold a second copy of the
    /// user's source in memory for as long as the session lasts.
    /// </summary>
    private static string CacheKey(string path, ParsedDiff diff)
    {
        var material = new StringBuilder();
        material.Append(DiffReviewPrompt.Version).Append(' ').Append(path);

        foreach (var hunk in diff.Hunks)
        {
            material.Append(' ').Append(hunk.HeaderLine);
            foreach (var line in hunk.Lines)
            {
                if (line.Type is DiffLineType.Added or DiffLineType.Removed)
                    material.Append(' ').Append((int)line.Type).Append(line.Content);
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString())));
    }
}
