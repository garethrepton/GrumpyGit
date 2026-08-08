using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using GrumpyGit.Core.Models;

namespace GrumpyGit.App.Controls;

/// <summary>
/// AvaloniaEdit background renderer that paints:
///   • removed lines with a red tint, added lines with a green tint
///   • within paired changed lines, a brighter highlight on just the changed characters
///   • hunk/file header lines with a muted blue tint
///   • lines the local model flagged as a problem, with a warning wash over the top
/// </summary>
internal sealed class DiffLineBackgroundRenderer : IBackgroundRenderer
{
    private readonly HashSet<int> _coloredLines;
    private readonly IBrush _colorBrush;
    private readonly IBrush _inlineBrush;
    private readonly HashSet<int> _hunkLines;
    private readonly IBrush _hunkBrush;
    private readonly HashSet<int> _warningLines;
    private readonly IBrush _warningBrush;

    // Inline ranges keyed by 1-based line number for fast lookup during Draw
    private readonly Dictionary<int, List<DiffInlineRange>> _inlineByLine;

    public KnownLayer Layer => KnownLayer.Background;

    public DiffLineBackgroundRenderer(
        IReadOnlyList<int> coloredLines,
        IBrush colorBrush,
        IBrush inlineBrush,
        IReadOnlyList<int> hunkLines,
        IBrush hunkBrush,
        IReadOnlyList<DiffInlineRange> inlineRanges,
        IReadOnlyList<int>? warningLines = null,
        IBrush? warningBrush = null)
    {
        _coloredLines  = new HashSet<int>(coloredLines);
        _colorBrush    = colorBrush;
        _inlineBrush   = inlineBrush;
        _hunkLines     = new HashSet<int>(hunkLines);
        _hunkBrush     = hunkBrush;
        _warningLines  = warningLines is null ? [] : new HashSet<int>(warningLines);
        _warningBrush  = warningBrush ?? Brushes.Transparent;

        _inlineByLine = inlineRanges
            .GroupBy(r => r.Line)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (textView.Document == null) return;

        foreach (var visualLine in textView.VisualLines)
        {
            var lineNum = visualLine.FirstDocumentLine.LineNumber;
            IBrush? brush = null;

            if (_coloredLines.Contains(lineNum))
                brush = _colorBrush;
            else if (_hunkLines.Contains(lineNum))
                brush = _hunkBrush;

            if (brush != null)
            {
                foreach (var textLine in visualLine.TextLines)
                {
                    var yPos = visualLine.GetTextLineVisualYPosition(textLine, VisualYPosition.TextTop)
                               - textView.ScrollOffset.Y;
                    drawingContext.FillRectangle(
                        brush,
                        new Rect(0, yPos, textView.Bounds.Width, textLine.Height));
                }
            }

            // Warning wash goes on top of whatever the line already had. Painted over
            // rather than instead of the add/remove tint, so a flagged line still reads as
            // an addition — losing that would be trading one signal for another.
            if (_warningLines.Contains(lineNum))
            {
                foreach (var textLine in visualLine.TextLines)
                {
                    var yPos = visualLine.GetTextLineVisualYPosition(textLine, VisualYPosition.TextTop)
                               - textView.ScrollOffset.Y;
                    drawingContext.FillRectangle(
                        _warningBrush,
                        new Rect(0, yPos, textView.Bounds.Width, textLine.Height));
                }
            }

            // Inline highlight — only for lines that also have a full-line colour
            if (brush != null && _inlineByLine.TryGetValue(lineNum, out var ranges))
            {
                var docLine = textView.Document.GetLineByNumber(lineNum);

                foreach (var range in ranges)
                {
                    int startOffset = docLine.Offset + range.Start;
                    int endOffset   = Math.Min(startOffset + range.Length, docLine.EndOffset);
                    if (startOffset >= endOffset) continue;

                    var segment = new SimpleSegment(startOffset, endOffset - startOffset);
                    foreach (var rect in BackgroundGeometryBuilder.GetRectsForSegment(textView, segment))
                        drawingContext.FillRectangle(_inlineBrush, rect);
                }
            }
        }
    }
}
