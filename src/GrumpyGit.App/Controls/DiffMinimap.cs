using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using GrumpyGit.Core.Models;

namespace GrumpyGit.App.Controls;

/// <summary>
/// A compressed overview of where the changes are in a file.
///
/// In full-file mode a diff can be thousands of lines with a handful of changed
/// regions; a scrollbar tells you nothing about where they are. This maps the whole
/// document onto one narrow strip so every change is visible at once, and lets the
/// user click or drag to jump straight to one.
/// </summary>
public class DiffMinimap : Control
{
    /// <summary>Minimum drawn height of a change marker, so single-line changes stay visible.</summary>
    private const double MinMarkerHeight = 2.0;

    private const double ViewportBorderWidth = 1.0;

    public static readonly StyledProperty<ParsedDiff?> DiffProperty =
        AvaloniaProperty.Register<DiffMinimap, ParsedDiff?>(nameof(Diff));

    /// <summary>Total lines in the rendered diff document.</summary>
    public static readonly StyledProperty<int> TotalLinesProperty =
        AvaloniaProperty.Register<DiffMinimap, int>(nameof(TotalLines), 1);

    /// <summary>First line currently visible in the editor (1-based).</summary>
    public static readonly StyledProperty<int> ViewportFirstLineProperty =
        AvaloniaProperty.Register<DiffMinimap, int>(nameof(ViewportFirstLine), 1);

    /// <summary>Number of lines visible in the editor.</summary>
    public static readonly StyledProperty<int> ViewportLineCountProperty =
        AvaloniaProperty.Register<DiffMinimap, int>(nameof(ViewportLineCount), 1);

    public ParsedDiff? Diff
    {
        get => GetValue(DiffProperty);
        set => SetValue(DiffProperty, value);
    }

    public int TotalLines
    {
        get => GetValue(TotalLinesProperty);
        set => SetValue(TotalLinesProperty, value);
    }

    public int ViewportFirstLine
    {
        get => GetValue(ViewportFirstLineProperty);
        set => SetValue(ViewportFirstLineProperty, value);
    }

    public int ViewportLineCount
    {
        get => GetValue(ViewportLineCountProperty);
        set => SetValue(ViewportLineCountProperty, value);
    }

    /// <summary>Raised with a 1-based target line when the user clicks or drags the strip.</summary>
    public event EventHandler<int>? LineRequested;

    static DiffMinimap()
    {
        AffectsRender<DiffMinimap>(
            DiffProperty, TotalLinesProperty, ViewportFirstLineProperty, ViewportLineCountProperty);
    }

    // ── Interaction ───────────────────────────────────────────────────────────

    private bool _dragging;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragging = true;
        e.Pointer.Capture(this);
        RequestLineAt(e.GetPosition(this).Y);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
            RequestLineAt(e.GetPosition(this).Y);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    /// <summary>
    /// Converts a Y position on the strip to a document line, centring the viewport on
    /// it so the click lands in the middle of the view rather than at its top edge.
    /// </summary>
    private void RequestLineAt(double y)
    {
        var height = Bounds.Height;
        if (height <= 0) return;

        var total = Math.Max(1, TotalLines);
        var fraction = Math.Clamp(y / height, 0.0, 1.0);
        var target = (int)Math.Round(fraction * total);

        var centred = target - ViewportLineCount / 2;
        LineRequested?.Invoke(this, Math.Clamp(centred, 1, total));
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        context.FillRectangle(
            ThemeTokens.Brush("GutterBgBrush", Brushes.Black), new Rect(0, 0, width, height));

        var diff = Diff;
        if (diff is null) return;

        var total = Math.Max(1, TotalLines);
        var scale = height / total;

        // Removals on the left half, additions on the right, so a line that is both
        // (a modification) reads as a single bar spanning the full width.
        var half = width / 2.0;

        DrawMarkers(context, diff.LeftColoredLines, ThemeTokens.Brush("DeleteFgBrush", Brushes.IndianRed),
            x: 0, markerWidth: half, scale: scale, height: height);

        DrawMarkers(context, diff.RightColoredLines, ThemeTokens.Brush("AddFgBrush", Brushes.MediumSeaGreen),
            x: half, markerWidth: half, scale: scale, height: height);

        DrawViewport(context, width, height, total, scale);
    }

    private static void DrawMarkers(
        DrawingContext context, IReadOnlyList<int> lines, IBrush brush,
        double x, double markerWidth, double scale, double height)
    {
        if (lines.Count == 0) return;

        // Merge consecutive lines into one rectangle: drawing 2000 separate 0.3px
        // rectangles is both slow and visually noisier than the blocks they form.
        var runStart = -1;
        var previous = int.MinValue;

        foreach (var line in lines)
        {
            if (runStart < 0)
            {
                runStart = line;
            }
            else if (line != previous + 1)
            {
                EmitRun(context, brush, x, markerWidth, scale, height, runStart, previous);
                runStart = line;
            }

            previous = line;
        }

        if (runStart >= 0)
            EmitRun(context, brush, x, markerWidth, scale, height, runStart, previous);
    }

    private static void EmitRun(
        DrawingContext context, IBrush brush, double x, double markerWidth,
        double scale, double height, int firstLine, int lastLine)
    {
        var top = (firstLine - 1) * scale;
        var rawHeight = (lastLine - firstLine + 1) * scale;
        var markerHeight = Math.Max(MinMarkerHeight, rawHeight);

        // Keep the marker inside the strip when clamping pushed it past the bottom.
        if (top + markerHeight > height)
            top = Math.Max(0, height - markerHeight);

        context.FillRectangle(brush, new Rect(x, top, markerWidth, markerHeight));
    }

    private void DrawViewport(DrawingContext context, double width, double height, int total, double scale)
    {
        if (ViewportLineCount <= 0 || ViewportLineCount >= total)
            return;

        // Reserve room for the box before clamping `top`, so the clamp below can never be
        // handed a min above its max — Math.Clamp throws on that rather than saturating.
        // The editor reports a first visible line past the normal last-page start when the
        // horizontal scrollbar appears (scrolling right to the end of a long line adds
        // scrollable height), which drove `top` to within a pixel of the bottom. Throwing
        // inside the render pass took the whole process down, not just the frame.
        var maxTop = Math.Max(0, height - MinMarkerHeight);
        var top = Math.Clamp((ViewportFirstLine - 1) * scale, 0, maxTop);
        var available = Math.Max(MinMarkerHeight, height - top);
        var boxHeight = Math.Clamp(ViewportLineCount * scale, MinMarkerHeight, available);

        context.FillRectangle(
            ThemeTokens.Brush("BgHoverBrush", Brushes.Gray) is ISolidColorBrush s
                ? new SolidColorBrush(s.Color, 0.22)
                : Brushes.Transparent,
            new Rect(0, top, width, boxHeight));

        context.DrawRectangle(
            null,
            new Pen(ThemeTokens.Brush("BorderStrongBrush", Brushes.Gray), ViewportBorderWidth),
            new Rect(0, top, width, boxHeight));
    }
}
