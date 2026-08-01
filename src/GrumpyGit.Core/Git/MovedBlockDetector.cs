using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Git;

/// <summary>
/// Finds runs of lines that were removed in one place and re-added in another.
///
/// A refactor that lifts a method to a different part of the file shows up in a plain
/// diff as a large deletion and an unrelated large insertion, and the reader has to
/// compare them by eye to discover nothing actually changed. Naming the pair collapses
/// that work: the block is marked moved, and attention goes to the edits that are real.
///
/// The parser already suppresses highlighting for content that appears on both sides of
/// a SINGLE hunk. This operates across the whole file, where the interesting moves are,
/// and reports the relationship rather than merely hiding it.
/// </summary>
public static class MovedBlockDetector
{
    /// <summary>
    /// Shortest run treated as a move. Below this, matches are dominated by structural
    /// filler — a lone <c>}</c> or <c>else</c> occurs everywhere and pairing them would
    /// report moves that mean nothing.
    /// </summary>
    public const int MinimumRunLength = 3;

    public static IReadOnlyList<MovedBlock> Detect(ParsedDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);

        var leftLines = diff.LeftText.Split('\n');
        var rightLines = diff.RightText.Split('\n');
        var headers = new HashSet<int>(diff.HunkHeaderLines);
        var rows = Math.Max(leftLines.Length, rightLines.Length);

        var removed = new List<(int Row, string Text)>();
        var added = new List<(int Row, string Text)>();

        for (var row = 1; row <= rows; row++)
        {
            if (headers.Contains(row)) continue;

            var left = row <= leftLines.Length ? leftLines[row - 1] : string.Empty;
            var right = row <= rightLines.Length ? rightLines[row - 1] : string.Empty;

            if (string.Equals(left, right, StringComparison.Ordinal)) continue;

            if (left.Length > 0) removed.Add((row, left));
            if (right.Length > 0) added.Add((row, right));
        }

        // Content -> positions within `added`, so a candidate start can be found without
        // rescanning the whole side for every removed line.
        var addedIndex = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < added.Count; i++)
        {
            var key = Normalise(added[i].Text);
            if (key.Length == 0) continue;
            if (!addedIndex.TryGetValue(key, out var list))
                addedIndex[key] = list = new List<int>();
            list.Add(i);
        }

        var blocks = new List<MovedBlock>();
        var removedConsumed = new bool[removed.Count];
        var addedConsumed = new bool[added.Count];

        for (var r = 0; r < removed.Count; r++)
        {
            if (removedConsumed[r]) continue;

            var seed = Normalise(removed[r].Text);
            if (seed.Length == 0) continue;
            if (!addedIndex.TryGetValue(seed, out var candidates)) continue;

            foreach (var start in candidates)
            {
                if (addedConsumed[start]) continue;

                // Extend while both sides keep matching AND stay contiguous in their own
                // row space. Without the contiguity check, unrelated lines separated by
                // other edits would be welded into one bogus "block".
                var length = 0;
                while (r + length < removed.Count
                       && start + length < added.Count
                       && !removedConsumed[r + length]
                       && !addedConsumed[start + length]
                       && Normalise(removed[r + length].Text) == Normalise(added[start + length].Text)
                       && (length == 0 || removed[r + length].Row == removed[r + length - 1].Row + 1)
                       && (length == 0 || added[start + length].Row == added[start + length - 1].Row + 1))
                {
                    length++;
                }

                if (length < MinimumRunLength) continue;

                for (var k = 0; k < length; k++)
                {
                    removedConsumed[r + k] = true;
                    addedConsumed[start + k] = true;
                }

                blocks.Add(new MovedBlock(removed[r].Row, added[start].Row, length));
                break;
            }
        }

        return blocks;
    }

    /// <summary>
    /// Compares on trimmed content: a moved block is nearly always re-indented at its
    /// destination, and treating that as a difference would miss the majority of real
    /// moves. Whitespace-only lines normalise to empty and are never used as a seed.
    /// </summary>
    private static string Normalise(string line) => line.Trim();
}
