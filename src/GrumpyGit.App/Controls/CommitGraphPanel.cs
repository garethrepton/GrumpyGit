using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using GrumpyGit.Core.Graph;

namespace GrumpyGit.App.Controls;

/// <summary>
/// The commit graph, drawn as one continuous picture beside the commit list.
///
/// This replaced a per-row cell inside the list. A cell could only ever see its own row,
/// which made anything spanning rows — filtering a branch out, highlighting one line of
/// development end to end — impossible to express. Drawing the whole graph in one control
/// also means one Render call per frame instead of one per visible row.
///
/// Alignment with the list is by construction: both use <see cref="RowHeight"/>, and the
/// panel is told the list's scroll offset rather than scrolling itself. That is why the
/// commit list has a fixed row height — with variable-height rows the two could not line
/// up at all.
/// </summary>
public class CommitGraphPanel : Control
{
    /// <summary>Must match the fixed ListBoxItem height of the commit list.</summary>
    public const double RowHeight = 24.0;

    private const double LaneWidth = 16.0;
    private const double NodeRadius = 4.5;
    private const double LineWidth = 2.0;
    private const double SidePadding = 8.0;

    // Cached per theme variant — Render runs every frame, which is far too hot for a
    // resource dictionary lookup. Keyed on the variant so a theme switch rebuilds once.
    private static IBrush[]? _laneBrushes;
    private static IBrush? _nodeOutlineBrush;
    private static IBrush? _selectionBrush;
    private static ThemeVariant? _paletteVariant;

    private static void EnsurePalette()
    {
        var variant = Application.Current?.ActualThemeVariant ?? ThemeVariant.Dark;
        if (_laneBrushes is not null && Equals(_paletteVariant, variant))
            return;

        _paletteVariant = variant;
        _laneBrushes =
        [
            ThemeTokens.Brush("Lane0Brush", Brushes.CornflowerBlue),
            ThemeTokens.Brush("Lane1Brush", Brushes.IndianRed),
            ThemeTokens.Brush("Lane2Brush", Brushes.MediumSeaGreen),
            ThemeTokens.Brush("Lane3Brush", Brushes.Goldenrod),
            ThemeTokens.Brush("Lane4Brush", Brushes.MediumTurquoise),
            ThemeTokens.Brush("Lane5Brush", Brushes.MediumPurple),
            ThemeTokens.Brush("Lane6Brush", Brushes.SandyBrown),
            ThemeTokens.Brush("Lane7Brush", Brushes.Orchid),
        ];
        _nodeOutlineBrush = ThemeTokens.Brush("BgSurfaceBrush", Brushes.Black);
        _selectionBrush = ThemeTokens.Brush("BgHoverBrush", Brushes.DimGray);
    }

    /// <summary>Colour slot for a branch, so the key and the graph agree.</summary>
    public static IBrush BrushForSlot(int slot)
    {
        EnsurePalette();
        return _laneBrushes![((slot % _laneBrushes.Length) + _laneBrushes.Length) % _laneBrushes.Length];
    }

    // ── Properties ────────────────────────────────────────────────────────────

    public static readonly StyledProperty<IReadOnlyList<GraphNode>?> NodesProperty =
        AvaloniaProperty.Register<CommitGraphPanel, IReadOnlyList<GraphNode>?>(nameof(Nodes));

    public static readonly StyledProperty<int> TotalLanesProperty =
        AvaloniaProperty.Register<CommitGraphPanel, int>(nameof(TotalLanes), 1);

    /// <summary>Vertical scroll offset of the commit list, in pixels.</summary>
    public static readonly StyledProperty<double> ScrollOffsetProperty =
        AvaloniaProperty.Register<CommitGraphPanel, double>(nameof(ScrollOffset));

    /// <summary>
    /// Rows in the list that precede the first graph node — the working-tree row. Without
    /// it every node would draw one row too high.
    /// </summary>
    public static readonly StyledProperty<int> RowOffsetProperty =
        AvaloniaProperty.Register<CommitGraphPanel, int>(nameof(RowOffset));

    public static readonly StyledProperty<string?> SelectedHashProperty =
        AvaloniaProperty.Register<CommitGraphPanel, string?>(nameof(SelectedHash));

    /// <summary>Branch label to colour slot. Falls back to lane index when absent.</summary>
    public static readonly StyledProperty<IReadOnlyDictionary<string, int>?> BranchColorsProperty =
        AvaloniaProperty.Register<CommitGraphPanel, IReadOnlyDictionary<string, int>?>(nameof(BranchColors));

    public IReadOnlyList<GraphNode>? Nodes
    {
        get => GetValue(NodesProperty);
        set => SetValue(NodesProperty, value);
    }

    public int TotalLanes
    {
        get => GetValue(TotalLanesProperty);
        set => SetValue(TotalLanesProperty, value);
    }

    public double ScrollOffset
    {
        get => GetValue(ScrollOffsetProperty);
        set => SetValue(ScrollOffsetProperty, value);
    }

    public int RowOffset
    {
        get => GetValue(RowOffsetProperty);
        set => SetValue(RowOffsetProperty, value);
    }

    public string? SelectedHash
    {
        get => GetValue(SelectedHashProperty);
        set => SetValue(SelectedHashProperty, value);
    }

    public IReadOnlyDictionary<string, int>? BranchColors
    {
        get => GetValue(BranchColorsProperty);
        set => SetValue(BranchColorsProperty, value);
    }

    /// <summary>Raised when a commit node is clicked, with its hash.</summary>
    public event EventHandler<string>? CommitClicked;

    static CommitGraphPanel()
    {
        AffectsRender<CommitGraphPanel>(
            NodesProperty, TotalLanesProperty, ScrollOffsetProperty,
            RowOffsetProperty, SelectedHashProperty, BranchColorsProperty);
        AffectsMeasure<CommitGraphPanel>(TotalLanesProperty);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var lanes = TotalLanes < 1 ? 1 : TotalLanes;
        var width = lanes * LaneWidth + SidePadding * 2;
        var height = double.IsInfinity(availableSize.Height) ? 200 : availableSize.Height;
        return new Size(width, height);
    }

    // ── Interaction ───────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var node = NodeAt(e.GetPosition(this));
        if (node is not null)
            CommitClicked?.Invoke(this, node.Hash);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var node = NodeAt(e.GetPosition(this));
        ToolTip.SetTip(this, node is null
            ? null
            : $"{Describe(node)}\n{node.Subject}");
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ToolTip.SetTip(this, null);
    }

    private static string Describe(GraphNode node) =>
        string.IsNullOrEmpty(node.BranchLabel)
            ? "unknown branch (no ref or merge record survives)"
            : node.BranchLabel;

    /// <summary>
    /// The node whose row the pointer is over. Hit-testing is by row rather than by
    /// distance to the drawn node, so clicking anywhere on a row works — the circles are
    /// 9px across and would be needlessly fiddly to aim at.
    /// </summary>
    private GraphNode? NodeAt(Point position)
    {
        var nodes = Nodes;
        if (nodes is null || nodes.Count == 0) return null;

        var listRow = (int)Math.Floor((position.Y + ScrollOffset) / RowHeight);
        var index = listRow - RowOffset;

        return index >= 0 && index < nodes.Count ? nodes[index] : null;
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        EnsurePalette();

        var height = Bounds.Height;
        var width = Bounds.Width;
        if (height <= 0 || width <= 0) return;

        // A bare Control draws nothing and so receives no pointer events.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        var nodes = Nodes;
        if (nodes is null || nodes.Count == 0) return;

        var offset = ScrollOffset;
        var rowOffset = RowOffset;

        // Only the visible window is drawn. A 20k-commit history would otherwise emit
        // 20k rows of geometry per frame for the ~40 that can actually be seen.
        var firstListRow = (int)Math.Floor(offset / RowHeight);
        var visibleRows = (int)Math.Ceiling(height / RowHeight) + 1;

        var selected = SelectedHash;

        for (var listRow = firstListRow; listRow < firstListRow + visibleRows; listRow++)
        {
            var index = listRow - rowOffset;
            if (index < 0) continue;
            if (index >= nodes.Count) break;

            var node = nodes[index];
            var top = listRow * RowHeight - offset;

            DrawRow(context, node, top, width, string.Equals(node.Hash, selected, StringComparison.Ordinal));
        }
    }

    private void DrawRow(DrawingContext context, GraphNode node, double top, double width, bool isSelected)
    {
        var midY = top + RowHeight / 2.0;
        var nodeCentreX = LaneX(node.Lane);

        if (isSelected && _selectionBrush is ISolidColorBrush s)
        {
            context.FillRectangle(
                new SolidColorBrush(s.Color, 0.35),
                new Rect(0, top, width, RowHeight));
        }

        foreach (var seg in node.Segments)
        {
            var pen = new Pen(BrushFor(seg.BranchLabel, seg.FromLane), LineWidth, lineCap: PenLineCap.Round);
            var x1 = LaneX(seg.FromLane);

            switch (seg.Type)
            {
                case SegmentType.Vertical:
                    context.DrawLine(pen, new Point(x1, top), new Point(x1, top + RowHeight));
                    break;

                case SegmentType.BranchOut:
                    DrawBezier(context, pen,
                        start: new Point(nodeCentreX, midY),
                        cp1: new Point(nodeCentreX, top + RowHeight),
                        cp2: new Point(x1, midY),
                        end: new Point(x1, top + RowHeight));
                    break;

                case SegmentType.MergeIn:
                    DrawBezier(context, pen,
                        start: new Point(x1, top),
                        cp1: new Point(x1, midY),
                        cp2: new Point(nodeCentreX, top),
                        end: new Point(nodeCentreX, midY));
                    break;
            }
        }

        var nodeBrush = BrushFor(node.BranchLabel, node.Lane);
        var outline = new Pen(_nodeOutlineBrush!, 1.5);

        // Merge commits read as a ring so the points where work landed stand out — in
        // branch mode they are the only structure left to navigate by.
        if (node.ParentHashes.Length > 1)
        {
            context.DrawEllipse(_nodeOutlineBrush, new Pen(nodeBrush, 2.0),
                new Point(nodeCentreX, midY), NodeRadius, NodeRadius);
        }
        else
        {
            context.DrawEllipse(nodeBrush, outline,
                new Point(nodeCentreX, midY), NodeRadius, NodeRadius);
        }
    }

    private static double LaneX(int lane) => SidePadding + lane * LaneWidth + LaneWidth / 2.0;

    /// <summary>
    /// Colour by branch where the branch is known, falling back to lane index. Branch
    /// colouring is what makes the key meaningful — a lane index changes as neighbouring
    /// branches open and close, so the same branch would change colour down the graph.
    /// </summary>
    private IBrush BrushFor(string? branchLabel, int lane)
    {
        var colors = BranchColors;
        if (branchLabel is not null && colors is not null && colors.TryGetValue(branchLabel, out var slot))
            return BrushForSlot(slot);

        return BrushForSlot(lane);
    }

    private static void DrawBezier(DrawingContext ctx, IPen pen, Point start, Point cp1, Point cp2, Point end)
    {
        var geo = new StreamGeometry();
        using (var sgc = geo.Open())
        {
            sgc.BeginFigure(start, false);
            sgc.CubicBezierTo(cp1, cp2, end);
            sgc.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);
    }
}
