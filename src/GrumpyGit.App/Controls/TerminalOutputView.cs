using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using GrumpyGit.Core.Terminal;

namespace GrumpyGit.App.Controls;

/// <summary>
/// Draws terminal scrollback onto a <see cref="DrawingContext"/>.
///
/// <para>
/// This is a custom-drawn control rather than an <c>ItemsControl</c> of templated rows for
/// the same reason the commit graph is: a row is a handful of styled runs on a fixed
/// character grid, and building a visual tree per row would cost far more than drawing the
/// text. It also virtualises itself — it asks the enclosing <see cref="ScrollViewer"/>
/// which rows are on screen and emits only those, so a 5,000-line scrollback costs the
/// same to render as a 40-line one.
/// </para>
/// <para>
/// Everything is positioned on a character grid derived from the monospace cell width,
/// rather than by measuring each run and advancing. That is what keeps a prompt aligned
/// after a shell redraws it in pieces: the shell thinks in columns, so we must too.
/// </para>
/// </summary>
internal sealed class TerminalOutputView : Control
{
    // Rows either side of the viewport, drawn so that a partially-scrolled row is never
    // missing and the margin offset between us and the ScrollViewer cannot matter.
    private const int OverscanRows = 2;

    private IReadOnlyList<TerminalLine> _lines = Array.Empty<TerminalLine>();
    private int _cursorColumn;
    private int _longestLine;

    private ScrollViewer? _scroller;

    private double _fontSize = 13;
    private Typeface _regular;
    private Typeface _bold;
    private double _cellWidth;
    private double _lineHeight;
    private bool _metricsValid;

    public TerminalOutputView()
    {
        Focusable = true;
        ClipToBounds = false;
        RebuildTypefaces();
    }

    /// <summary>Point size of the monospace text. Drives the whole character grid.</summary>
    public double FontSize
    {
        get => _fontSize;
        set
        {
            if (Math.Abs(_fontSize - value) < 0.01) return;
            _fontSize = value;
            _metricsValid = false;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>Character columns that currently fit. Reported to the shell on resize.</summary>
    public int VisibleColumns
    {
        get
        {
            EnsureMetrics();
            var width = _scroller?.Viewport.Width ?? Bounds.Width;
            return _cellWidth <= 0 ? 80 : Math.Max(20, (int)(width / _cellWidth));
        }
    }

    /// <summary>Character rows that currently fit.</summary>
    public int VisibleRows
    {
        get
        {
            EnsureMetrics();
            var height = _scroller?.Viewport.Height ?? Bounds.Height;
            return _lineHeight <= 0 ? 25 : Math.Max(5, (int)(height / _lineHeight));
        }
    }

    /// <summary>Swaps in a new scrollback snapshot and repaints.</summary>
    public void Update(IReadOnlyList<TerminalLine> lines, int cursorColumn)
    {
        _lines = lines;
        _cursorColumn = cursorColumn;

        var longest = 0;
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].Length > longest)
                longest = lines[i].Length;
        _longestLine = longest;

        InvalidateMeasure();
        InvalidateVisual();
    }

    // ── Scroll plumbing ───────────────────────────────────────────────────────

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _scroller = this.FindAncestorOfType<ScrollViewer>();

        // Scrolling does not change our content, so Avalonia has no reason to call Render
        // again — but it must, because Render only emits the rows that were visible last
        // time it ran.
        if (_scroller is not null)
            _scroller.ScrollChanged += OnScrollChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_scroller is not null)
            _scroller.ScrollChanged -= OnScrollChanged;
        _scroller = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    // ── Metrics ───────────────────────────────────────────────────────────────

    private void RebuildTypefaces()
    {
        var family = ThemeTokens.Mono;
        _regular = new Typeface(family);
        _bold = new Typeface(family, FontStyle.Normal, FontWeight.Bold);
        _metricsValid = false;
    }

    private void EnsureMetrics()
    {
        if (_metricsValid) return;

        var probe = new FormattedText(
            "M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _regular, _fontSize, Brushes.White);

        _cellWidth = probe.Width;
        _lineHeight = Math.Ceiling(probe.Height);
        _metricsValid = _cellWidth > 0 && _lineHeight > 0;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureMetrics();

        // Width is the widest row, not the viewport: the ScrollViewer stretches us to at
        // least its own width anyway, and reporting the content width is what lets it
        // decide whether a horizontal scrollbar is needed.
        var width = _longestLine * _cellWidth;
        var height = Math.Max(_lines.Count, 1) * _lineHeight;
        return new Size(width, height);
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        EnsureMetrics();
        if (_lines.Count == 0 || _lineHeight <= 0) return;

        var offsetY = _scroller?.Offset.Y ?? 0;
        var viewportHeight = _scroller?.Viewport.Height ?? Bounds.Height;
        if (viewportHeight <= 0) viewportHeight = Bounds.Height;

        var first = Math.Max(0, (int)(offsetY / _lineHeight) - OverscanRows);
        var last = Math.Min(
            _lines.Count - 1,
            (int)((offsetY + viewportHeight) / _lineHeight) + OverscanRows);

        var defaultForeground = TerminalPalette.Foreground;

        for (var i = first; i <= last; i++)
            RenderLine(context, _lines[i], i * _lineHeight, defaultForeground);

        RenderCursor(context);
    }

    private void RenderLine(DrawingContext context, TerminalLine line, double y, IBrush defaultForeground)
    {
        var column = 0;

        foreach (var span in line.Spans)
        {
            var style = span.Style;

            // Inverse swaps the pair rather than picking an "inverse colour", because that
            // is all it means — and it is how selected entries in `git status` and most
            // prompt themes get their highlight.
            var foreground = TerminalPalette.Resolve(
                style.Inverse ? style.Background : style.EffectiveForeground,
                style.Inverse ? TerminalPalette.Background : defaultForeground);

            var background = style.Inverse
                ? TerminalPalette.Resolve(style.EffectiveForeground, defaultForeground)
                : (style.Background.IsDefault ? null : TerminalPalette.Resolve(style.Background, defaultForeground));

            var x = column * _cellWidth;
            var width = span.Text.Length * _cellWidth;

            if (background is not null)
                context.FillRectangle(background, new Rect(x, y, width, _lineHeight));

            var text = new FormattedText(
                span.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                style.Bold ? _bold : _regular, _fontSize, foreground);

            context.DrawText(text, new Point(x, y));

            if (style.Underline)
            {
                var underlineY = y + _lineHeight - 1.5;
                context.DrawLine(new Pen(foreground, 1),
                    new Point(x, underlineY), new Point(x + width, underlineY));
            }

            column += span.Text.Length;
        }
    }

    /// <summary>
    /// Draws the caret as a translucent block on the last row. Only while focused: an
    /// unfocused terminal showing a cursor invites people to type into a panel that is not
    /// listening.
    /// </summary>
    private void RenderCursor(DrawingContext context)
    {
        if (!IsFocused || _cellWidth <= 0) return;

        var y = (_lines.Count - 1) * _lineHeight;
        var x = _cursorColumn * _cellWidth;
        context.FillRectangle(TerminalPalette.Cursor, new Rect(x, y, _cellWidth, _lineHeight));
    }

    protected override void OnGotFocus(Avalonia.Input.GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }
}
