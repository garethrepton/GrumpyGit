using System.Globalization;
using System.Text;

namespace GrumpyGit.Core.LocalModel;

/// <summary>One file's contribution to a changeset, as the orientation prompt sees it.</summary>
/// <param name="Path">Repo-relative path.</param>
/// <param name="Added">Lines added.</param>
/// <param name="Removed">Lines removed.</param>
/// <param name="Symbols">Declarations git's hunk headers named, if any.</param>
/// <param name="KnownSummary">
/// A per-file review already in the cache, if there is one. Free quality: the changeset
/// reading gets to see what the model previously concluded about a file rather than
/// guessing from its name and line counts.
/// </param>
public sealed record ChangeSetFile(
    string Path,
    int Added,
    int Removed,
    IReadOnlyList<string> Symbols,
    string? KnownSummary = null);

/// <summary>Something the model thinks is worth a closer look, and where.</summary>
public sealed record WatchItem(string Path, string Text);

/// <summary>
/// The model's reading of a whole commit or working tree: what it does, how much care it
/// wants, and which files to start with.
/// </summary>
public sealed record ChangeSetReviewResult(
    string Summary,
    ReviewRisk Risk,
    IReadOnlyList<WatchItem> Watch)
{
    public static readonly ChangeSetReviewResult Empty = new(string.Empty, ReviewRisk.None, []);

    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
}

/// <summary>
/// Builds the prompt for a whole changeset, and reads the reply back.
///
/// This is an <em>orientation</em> pass, not a code review: it is given the shape of the
/// change — files, churn, symbol names, and any per-file readings already cached — rather
/// than the diffs themselves. That is a deliberate trade. A commit's full text does not
/// fit a small model's context, and feeding it a truncated tenth of the change and asking
/// for a verdict is how you get confident nonsense. What it can do honestly from the shape
/// is say what the change appears to be about and which files carry the weight.
///
/// Per-file review — <see cref="DiffReviewPrompt"/> — is where actual code is read, and
/// "Review all" is where every file gets that treatment.
/// </summary>
public static class ChangeSetReviewPrompt
{
    /// <summary>Part of the cache key; bump when the wording changes.</summary>
    public const int Version = 1;

    /// <summary>
    /// Enough files to characterise a change, few enough to leave the model room to answer.
    /// A commit touching more than this is described by its largest files plus a count.
    /// </summary>
    public const int MaxFilesListed = 40;

    private const string SystemInstruction =
        """
        You are orienting a reviewer who is about to read a set of changes. You are given
        the files it touches, how much each changed, and the declarations involved — not
        the code itself.

        Reply using only these lines, in this order, and write nothing else:

        SUMMARY: one or two sentences on what this change appears to be about.
        RISK: none, caution or danger.
        WATCH <path>: why that file is worth reading first.

        Give at most three WATCH lines, for the files that carry the most weight or look
        riskiest — migrations, deletions, security or authentication code, configuration,
        anything with unusually large churn. Use only paths you were given. Do not guess at
        what the code says; you have not been shown it. If nothing stands out, write no
        WATCH lines and say RISK: none.
        """;

    public static ModelPrompt Build(string title, IReadOnlyList<ChangeSetFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var user = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(title))
            user.Append("Change: ").AppendLine(title);

        var totalAdded = files.Sum(f => f.Added);
        var totalRemoved = files.Sum(f => f.Removed);
        user.Append(files.Count).Append(" file(s), +").Append(totalAdded)
            .Append(" −").Append(totalRemoved).AppendLine();
        user.AppendLine();

        // Largest first: if the list has to be cut, what survives is what matters most.
        var listed = files
            .OrderByDescending(f => f.Added + f.Removed)
            .Take(MaxFilesListed)
            .ToList();

        foreach (var file in listed)
        {
            user.Append(file.Path)
                .Append("  +").Append(file.Added)
                .Append(" −").Append(file.Removed);

            if (file.Symbols.Count > 0)
                user.Append("  [").Append(string.Join(", ", file.Symbols.Take(6))).Append(']');

            user.AppendLine();

            if (!string.IsNullOrWhiteSpace(file.KnownSummary))
                user.Append("    already reviewed: ").AppendLine(file.KnownSummary);
        }

        if (files.Count > listed.Count)
            user.Append("(and ").Append(files.Count - listed.Count).AppendLine(" smaller file(s) not listed)");

        return new ModelPrompt(SystemInstruction, user.ToString());
    }

    /// <summary>
    /// Reads the reply. Same forgiving approach as <see cref="DiffReviewParser"/>: take
    /// what parses, drop what does not, and refuse to attach a note to a file that was
    /// never in the change.
    /// </summary>
    public static ChangeSetReviewResult Parse(string reply, IReadOnlyList<ChangeSetFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (string.IsNullOrWhiteSpace(reply))
            return ChangeSetReviewResult.Empty;

        var known = files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var summary = new List<string>();
        var risk = ReviewRisk.None;
        var watch = new List<WatchItem>();

        foreach (var raw in reply.Split('\n'))
        {
            var line = raw.Trim().TrimStart('-', '*', '#', ' ').Replace("**", string.Empty);
            if (line.Length == 0) continue;

            if (line.StartsWith("SUMMARY:", StringComparison.OrdinalIgnoreCase))
            {
                var text = line["SUMMARY:".Length..].Trim();
                if (text.Length > 0) summary.Add(text);
                continue;
            }

            if (line.StartsWith("RISK:", StringComparison.OrdinalIgnoreCase))
            {
                risk = ParseRisk(line["RISK:".Length..], risk);
                continue;
            }

            if (!line.StartsWith("WATCH", StringComparison.OrdinalIgnoreCase)) continue;
            if (watch.Count >= 3) continue;

            var rest = line["WATCH".Length..];
            var colon = rest.IndexOf(':');
            if (colon <= 0) continue;

            var path = rest[..colon].Trim();
            var why = rest[(colon + 1)..].Trim();

            // A note about a file the change does not contain has nowhere to point, and
            // would send the reader looking for something that is not there.
            if (why.Length == 0 || !known.Contains(path)) continue;

            watch.Add(new WatchItem(path, why));
        }

        return new ChangeSetReviewResult(string.Join(" ", summary).Trim(), risk, watch);
    }

    private static ReviewRisk ParseRisk(string text, ReviewRisk fallback)
    {
        var value = text.Trim().TrimEnd('.').ToLower(CultureInfo.InvariantCulture);
        if (value.Contains("danger")) return ReviewRisk.Danger;
        if (value.Contains("caution")) return ReviewRisk.Caution;
        if (value.Contains("none")) return ReviewRisk.None;
        return fallback;
    }
}
