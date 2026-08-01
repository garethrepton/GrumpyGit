using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using GrumpyGit.Core.Graph;

namespace GrumpyGit.App.Controls;

/// <summary>
/// Custom Avalonia control that renders the commit graph slice for a single list row:
/// lane lines (vertical / branch-out / merge-in) and the commit node circle.
/// Branch-out and merge-in connections use cubic bezier curves for a polished look.
/// </summary>
public class CommitGraphCell : Control
{
    private const double LaneWidth    = 18.0;
    private const double NodeRadius   = 5.0;
    private const double LineWidth    = 2.0;

    // Colour palette — cycles by lane index. Resolved from Themes/Tokens.axaml
    // so the graph follows the theme variant like everything else.
    //
    // Cached rather than resolved per call: Render runs for every visible row on
    // every frame, and a dictionary lookup (let alone an array allocation) there
    // would be wasteful. The cache is keyed on the active variant, so a runtime
    // theme switch rebuilds it exactly once.
    private static IBrush[]? _laneBrushes;
    private static IBrush? _nodeOutlineBrush;
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
    }

    private static IBrush[] LaneBrushes
    {
        get { EnsurePalette(); return _laneBrushes!; }
    }

    // The ring that punches the node out of the row behind it. It must match the
    // commit list's own background — previously it was #1A1A2E while the list was
    // #2A2A3E, so the "halo" rendered as a dark smudge in every state.
    private static IBrush NodeOutlineBrush
    {
        get { EnsurePalette(); return _nodeOutlineBrush!; }
    }

    // ── Avalonia properties ───────────────────────────────────────────────────

    public static readonly StyledProperty<IReadOnlyList<GraphSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<CommitGraphCell, IReadOnlyList<GraphSegment>?>(nameof(Segments));

    public static readonly StyledProperty<int> LaneProperty =
        AvaloniaProperty.Register<CommitGraphCell, int>(nameof(Lane));

    public static readonly StyledProperty<int> TotalLanesProperty =
        AvaloniaProperty.Register<CommitGraphCell, int>(nameof(TotalLanes), 1);

    /// <summary>Index into <see cref="Segments"/> of the line under the pointer, or -1.</summary>
    public static readonly StyledProperty<int> HoveredSegmentIndexProperty =
        AvaloniaProperty.Register<CommitGraphCell, int>(nameof(HoveredSegmentIndex), -1);

    public int HoveredSegmentIndex
    {
        get => GetValue(HoveredSegmentIndexProperty);
        set => SetValue(HoveredSegmentIndexProperty, value);
    }

    static CommitGraphCell()
    {
        AffectsRender<CommitGraphCell>(
            SegmentsProperty, LaneProperty, TotalLanesProperty, HoveredSegmentIndexProperty);
        AffectsMeasure<CommitGraphCell>(TotalLanesProperty);
    }

    // ── Hover hit-testing ─────────────────────────────────────────────────────

    /// <summary>How close (px) the pointer must be to a lane's centre to count as over it.</summary>
    private const double HoverTolerance = LaneWidth / 2.0;

    protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        UpdateHover(e.GetPosition(this));
    }

    protected override void OnPointerExited(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerExited(e);
        HoveredSegmentIndex = -1;
        ToolTip.SetTip(this, null);
    }

    /// <summary>
    /// Finds the lane line nearest the pointer and shows which branch it belongs to.
    ///
    /// Hit-testing is done on lane centre X rather than on the drawn bezier path: the
    /// curves only deviate near a merge, and matching on the lane is both far cheaper
    /// and more forgiving to aim at than a 2px stroke.
    /// </summary>
    private void UpdateHover(Point position)
    {
        var segments = Segments;
        if (segments is null || segments.Count == 0)
        {
            HoveredSegmentIndex = -1;
            ToolTip.SetTip(this, null);
            return;
        }

        var bestIndex = -1;
        var bestDistance = double.MaxValue;

        for (var i = 0; i < segments.Count; i++)
        {
            var laneCentreX = segments[i].FromLane * LaneWidth + LaneWidth / 2.0;
            var distance = System.Math.Abs(position.X - laneCentreX);

            if (distance < bestDistance && distance <= HoverTolerance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        if (bestIndex == HoveredSegmentIndex)
            return;

        HoveredSegmentIndex = bestIndex;

        if (bestIndex < 0)
        {
            ToolTip.SetIsOpen(this, false);
            return;
        }

        // Avalonia's tooltip service decides whether to track a control when the
        // pointer ENTERS it. By the time we know which lane is under the pointer we
        // are already inside, so setting the tip alone shows nothing — it has to be
        // opened explicitly. Closing first forces a reposition and content refresh
        // when moving between adjacent lanes without leaving the cell.
        ToolTip.SetIsOpen(this, false);
        ToolTip.SetTip(this, DescribeSegment(segments[bestIndex]));
        ToolTip.SetIsOpen(this, true);
    }

    /// <summary>
    /// Builds the hover text. When the branch cannot be determined we say so plainly —
    /// git genuinely does not record which branch a commit was made on, and a confidently
    /// wrong branch name is worse than an honest "unknown".
    /// </summary>
    private static string DescribeSegment(GraphSegment segment)
    {
        var kind = segment.Type switch
        {
            SegmentType.MergeIn => "Merged in from",
            SegmentType.BranchOut => "Branches to",
            _ => "Branch",
        };

        return string.IsNullOrEmpty(segment.BranchLabel)
            ? $"{kind}: unknown branch (no ref or merge record survives)"
            : $"{kind}: {segment.BranchLabel}";
    }

    public IReadOnlyList<GraphSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public int Lane
    {
        get => GetValue(LaneProperty);
        set => SetValue(LaneProperty, value);
    }

    public int TotalLanes
    {
        get => GetValue(TotalLanesProperty);
        set => SetValue(TotalLanesProperty, value);
    }

    // ── Layout ────────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        var lanes = TotalLanes < 1 ? 1 : TotalLanes;
        double height = double.IsInfinity(availableSize.Height) ? 24.0 : availableSize.Height;
        return new Size(lanes * LaneWidth, height);
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        var segments = Segments;
        var lane     = Lane;
        double height      = Bounds.Height > 0 ? Bounds.Height : 24;
        double midY        = height / 2.0;
        double nodeCenterX = lane * LaneWidth + LaneWidth / 2.0;

        // A bare Control draws nothing, so it would receive no pointer events and
        // could never be hovered. Filling the bounds with a transparent brush makes
        // the whole cell hit-testable without changing how it looks.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        if (segments != null)
        {
            var hovered = HoveredSegmentIndex;

            for (var i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                var brush = LaneBrushes[seg.FromLane % LaneBrushes.Length];

                // Thicken the hovered lane so the tooltip clearly refers to the line
                // the pointer is actually on, rather than leaving the user guessing
                // which of several adjacent lanes was picked.
                var isHovered = i == hovered;
                var pen = new Pen(
                    brush,
                    isHovered ? LineWidth + 1.5 : LineWidth,
                    lineCap: PenLineCap.Round);

                double x1 = seg.FromLane * LaneWidth + LaneWidth / 2.0;

                switch (seg.Type)
                {
                    case SegmentType.Vertical:
                        // Straight line — no allocation needed
                        context.DrawLine(pen, new Point(x1, 0), new Point(x1, height));
                        break;

                    case SegmentType.BranchOut:
                        // Cubic bezier: starts going straight down from node, curves to branch lane.
                        // CP1 pulls toward bottom at node X; CP2 pulls toward mid-height at branch X.
                        DrawBezier(context, pen,
                            start: new Point(nodeCenterX, midY),
                            cp1:   new Point(nodeCenterX, height),
                            cp2:   new Point(x1,          midY),
                            end:   new Point(x1,          height));
                        break;

                    case SegmentType.MergeIn:
                        // Cubic bezier: arrives from merge lane at top, curves to node center.
                        // CP1 pulls toward mid-height at merge X; CP2 pulls toward top at node X.
                        DrawBezier(context, pen,
                            start: new Point(x1,          0),
                            cp1:   new Point(x1,          midY),
                            cp2:   new Point(nodeCenterX, 0),
                            end:   new Point(nodeCenterX, midY));
                        break;
                }
            }
        }

        // Node circle drawn on top of all lines
        var nodeBrush  = LaneBrushes[lane % LaneBrushes.Length];
        var nodePen    = new Pen(NodeOutlineBrush, 1.5);
        context.DrawEllipse(nodeBrush, nodePen, new Point(nodeCenterX, midY), NodeRadius, NodeRadius);
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
