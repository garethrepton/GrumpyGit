using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Rendering;

namespace GrumpyGit.App.Controls;

/// <summary>
/// Paints lines belonging to a moved block in their own colour.
///
/// Runs as a separate renderer added AFTER the add/remove one so it draws over the top:
/// the lines of a move are, by construction, also marked as removed on one side and added
/// on the other, and the move is the more informative reading of the two.
/// </summary>
internal sealed class MovedBlockBackgroundRenderer : IBackgroundRenderer
{
    private readonly HashSet<int> _lines;
    private readonly IBrush _brush;

    public KnownLayer Layer => KnownLayer.Background;

    public MovedBlockBackgroundRenderer(IEnumerable<int> lines, IBrush brush)
    {
        _lines = new HashSet<int>(lines);
        _brush = brush;
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView.Document == null) return;

        foreach (var visualLine in textView.VisualLines)
        {
            if (!_lines.Contains(visualLine.FirstDocumentLine.LineNumber)) continue;

            foreach (var textLine in visualLine.TextLines)
            {
                var y = visualLine.GetTextLineVisualYPosition(textLine, VisualYPosition.TextTop)
                        - textView.ScrollOffset.Y;
                drawingContext.FillRectangle(
                    _brush, new Rect(0, y, textView.Bounds.Width, textLine.Height));
            }
        }
    }
}
