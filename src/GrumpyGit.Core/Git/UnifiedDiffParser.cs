using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Git;

/// <summary>
/// Parses unified diff output from git into a <see cref="ParsedDiff"/> suitable for
/// side-by-side rendering.  Removed and added lines within a hunk are paired together
/// (with empty padding lines on the shorter side) so both editors stay visually aligned.
/// Also populates structured <see cref="DiffHunk"/> objects for patch construction.
/// </summary>
public static class UnifiedDiffParser
{
    private static readonly Regex HunkHeaderRegex =
        new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.Compiled);

    public static ParsedDiff Parse(string unifiedDiff)
    {
        var leftLines          = new List<string>();
        var rightLines         = new List<string>();
        var leftColoredLines   = new List<int>();          // 1-based, red
        var rightColoredLines  = new List<int>();          // 1-based, green
        var hunkHeaderLines    = new List<int>();          // 1-based, applies to both
        var leftInlineRanges   = new List<DiffInlineRange>();
        var rightInlineRanges  = new List<DiffInlineRange>();

        // Structured hunk tracking
        var fileHeaderLines    = new List<string>();
        var hunks              = new List<DiffHunk>();
        var currentHunkLines   = new List<DiffLine>();
        int currentOldStart = 0, currentOldCount = 0, currentNewStart = 0, currentNewCount = 0;
        string currentHeaderLine = string.Empty;
        int currentHunkRenderedLine = 0;
        int oldLineNum = 0, newLineNum = 0;
        bool inHunk = false;

        var removedBuf = new List<string>();
        var addedBuf   = new List<string>();

        // Track raw diff lines corresponding to removed/added buffers for DiffLine creation
        var removedDiffLines = new List<DiffLine>();
        var addedDiffLines   = new List<DiffLine>();

        void FlushHunk()
        {
            if (!inHunk) return;
            hunks.Add(new DiffHunk
            {
                Index = hunks.Count,
                OldStart = currentOldStart,
                OldCount = currentOldCount,
                NewStart = currentNewStart,
                NewCount = currentNewCount,
                HeaderLine = currentHeaderLine,
                Lines = currentHunkLines.ToList(),
                RenderedLineNumber = currentHunkRenderedLine
            });
            currentHunkLines = new List<DiffLine>();
            inHunk = false;
        }

        void FlushBuffers()
        {
            if (removedBuf.Count == 0 && addedBuf.Count == 0)
                return;

            // Lines whose exact content appears in both buffers are "moved" rather than
            // truly added/removed -- don't highlight them.  Use frequency maps so that
            // if a line appears twice removed but once added, only one copy is exempt.
            var removedFreq = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var l in removedBuf)
                removedFreq[l] = removedFreq.GetValueOrDefault(l) + 1;

            var addedFreq = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var l in addedBuf)
                addedFreq[l] = addedFreq.GetValueOrDefault(l) + 1;

            // For each content, min(removedCount, addedCount) pairs are exempt.
            var exemptCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (content, rc) in removedFreq)
                if (addedFreq.TryGetValue(content, out var ac))
                    exemptCounts[content] = Math.Min(rc, ac);

            // Track separately so both sides can consume up to the exempt limit.
            var removedExemptUsed = new Dictionary<string, int>(StringComparer.Ordinal);
            var addedExemptUsed   = new Dictionary<string, int>(StringComparer.Ordinal);

            bool UseRemoved(string c)
            {
                if (!exemptCounts.TryGetValue(c, out var max)) return false;
                var used = removedExemptUsed.GetValueOrDefault(c);
                if (used >= max) return false;
                removedExemptUsed[c] = used + 1;
                return true;
            }

            bool UseAdded(string c)
            {
                if (!exemptCounts.TryGetValue(c, out var max)) return false;
                var used = addedExemptUsed.GetValueOrDefault(c);
                if (used >= max) return false;
                addedExemptUsed[c] = used + 1;
                return true;
            }

            int count = Math.Max(removedBuf.Count, addedBuf.Count);
            for (int i = 0; i < count; i++)
            {
                string? lc = i < removedBuf.Count ? removedBuf[i] : null;
                string? rc = i < addedBuf.Count   ? addedBuf[i]   : null;

                bool lColored = lc != null && !UseRemoved(lc);
                bool rColored = rc != null && !UseAdded(rc);

                leftLines.Add(lc ?? string.Empty);
                int leftLineNum = leftLines.Count;

                rightLines.Add(rc ?? string.Empty);
                int rightLineNum = rightLines.Count;

                if (lColored) leftColoredLines.Add(leftLineNum);
                if (rColored) rightColoredLines.Add(rightLineNum);

                // Inline diff: for paired non-empty changed lines, highlight just the
                // characters that differ rather than leaving the entire line coloured.
                if (lColored && rColored && lc!.Length > 0 && rc!.Length > 0)
                {
                    var (ls, ll, rs, rl) = FindInlineDiff(lc, rc);
                    if (ll > 0) leftInlineRanges.Add(new DiffInlineRange(leftLineNum,   ls, ll));
                    if (rl > 0) rightInlineRanges.Add(new DiffInlineRange(rightLineNum, rs, rl));
                }
            }

            // Set rendered line numbers on buffered DiffLines
            // Removed lines are rendered on the left side, added lines on the right side.
            // For the side-by-side view, both are at the same rendered positions.
            int baseRenderedLine = leftLines.Count - count + 1;
            for (int i = 0; i < removedDiffLines.Count; i++)
            {
                if (i < count)
                    removedDiffLines[i].RenderedLineNumber = baseRenderedLine + i;
            }
            for (int i = 0; i < addedDiffLines.Count; i++)
            {
                if (i < count)
                    addedDiffLines[i].RenderedLineNumber = baseRenderedLine + i;
            }

            removedBuf.Clear();
            addedBuf.Clear();
            removedDiffLines.Clear();
            addedDiffLines.Clear();
        }

        var rawLines = unifiedDiff.Split('\n');
        // Trim trailing empty entry produced by a final '\n' — it's not a real diff line
        int lineCount = rawLines.Length;
        while (lineCount > 0 && rawLines[lineCount - 1].TrimEnd('\r').Length == 0)
            lineCount--;

        for (int lineIdx = 0; lineIdx < lineCount; lineIdx++)
        {
            var line = rawLines[lineIdx].TrimEnd('\r');

            // File / index meta-headers -- collect for patch reconstruction
            if (line.StartsWith("diff ", StringComparison.Ordinal)
                || line.StartsWith("index ", StringComparison.Ordinal)
                || line.StartsWith("--- ", StringComparison.Ordinal)
                || line.StartsWith("+++ ", StringComparison.Ordinal)
                || line.StartsWith("new file mode", StringComparison.Ordinal)
                || line.StartsWith("deleted file mode", StringComparison.Ordinal)
                || line.StartsWith("rename ", StringComparison.Ordinal)
                || line.StartsWith("similarity ", StringComparison.Ordinal)
                || line.StartsWith("old mode", StringComparison.Ordinal)
                || line.StartsWith("new mode", StringComparison.Ordinal))
            {
                FlushBuffers();
                fileHeaderLines.Add(line);
                continue;
            }

            // Hunk header:  @@ -a,b +c,d @@
            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                FlushBuffers();
                FlushHunk();

                var match = HunkHeaderRegex.Match(line);
                if (match.Success)
                {
                    currentOldStart = int.Parse(match.Groups[1].Value);
                    currentOldCount = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 1;
                    currentNewStart = int.Parse(match.Groups[3].Value);
                    currentNewCount = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 1;
                }

                currentHeaderLine = line;
                oldLineNum = currentOldStart;
                newLineNum = currentNewStart;
                inHunk = true;

                hunkHeaderLines.Add(leftLines.Count + 1);
                currentHunkRenderedLine = leftLines.Count + 1;

                // The raw "@@ -a,b +c,d @@" text is machine-oriented noise in a
                // side-by-side view: both line numbers it encodes are already in the
                // gutters. The ROW is still emitted — it anchors the hunk staging
                // button and renders as the tinted band separating hunks — but with
                // no text. The real header is preserved on DiffHunk.HeaderLine, which
                // is what patch construction uses.
                leftLines.Add(string.Empty);
                rightLines.Add(string.Empty);
                continue;
            }

            if (line.StartsWith("-", StringComparison.Ordinal))
            {
                var content = line.Length > 1 ? line[1..] : string.Empty;
                removedBuf.Add(content);

                var diffLine = new DiffLine
                {
                    Type = DiffLineType.Removed,
                    Content = content,
                    OldLineNumber = oldLineNum
                };
                currentHunkLines.Add(diffLine);
                removedDiffLines.Add(diffLine);
                oldLineNum++;
                continue;
            }

            if (line.StartsWith("+", StringComparison.Ordinal))
            {
                var content = line.Length > 1 ? line[1..] : string.Empty;
                addedBuf.Add(content);

                var diffLine = new DiffLine
                {
                    Type = DiffLineType.Added,
                    Content = content,
                    NewLineNumber = newLineNum
                };
                currentHunkLines.Add(diffLine);
                addedDiffLines.Add(diffLine);
                newLineNum++;
                continue;
            }

            if (line.StartsWith(@"\", StringComparison.Ordinal))
            {
                // "\ No newline at end of file"
                var diffLine = new DiffLine
                {
                    Type = DiffLineType.NoNewlineMarker,
                    Content = line
                };
                currentHunkLines.Add(diffLine);
                continue;
            }

            if (line.StartsWith(" ", StringComparison.Ordinal) || line.Length == 0)
            {
                FlushBuffers();
                var content = line.Length > 1 ? line[1..] : string.Empty;
                leftLines.Add(content);
                rightLines.Add(content);

                if (inHunk)
                {
                    var diffLine = new DiffLine
                    {
                        Type = DiffLineType.Context,
                        Content = content,
                        OldLineNumber = oldLineNum,
                        NewLineNumber = newLineNum,
                        RenderedLineNumber = leftLines.Count
                    };
                    currentHunkLines.Add(diffLine);
                    oldLineNum++;
                    newLineNum++;
                }
                continue;
            }
        }

        FlushBuffers();
        FlushHunk();

        return new ParsedDiff(
            string.Join("\n", leftLines),
            string.Join("\n", rightLines),
            leftColoredLines,
            rightColoredLines,
            hunkHeaderLines,
            leftInlineRanges,
            rightInlineRanges,
            hunks,
            fileHeaderLines);
    }

    /// <summary>
    /// For untracked files where git diff returns nothing -- display raw content
    /// as all-added lines on the right with an empty left side.
    /// </summary>
    public static ParsedDiff ParseRawContent(string content)
    {
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var addedIndices = Enumerable.Range(1, lines.Count).ToList();

        // Left side: same number of empty lines so row heights stay aligned
        var leftText  = string.Join("\n", Enumerable.Repeat(string.Empty, lines.Count));
        var rightText = string.Join("\n", lines);

        return new ParsedDiff(
            leftText,
            rightText,
            Array.Empty<int>(),
            addedIndices,
            Array.Empty<int>());
    }

    /// <summary>
    /// Finds the outermost contiguous changed region between two strings using
    /// common-prefix / common-suffix matching.  Returns 0-based character offsets.
    /// </summary>
    private static (int leftStart, int leftLen, int rightStart, int rightLen)
        FindInlineDiff(string left, string right)
    {
        // Common prefix
        int prefixLen = 0;
        int minLen = Math.Min(left.Length, right.Length);
        while (prefixLen < minLen && left[prefixLen] == right[prefixLen])
            prefixLen++;

        // Common suffix (don't overlap with prefix)
        int suffixLen = 0;
        int leftRemaining  = left.Length  - prefixLen;
        int rightRemaining = right.Length - prefixLen;
        int maxSuffix = Math.Min(leftRemaining, rightRemaining);
        while (suffixLen < maxSuffix
               && left[left.Length   - 1 - suffixLen]
                  == right[right.Length - 1 - suffixLen])
            suffixLen++;

        return (
            prefixLen, left.Length  - prefixLen - suffixLen,
            prefixLen, right.Length - prefixLen - suffixLen
        );
    }
}
