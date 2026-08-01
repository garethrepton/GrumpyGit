using System.Collections.Generic;

namespace GrumpyGit.Core.Models;

/// <summary>Character-range highlight within a single line of the diff.</summary>
/// <param name="Line">1-based line number in the document.</param>
/// <param name="Start">0-based character offset within the line.</param>
/// <param name="Length">Number of characters to highlight.</param>
public sealed record DiffInlineRange(int Line, int Start, int Length);

/// <summary>
/// Holds the parsed left/right sides of a unified diff, with aligned line counts
/// (padding empty lines are inserted so that removed/added blocks stay visually aligned).
/// </summary>
public sealed class ParsedDiff
{
    /// <summary>Full text for the left (old) editor — lines separated by '\n'.</summary>
    public string LeftText { get; }

    /// <summary>Full text for the right (new) editor — lines separated by '\n'.</summary>
    public string RightText { get; }

    /// <summary>1-based line numbers in the left document that should have a red background (removed lines).</summary>
    public IReadOnlyList<int> LeftColoredLines { get; }

    /// <summary>1-based line numbers in the right document that should have a green background (added lines).</summary>
    public IReadOnlyList<int> RightColoredLines { get; }

    /// <summary>1-based line numbers (same in both documents) for diff/index/@@ header lines.</summary>
    public IReadOnlyList<int> HunkHeaderLines { get; }

    /// <summary>Character-level changed ranges within left (removed) lines — drawn with a brighter red.</summary>
    public IReadOnlyList<DiffInlineRange> LeftInlineRanges { get; }

    /// <summary>Character-level changed ranges within right (added) lines — drawn with a brighter green.</summary>
    public IReadOnlyList<DiffInlineRange> RightInlineRanges { get; }

    /// <summary>Structured hunk objects parsed from the unified diff.</summary>
    public IReadOnlyList<DiffHunk> Hunks { get; }

    /// <summary>Raw file header lines (diff --git, index, ---, +++, mode lines) needed for patch construction.</summary>
    public IReadOnlyList<string> FileHeaderLines { get; }

    public ParsedDiff(
        string leftText,
        string rightText,
        IReadOnlyList<int> leftColoredLines,
        IReadOnlyList<int> rightColoredLines,
        IReadOnlyList<int> hunkHeaderLines,
        IReadOnlyList<DiffInlineRange>? leftInlineRanges = null,
        IReadOnlyList<DiffInlineRange>? rightInlineRanges = null,
        IReadOnlyList<DiffHunk>? hunks = null,
        IReadOnlyList<string>? fileHeaderLines = null)
    {
        LeftText = leftText;
        RightText = rightText;
        LeftColoredLines = leftColoredLines;
        RightColoredLines = rightColoredLines;
        HunkHeaderLines = hunkHeaderLines;
        LeftInlineRanges = leftInlineRanges ?? [];
        RightInlineRanges = rightInlineRanges ?? [];
        Hunks = hunks ?? [];
        FileHeaderLines = fileHeaderLines ?? [];
    }
}
