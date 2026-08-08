using System.Globalization;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.LocalModel;

/// <summary>
/// Reads the model's reply back into a <see cref="DiffReviewResult"/>.
///
/// Deliberately forgiving. A small model will wrap its answer in a sentence, repeat a
/// label, number a hunk that does not exist, or cite a line it was never shown. None of
/// that should cost the user the whole review, so every rule here is "take what parses,
/// drop what does not" — with one exception: a line number that cannot be mapped into the
/// rendered diff is kept as text but anchored nowhere, because an issue pointing at the
/// wrong line is worse than one pointing at nothing.
/// </summary>
public static class DiffReviewParser
{
    /// <summary>
    /// Enough for a thorough file, few enough that a model stuck in a loop cannot fill the
    /// panel with repetitions of one thought.
    /// </summary>
    private const int MaxIssues = 20;

    public static DiffReviewResult Parse(string reply, ParsedDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);

        if (string.IsNullOrWhiteSpace(reply))
            return DiffReviewResult.Empty;

        reply = WithoutReasoning(reply);
        if (string.IsNullOrWhiteSpace(reply))
            return DiffReviewResult.Empty;

        var newLineToRendered = MapNewFileLines(diff);

        // The same segmentation the prompt numbered from, so "CHANGE 7" means the seventh
        // block here too. Recounting would be one definition of a change too many.
        var blocks = DiffNotebook.Split(diff);

        var summary = new List<string>();
        var risk = ReviewRisk.None;
        var issues = new List<ReviewIssue>();
        var notes = new Dictionary<int, ChangeNote>();
        var concerns = new Dictionary<int, ChangeConcern>();

        foreach (var raw in reply.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;

            // Models like to decorate labels — "**SUMMARY:**", "- SUMMARY:". Strip the
            // decoration rather than failing to recognise the line.
            line = line.TrimStart('-', '*', '#', ' ').Replace("**", string.Empty);

            if (TryTakeLabel(line, "SUMMARY:", out var summaryText))
            {
                if (summaryText.Length > 0)
                    summary.Add(summaryText);
                continue;
            }

            if (TryTakeLabel(line, "RISK:", out var riskText))
            {
                risk = ParseRisk(riskText, risk);
                continue;
            }

            if (TryTakeNumbered(line, "ISSUE", out var issueLine, out var issueText))
            {
                if (issues.Count >= MaxIssues || issueText.Length == 0)
                    continue;

                newLineToRendered.TryGetValue(issueLine, out var rendered);
                issues.Add(new ReviewIssue(issueLine, rendered, issueText));
                continue;
            }

            if (TryTakeNumbered(line, "TOUCHES", out var touched, out var touchText))
            {
                if (touched < 1 || touched > blocks.Count)
                    continue;

                // An unrecognised leading word means the model wrote prose where a category
                // was asked for. Dropped rather than filed under "other": a badge saying
                // something is consequential without saying how is a reason to stop with
                // nothing to look at.
                var kind = ParseConcern(touchText, out var detail);
                if (kind == ConcernKind.None)
                    continue;

                concerns.TryAdd(touched, new ChangeConcern(touched, kind, detail));
                continue;
            }

            // "HUNK" is still accepted. The label changed with prompt version 4, and a
            // small model that has seen a million diffs will reach for the older word
            // regardless of what it was asked for; dropping those lines would cost a
            // perfectly good note over vocabulary.
            if (TryTakeNumbered(line, "CHANGE", out var number, out var noteText)
                || TryTakeNumbered(line, "HUNK", out number, out noteText))
            {
                // A note for a change that was never shown has nowhere to be drawn.
                if (noteText.Length == 0 || number < 1 || number > blocks.Count)
                    continue;

                // First answer wins: a repeated label is a model looping, not a correction.
                notes.TryAdd(number, new ChangeNote(number, blocks[number - 1].StartRenderedLine, noteText));
            }
        }

        return new DiffReviewResult(
            string.Join(" ", summary).Trim(),
            risk,
            issues,
            notes.Values.OrderBy(n => n.ChangeNumber).ToList(),
            concerns.Values.OrderBy(c => c.ChangeNumber).ToList());
    }

    /// <summary>
    /// Reads the leading category word of a TOUCHES line, and returns the rest as detail.
    ///
    /// Matched on a prefix rather than an exact word because a small model asked for
    /// "files" writes "files:", "files —" and "Files" in roughly equal measure, and the
    /// category is the part that has to be right.
    /// </summary>
    private static ConcernKind ParseConcern(string text, out string detail)
    {
        var trimmed = text.TrimStart('-', '*', ' ');
        detail = trimmed;

        foreach (var (word, kind) in ConcernWords)
        {
            if (!trimmed.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                continue;

            detail = trimmed[word.Length..].TrimStart(':', '-', '—', ' ').Trim();
            return kind;
        }

        return ConcernKind.None;
    }

    /// <summary>
    /// Longest first, so "data-loss" is not read as nothing when "data" is not a category
    /// and "files" is not shadowed by a shorter prefix.
    /// </summary>
    private static readonly (string Word, ConcernKind Kind)[] ConcernWords =
    [
        ("credentials", ConcernKind.Credentials),
        ("credential", ConcernKind.Credentials),
        ("data-loss", ConcernKind.DataLoss),
        ("data loss", ConcernKind.DataLoss),
        ("network", ConcernKind.Network),
        ("process", ConcernKind.Process),
        ("files", ConcernKind.Files),
        ("file", ConcernKind.Files),
    ];

    /// <summary>
    /// Drops a leading <c>&lt;think&gt;…&lt;/think&gt;</c> block.
    ///
    /// The Qwen3 models reason before answering, and their reasoning is prose about the
    /// diff — exactly the shape that makes the forgiving rules above pick up a stray
    /// "SUMMARY" or a line number out of the model's working-out. The prompt asks them not
    /// to, and this is what happens when they do it anyway.
    ///
    /// An unclosed block means the token budget ran out mid-thought: there is no answer
    /// after it, so the whole reply goes.
    /// </summary>
    private static string WithoutReasoning(string reply)
    {
        const string open = "<think>";
        const string close = "</think>";

        var start = reply.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return reply;

        var end = reply.IndexOf(close, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return string.Empty;

        return string.Concat(reply.AsSpan(0, start), reply.AsSpan(end + close.Length));
    }

    /// <summary>
    /// New-file line number to line in the rendered diff document. Only added lines have a
    /// new-file number, which is also the only kind of line the prompt numbers — so the
    /// model can only cite lines that appear here.
    /// </summary>
    private static Dictionary<int, int> MapNewFileLines(ParsedDiff diff)
    {
        var map = new Dictionary<int, int>();

        foreach (var hunk in diff.Hunks)
        {
            foreach (var line in hunk.Lines)
            {
                if (line.Type != DiffLineType.Added) continue;
                if (line.NewLineNumber <= 0 || line.RenderedLineNumber <= 0) continue;
                map.TryAdd(line.NewLineNumber, line.RenderedLineNumber);
            }
        }

        return map;
    }

    private static bool TryTakeLabel(string line, string label, out string text)
    {
        if (line.StartsWith(label, StringComparison.OrdinalIgnoreCase))
        {
            text = line[label.Length..].Trim();
            return true;
        }

        text = string.Empty;
        return false;
    }

    /// <summary>Matches <c>ISSUE 42: text</c> and <c>HUNK 3: text</c>, tolerating stray punctuation.</summary>
    private static bool TryTakeNumbered(string line, string label, out int number, out string text)
    {
        number = 0;
        text = string.Empty;

        if (!line.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = line[label.Length..];
        var colon = rest.IndexOf(':');
        if (colon < 0)
            return false;

        var numberPart = rest[..colon].Trim().TrimStart('#').Trim();
        if (!int.TryParse(numberPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return false;

        text = rest[(colon + 1)..].Trim();
        return true;
    }

    private static ReviewRisk ParseRisk(string text, ReviewRisk fallback)
    {
        var value = text.Trim().TrimEnd('.').ToLowerInvariant();

        // Contains rather than equals: "caution — the guard moved" is a common shape, and
        // the qualifier does not change the verdict.
        if (value.Contains("danger")) return ReviewRisk.Danger;
        if (value.Contains("caution")) return ReviewRisk.Caution;
        if (value.Contains("none")) return ReviewRisk.None;
        return fallback;
    }
}
