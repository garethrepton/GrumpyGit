using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Rendering;

namespace GrumpyGit.App.Controls;

/// <summary>
/// Draws the replaced text as a translucent overlay on the line that replaced it.
///
/// Stacking old above new costs a row per edit and pushes the file apart; superimposing
/// them keeps the document at its true length and puts the comparison in one place, so
/// the difference is read rather than scanned for. Because the old text is drawn over the
/// new, the two are legible together only while the overlay stays faint — hence the low
/// opacity and the italic face, which separate the layers without a colour cue.
///
/// Drawn in the foreground layer: the point is that the ghost sits ON the current line,
/// not behind it as a background wash.
/// </summary>
internal sealed class GhostOverlayRenderer : IBackgroundRenderer
{
    private readonly IReadOnlyDictionary<int, string> _oldTextByLine;
    private readonly IBrush _brush;
    private readonly Typeface _typeface;
    private readonly double _fontSize;

    /// <summary>
    /// Foreground rather than Background: this is content, and it has to land on top of
    /// the line it is commenting on.
    /// </summary>
    public KnownLayer Layer => KnownLayer.Selection;

    public GhostOverlayRenderer(
        IReadOnlyDictionary<int, string> oldTextByLine,
        IBrush brush,
        Typeface typeface,
        double fontSize)
    {
        _oldTextByLine = oldTextByLine;
        _brush = brush;
        _typeface = typeface;
        _fontSize = fontSize;
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView.Document == null || _oldTextByLine.Count == 0) return;

        foreach (var visualLine in textView.VisualLines)
        {
            var lineNumber = visualLine.FirstDocumentLine.LineNumber;
            if (!_oldTextByLine.TryGetValue(lineNumber, out var oldText)) continue;
            if (string.IsNullOrWhiteSpace(oldText)) continue;

            foreach (var textLine in visualLine.TextLines)
            {
                var y = visualLine.GetTextLineVisualYPosition(textLine, VisualYPosition.TextTop)
                        - textView.ScrollOffset.Y;

                var formatted = new FormattedText(
                    oldText,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    _typeface,
                    _fontSize,
                    _brush);

                // Struck through as well as faded, so the overlay still reads as "removed"
                // if the two layers happen to align on similar glyphs.
                formatted.SetTextDecorations(TextDecorations.Strikethrough);

                drawingContext.DrawText(formatted, new Point(-textView.ScrollOffset.X, y));
                break;   // Only the first visual line of a wrapped row carries the ghost.
            }
        }
    }
}
