using System;
using System.Collections.Generic;
using System.Text;

namespace GrumpyGit.Core.Terminal;

/// <summary>
/// One rendered row of terminal output: styled spans plus the plain text behind them.
///
/// Immutable, and re-created rather than mutated whenever the row changes. The renderer
/// holds the list of lines directly instead of copying them into view models, so an
/// immutable row is what makes it safe for it to keep drawing an old snapshot while the
/// reader thread is producing the next one.
/// </summary>
public sealed class TerminalLine
{
    public static readonly TerminalLine Empty = new(Array.Empty<TerminalSpan>());

    public IReadOnlyList<TerminalSpan> Spans { get; }

    /// <summary>The row without styling — used for clipboard copy and for tests.</summary>
    public string Text { get; }

    /// <summary>Character count, i.e. the column the row ends at.</summary>
    public int Length => Text.Length;

    public TerminalLine(IReadOnlyList<TerminalSpan> spans)
    {
        Spans = spans;

        if (spans.Count == 0)
        {
            Text = string.Empty;
            return;
        }

        if (spans.Count == 1)
        {
            Text = spans[0].Text;
            return;
        }

        var builder = new StringBuilder();
        foreach (var span in spans)
            builder.Append(span.Text);
        Text = builder.ToString();
    }
}
