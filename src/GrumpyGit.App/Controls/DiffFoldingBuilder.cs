using System;
using System.Collections.Generic;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using GrumpyGit.Core.Models;

namespace GrumpyGit.App.Controls;

/// <summary>
/// Builds fold regions over the long stretches of unchanged code that full-file mode
/// produces.
///
/// Showing the whole file is what makes a change reviewable in context, but a 2000-line
/// file with three edits is mostly noise. Folding the untouched runs keeps the context
/// available without making the reader scroll through it.
/// </summary>
public static class DiffFoldingBuilder
{
    /// <summary>
    /// Unchanged lines to leave visible on each side of a change. Matches the usual
    /// diff context so a folded view still reads like a normal hunk.
    /// </summary>
    public const int KeptContextLines = 3;

    /// <summary>
    /// Shortest run worth folding. Below this the fold marker takes as much room as the
    /// lines it hides, so it costs the reader attention for nothing.
    /// </summary>
    public const int MinimumFoldableRun = 6;

    /// <summary>
    /// Computes foldable regions for a document.
    ///
    /// Both editors are folded on the SAME line ranges rather than each side computing
    /// its own. <see cref="ParsedDiff"/> pads both sides to equal length so they stay
    /// aligned; folding them independently would break that alignment and the two panes
    /// would drift apart as regions collapse.
    /// </summary>
    /// <param name="document">Either editor's document — both have the same line count.</param>
    /// <param name="changedLines">1-based line numbers that differ on either side.</param>
    public static IReadOnlyList<NewFolding> Build(TextDocument document, IReadOnlySet<int> changedLines)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(changedLines);

        var foldings = new List<NewFolding>();
        var lineCount = document.LineCount;
        if (lineCount == 0) return foldings;

        // Walk the document, treating any line within KeptContextLines of a change as
        // "must stay visible"; everything else is a candidate for folding.
        var runStart = -1;

        for (var line = 1; line <= lineCount + 1; line++)
        {
            var mustShow = line > lineCount || IsNearChange(line, changedLines);

            if (!mustShow)
            {
                if (runStart < 0) runStart = line;
                continue;
            }

            if (runStart >= 0)
            {
                TryAddFolding(document, foldings, runStart, line - 1);
                runStart = -1;
            }
        }

        return foldings;
    }

    private static bool IsNearChange(int line, IReadOnlySet<int> changedLines)
    {
        for (var offset = -KeptContextLines; offset <= KeptContextLines; offset++)
        {
            if (changedLines.Contains(line + offset))
                return true;
        }
        return false;
    }

    private static void TryAddFolding(
        TextDocument document, List<NewFolding> foldings, int firstLine, int lastLine)
    {
        var length = lastLine - firstLine + 1;
        if (length < MinimumFoldableRun) return;

        var start = document.GetLineByNumber(firstLine);
        var end = document.GetLineByNumber(lastLine);

        foldings.Add(new NewFolding(start.Offset, end.EndOffset)
        {
            Name = $" ⋯ {length} unchanged lines ",
            // Closed by default: the point is to hide them until asked for.
            DefaultClosed = true,
        });
    }
}
