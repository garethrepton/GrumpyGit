using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using AvaloniaEdit.TextMate;
using GrumpyGit.App.ViewModels;
using GrumpyGit.Core.Models;
using TextMateSharp.Grammars;

namespace GrumpyGit.App.Controls;

public partial class DiffViewer : UserControl
{
    // ── Avalonia properties ───────────────────────────────────────────────────

    public static readonly StyledProperty<ParsedDiff?> DiffProperty =
        AvaloniaProperty.Register<DiffViewer, ParsedDiff?>(nameof(Diff));

    public static readonly StyledProperty<string?> FilePathProperty =
        AvaloniaProperty.Register<DiffViewer, string?>(nameof(FilePath));

    public static readonly StyledProperty<ObservableCollection<DiffHunkViewModel>?> HunkViewModelsProperty =
        AvaloniaProperty.Register<DiffViewer, ObservableCollection<DiffHunkViewModel>?>(nameof(HunkViewModels));

    public static readonly StyledProperty<bool> IsWorkingTreeProperty =
        AvaloniaProperty.Register<DiffViewer, bool>(nameof(IsWorkingTree));

    /// <summary>
    /// False when the active diff options produce a patch that cannot be applied,
    /// which must suppress hunk/line staging affordances.
    /// </summary>
    public static readonly StyledProperty<bool> CanStageProperty =
        AvaloniaProperty.Register<DiffViewer, bool>(nameof(CanStage), defaultValue: true);

    public bool CanStage
    {
        get => GetValue(CanStageProperty);
        set => SetValue(CanStageProperty, value);
    }

    /// <summary>
    /// True when the diff is showing the whole file, which is when folding the
    /// unchanged stretches becomes worthwhile.
    /// </summary>
    public static readonly StyledProperty<bool> CollapseUnchangedProperty =
        AvaloniaProperty.Register<DiffViewer, bool>(nameof(CollapseUnchanged));

    public bool CollapseUnchanged
    {
        get => GetValue(CollapseUnchangedProperty);
        set => SetValue(CollapseUnchangedProperty, value);
    }

    public static readonly StyledProperty<ICommand?> StageLinesCommandProperty =
        AvaloniaProperty.Register<DiffViewer, ICommand?>(nameof(StageLinesCommand));

    public static readonly StyledProperty<ICommand?> UnstageLinesCommandProperty =
        AvaloniaProperty.Register<DiffViewer, ICommand?>(nameof(UnstageLinesCommand));

    public ParsedDiff? Diff
    {
        get => GetValue(DiffProperty);
        set => SetValue(DiffProperty, value);
    }

    public string? FilePath
    {
        get => GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    public ObservableCollection<DiffHunkViewModel>? HunkViewModels
    {
        get => GetValue(HunkViewModelsProperty);
        set => SetValue(HunkViewModelsProperty, value);
    }

    public bool IsWorkingTree
    {
        get => GetValue(IsWorkingTreeProperty);
        set => SetValue(IsWorkingTreeProperty, value);
    }

    public ICommand? StageLinesCommand
    {
        get => GetValue(StageLinesCommandProperty);
        set => SetValue(StageLinesCommandProperty, value);
    }

    public ICommand? UnstageLinesCommand
    {
        get => GetValue(UnstageLinesCommandProperty);
        set => SetValue(UnstageLinesCommandProperty, value);
    }

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly RegistryOptions _registryOptions = new(ThemeName.DarkPlus);
    private TextMate.Installation? _leftInstallation;
    private TextMate.Installation? _rightInstallation;
    private ScrollViewer? _leftScrollViewer;
    private ScrollViewer? _rightScrollViewer;
    private bool _syncingScroll;
    private readonly List<Button> _hunkButtons = new();
    private FoldingManager? _leftFolding;
    private FoldingManager? _rightFolding;

    // ── Constructor ───────────────────────────────────────────────────────────

    public DiffViewer()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // Install syntax highlighting
        try
        {
            _leftInstallation = LeftEditor.InstallTextMate(_registryOptions);
            _rightInstallation = RightEditor.InstallTextMate(_registryOptions);
        }
        catch
        {
            // TextMate unavailable
        }

        // Find the ScrollViewers for sync
        Dispatcher.UIThread.Post(() =>
        {
            _leftScrollViewer = LeftEditor.FindDescendantOfType<ScrollViewer>();
            _rightScrollViewer = RightEditor.FindDescendantOfType<ScrollViewer>();

            if (_leftScrollViewer != null)
                _leftScrollViewer.ScrollChanged += OnLeftScrollChanged;

            if (_rightScrollViewer != null)
                _rightScrollViewer.ScrollChanged += OnRightScrollChanged;

            Minimap.LineRequested += OnMinimapLineRequested;

            ApplyDiff();
            // ApplyDiff drops any existing folding managers, so the initial load has to
            // reinstall them too — otherwise a diff that was already set before the
            // control loaded renders unfolded until the next property change.
            ApplyFoldings();
            UpdateMinimapViewport();
        }, DispatcherPriority.Loaded);

        // Wire up context menu for line-level staging
        SetupContextMenu();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_leftScrollViewer != null)
            _leftScrollViewer.ScrollChanged -= OnLeftScrollChanged;
        if (_rightScrollViewer != null)
            _rightScrollViewer.ScrollChanged -= OnRightScrollChanged;

        Minimap.LineRequested -= OnMinimapLineRequested;

        ClearFoldings();

        _leftInstallation?.Dispose();
        _rightInstallation?.Dispose();
    }

    // ── Folding of unchanged regions ──────────────────────────────────────────

    /// <summary>
    /// Rebuilds the fold regions for the current diff.
    ///
    /// Both editors get the SAME line ranges. <see cref="ParsedDiff"/> pads the two
    /// sides to equal length so rows line up; folding each side on its own changed
    /// lines would collapse different rows on each side and the panes would drift.
    /// </summary>
    private void ApplyFoldings()
    {
        if (!CollapseUnchanged)
        {
            ClearFoldings();
            return;
        }

        var diff = Diff;
        if (diff is null)
        {
            ClearFoldings();
            return;
        }

        var changed = new HashSet<int>(diff.LeftColoredLines);
        changed.UnionWith(diff.RightColoredLines);
        changed.UnionWith(diff.HunkHeaderLines);

        _leftFolding ??= FoldingManager.Install(LeftEditor.TextArea);
        _rightFolding ??= FoldingManager.Install(RightEditor.TextArea);

        ApplyTo(_leftFolding, LeftEditor.Document, changed);
        ApplyTo(_rightFolding, RightEditor.Document, changed);
    }

    private static void ApplyTo(FoldingManager manager, TextDocument? document, HashSet<int> changed)
    {
        if (document is null) return;

        var foldings = DiffFoldingBuilder.Build(document, changed);
        manager.UpdateFoldings(foldings, -1);
    }

    private void ClearFoldings()
    {
        if (_leftFolding is not null)
        {
            FoldingManager.Uninstall(_leftFolding);
            _leftFolding = null;
        }

        if (_rightFolding is not null)
        {
            FoldingManager.Uninstall(_rightFolding);
            _rightFolding = null;
        }
    }

    // ── Minimap ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Keeps the minimap's viewport box in step with the editor. Derived from the
    /// scroll offset rather than the visual-line collection so it stays correct while
    /// a scroll is still animating.
    /// </summary>
    private void UpdateMinimapViewport()
    {
        var totalLines = RightEditor.Document?.LineCount ?? 0;
        if (totalLines <= 0)
        {
            Minimap.TotalLines = 1;
            Minimap.ViewportLineCount = 0;
            return;
        }

        Minimap.TotalLines = totalLines;

        var lineHeight = RightEditor.TextArea.TextView.DefaultLineHeight;
        if (lineHeight <= 0 || _rightScrollViewer is null)
        {
            Minimap.ViewportLineCount = 0;
            return;
        }

        Minimap.ViewportFirstLine = (int)(_rightScrollViewer.Offset.Y / lineHeight) + 1;
        Minimap.ViewportLineCount = (int)Math.Ceiling(_rightScrollViewer.Viewport.Height / lineHeight);
    }

    private void OnMinimapLineRequested(object? sender, int line) => ScrollToDiffLine(line);

    /// <summary>
    /// Scrolls both editors so <paramref name="line"/> is visible. Used by the minimap
    /// and by the viewmodel's next/previous-change navigation.
    /// </summary>
    public void ScrollToDiffLine(int line)
    {
        // A scroll requested as part of loading a diff arrives in the same beat as the
        // document assignment, before the text view has measured the new content — the
        // scroll would then be computed against a stale layout and land in the wrong
        // place (or nowhere). Deferring to Render priority lets the new document lay out
        // first. For minimap and next/previous-change use the extra frame is invisible.
        Dispatcher.UIThread.Post(() => ScrollToDiffLineCore(line), DispatcherPriority.Render);
    }

    private void ScrollToDiffLineCore(int line)
    {
        var document = RightEditor.Document;
        if (document is null || document.LineCount == 0) return;

        var target = Math.Clamp(line, 1, document.LineCount);
        RightEditor.ScrollToLine(target);

        // The scroll-sync handler mirrors this to the left editor, but only once the
        // offset actually changes; setting it directly avoids a one-frame mismatch.
        if (_leftScrollViewer is not null && _rightScrollViewer is not null)
            _leftScrollViewer.Offset = _leftScrollViewer.Offset.WithY(_rightScrollViewer.Offset.Y);

        UpdateMinimapViewport();
    }

    // ── Property change ───────────────────────────────────────────────────────

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == DiffProperty || change.Property == FilePathProperty)
        {
            ApplyDiff();
            Minimap.Diff = Diff;
            ApplyFoldings();
            UpdateMinimapViewport();
        }

        if (change.Property == CollapseUnchangedProperty)
        {
            ApplyFoldings();
        }

        if (change.Property == HunkViewModelsProperty
            || change.Property == IsWorkingTreeProperty
            || change.Property == CanStageProperty)
        {
            PositionHunkButtons();
        }
    }

    // ── Scroll synchronisation ────────────────────────────────────────────────

    private void OnLeftScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll || _rightScrollViewer == null) return;
        _syncingScroll = true;
        _rightScrollViewer.Offset = _rightScrollViewer.Offset.WithY(_leftScrollViewer!.Offset.Y);
        _syncingScroll = false;
        PositionHunkButtons();
    }

    private void OnRightScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        UpdateMinimapViewport();

        if (_syncingScroll || _leftScrollViewer == null) return;
        _syncingScroll = true;
        _leftScrollViewer.Offset = _leftScrollViewer.Offset.WithY(_rightScrollViewer!.Offset.Y);
        _syncingScroll = false;

        // Hunk buttons are positioned against the left editor, which has just been
        // scrolled to match. Without this they strand in place when the user scrolls
        // using the right-hand editor.
        PositionHunkButtons();
    }

    // ── Diff application ──────────────────────────────────────────────────────

    private void ApplyDiff()
    {
        // A FoldingManager is bound to the TextDocument it was installed against, and
        // the assignments below replace both documents outright. Uninstalling here —
        // while the old documents are still attached — is what stops the cached
        // managers in ApplyFoldings from outliving them: a stale manager holds
        // collapsed sections anchored in a document the editor no longer shows, which
        // suppresses the newly loaded lines and blanks the pane. ApplyFoldings
        // reinstalls against the new documents immediately after.
        ClearFoldings();

        var diff = Diff;
        var filePath = FilePath;

        // Apply syntax-highlighting grammar from file extension
        if (filePath != null)
        {
            var ext = Path.GetExtension(filePath);
            try
            {
                var language = _registryOptions.GetLanguageByExtension(ext);
                if (language != null)
                {
                    var scope = _registryOptions.GetScopeByLanguageId(language.Id);
                    if (scope != null)
                    {
                        _leftInstallation?.SetGrammar(scope);
                        _rightInstallation?.SetGrammar(scope);
                    }
                }
            }
            catch
            {
                // Unknown extension
            }
        }

        if (diff == null)
        {
            LeftEditor.Document = new TextDocument();
            RightEditor.Document = new TextDocument();
            ClearHunkButtons();
            return;
        }

        LeftEditor.Document = new TextDocument(diff.LeftText);
        RightEditor.Document = new TextDocument(diff.RightText);

        SetBackgroundRenderer(LeftEditor,  diff.LeftColoredLines,  diff.HunkHeaderLines, diff.LeftInlineRanges,  isLeft: true);
        SetBackgroundRenderer(RightEditor, diff.RightColoredLines, diff.HunkHeaderLines, diff.RightInlineRanges, isLeft: false);

        // Position hunk buttons after layout
        Dispatcher.UIThread.Post(PositionHunkButtons, DispatcherPriority.Render);
    }

    private static void SetBackgroundRenderer(
        TextEditor editor,
        IReadOnlyList<int> coloredLines,
        IReadOnlyList<int> hunkLines,
        IReadOnlyList<DiffInlineRange> inlineRanges,
        bool isLeft)
    {
        var renderers = editor.TextArea.TextView.BackgroundRenderers;

        foreach (var old in renderers.OfType<DiffLineBackgroundRenderer>().ToList())
            renderers.Remove(old);

        // Alpha tints from the token set rather than opaque slabs, so the
        // TextMate foreground colours stay legible through the diff shading.
        var colorBrush = isLeft
            ? ThemeTokens.Brush("DiffDelLineBrush", Brushes.Transparent)
            : ThemeTokens.Brush("DiffAddLineBrush", Brushes.Transparent);
        var inlineBrush = isLeft
            ? ThemeTokens.Brush("DiffDelWordBrush", Brushes.Transparent)
            : ThemeTokens.Brush("DiffAddWordBrush", Brushes.Transparent);

        var hunkBrush = ThemeTokens.Brush("DiffHunkLineBrush", Brushes.Transparent);

        renderers.Add(new DiffLineBackgroundRenderer(
            coloredLines, colorBrush, inlineBrush, hunkLines, hunkBrush, inlineRanges));
    }

    // ── Hunk button overlay ──────────────────────────────────────────────────

    private void ClearHunkButtons()
    {
        foreach (var btn in _hunkButtons)
            HunkButtonCanvas.Children.Remove(btn);
        _hunkButtons.Clear();
    }

    private void PositionHunkButtons()
    {
        ClearHunkButtons();

        var hunks = HunkViewModels;
        if (hunks == null || hunks.Count == 0 || !IsWorkingTree)
            return;

        // A whitespace-insensitive diff omits real differences, so a patch built from
        // it will not apply. Offering the buttons and failing later would be worse
        // than not offering them.
        if (!CanStage)
            return;

        var textView = LeftEditor.TextArea.TextView;
        var document = LeftEditor.Document;
        if (document == null || textView.VisualLines.Count == 0)
            return;

        foreach (var hunkVm in hunks)
        {
            int lineNumber = hunkVm.RenderedLineNumber;
            if (lineNumber < 1 || lineNumber > document.LineCount)
                continue;

            // Compute Y position of the hunk header line
            try
            {
                var docLine = document.GetLineByNumber(lineNumber);
                var visualPos = textView.GetVisualPosition(
                    new AvaloniaEdit.TextViewPosition(lineNumber, 0),
                    AvaloniaEdit.Rendering.VisualYPosition.TextTop);

                double y = visualPos.Y - textView.ScrollOffset.Y;

                // Skip if outside visible viewport
                if (y < -20 || y > textView.Bounds.Height + 20)
                    continue;

                var btn = CreateHunkButton(hunkVm);
                Canvas.SetLeft(btn, 4);
                Canvas.SetTop(btn, y);
                HunkButtonCanvas.Children.Add(btn);
                _hunkButtons.Add(btn);
            }
            catch
            {
                // Line not visible or layout not ready
            }
        }
    }

    private static Button CreateHunkButton(DiffHunkViewModel hunkVm)
    {
        var isStaged = hunkVm.IsStaged;

        // A tinted chip rather than a saturated slab: these sit on top of code,
        // so they must be legible without shouting over it. Colours come from
        // the same add/warn ramp used everywhere else.
        var (bgKey, fgKey, borderKey) = isStaged
            ? ("WarnBgBrush", "WarnFgBrush", "WarnBorderBrush")
            : ("AddBgBrush", "AddFgBrush", "AddBorderBrush");

        var btn = new Button
        {
            Content = hunkVm.ButtonText,
            FontSize = ThemeTokens.Size("FontSizeMicro", 10),
            FontWeight = FontWeight.SemiBold,
            Padding = new Thickness(7, 1),
            CornerRadius = new CornerRadius(4),
            Background = ThemeTokens.Brush(bgKey, Brushes.Gray),
            Foreground = ThemeTokens.Brush(fgKey, Brushes.White),
            BorderBrush = ThemeTokens.Brush(borderKey, Brushes.Gray),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = hunkVm
        };

        btn.Click += (_, _) =>
        {
            if (isStaged)
                hunkVm.UnstageHunkCommand?.Execute(null);
            else
                hunkVm.StageHunkCommand?.Execute(null);
        };

        return btn;
    }

    // ── Context menu for line-level staging ───────────────────────────────────

    private void SetupContextMenu()
    {
        var stageItem = new MenuItem { Header = "Stage Selected Lines" };
        stageItem.Click += OnStageSelectedLinesClick;

        var unstageItem = new MenuItem { Header = "Unstage Selected Lines" };
        unstageItem.Click += OnUnstageSelectedLinesClick;

        var menu = new ContextMenu
        {
            Items = { stageItem, unstageItem }
        };

        // Attach to both editors
        LeftEditor.ContextMenu = menu;
        RightEditor.ContextMenu = menu;
    }

    private void OnStageSelectedLinesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!IsWorkingTree) return;
        var (hunk, indices) = GetSelectedHunkLines();
        if (hunk == null || indices.Count == 0) return;

        var diff = Diff;
        if (diff == null) return;

        StageLinesCommand?.Execute((diff, hunk, indices));
    }

    private void OnUnstageSelectedLinesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!IsWorkingTree) return;
        var (hunk, indices) = GetSelectedHunkLines();
        if (hunk == null || indices.Count == 0) return;

        var diff = Diff;
        if (diff == null) return;

        UnstageLinesCommand?.Execute((diff, hunk, indices));
    }

    /// <summary>
    /// Determines which hunk and line indices are selected in the left editor.
    /// Returns (hunk, selectedLineIndices) or (null, empty) if no valid selection.
    /// </summary>
    private (DiffHunk? hunk, HashSet<int> indices) GetSelectedHunkLines()
    {
        var diff = Diff;
        if (diff == null || diff.Hunks.Count == 0)
            return (null, new HashSet<int>());

        var selection = LeftEditor.TextArea.Selection;
        if (selection.IsEmpty)
        {
            // Try right editor
            selection = RightEditor.TextArea.Selection;
        }

        if (selection.IsEmpty)
            return (null, new HashSet<int>());

        // Get the 1-based line range of the selection
        int startLine = selection.StartPosition.Line;
        int endLine = selection.EndPosition.Line;
        if (startLine > endLine)
            (startLine, endLine) = (endLine, startLine);

        // Find which hunk contains these lines
        DiffHunk? targetHunk = null;
        foreach (var hunk in diff.Hunks)
        {
            int hunkStart = hunk.RenderedLineNumber;
            int hunkEnd = hunkStart;
            foreach (var line in hunk.Lines)
            {
                if (line.RenderedLineNumber > 0 && line.RenderedLineNumber > hunkEnd)
                    hunkEnd = line.RenderedLineNumber;
            }

            if (startLine >= hunkStart && startLine <= hunkEnd + 1)
            {
                targetHunk = hunk;
                break;
            }
        }

        if (targetHunk == null)
            return (null, new HashSet<int>());

        // Map rendered line numbers back to DiffLine indices
        var indices = new HashSet<int>();
        for (int i = 0; i < targetHunk.Lines.Count; i++)
        {
            var dl = targetHunk.Lines[i];
            if (dl.RenderedLineNumber >= startLine && dl.RenderedLineNumber <= endLine
                && (dl.Type == DiffLineType.Added || dl.Type == DiffLineType.Removed))
            {
                indices.Add(i);
            }
        }

        return (targetHunk, indices);
    }

    // ── Keyboard shortcuts ───────────────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (!IsWorkingTree) return;

        // Ctrl+Shift+S: Stage hunk at cursor (or selected lines)
        if (e.Key == Key.S && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            var (hunk, indices) = GetSelectedHunkLines();
            var diff = Diff;
            if (diff == null) return;

            if (hunk != null && indices.Count > 0)
            {
                StageLinesCommand?.Execute((diff, hunk, indices));
            }
            else
            {
                // Stage hunk at cursor
                var cursorHunk = GetHunkAtCursor();
                if (cursorHunk != null)
                {
                    var vm = HunkViewModels?.FirstOrDefault(h => h.Hunk == cursorHunk);
                    vm?.StageHunkCommand?.Execute(null);
                }
            }
            e.Handled = true;
        }

        // Ctrl+Shift+U: Unstage hunk at cursor (or selected lines)
        if (e.Key == Key.U && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            var (hunk, indices) = GetSelectedHunkLines();
            var diff = Diff;
            if (diff == null) return;

            if (hunk != null && indices.Count > 0)
            {
                UnstageLinesCommand?.Execute((diff, hunk, indices));
            }
            else
            {
                var cursorHunk = GetHunkAtCursor();
                if (cursorHunk != null)
                {
                    var vm = HunkViewModels?.FirstOrDefault(h => h.Hunk == cursorHunk);
                    vm?.UnstageHunkCommand?.Execute(null);
                }
            }
            e.Handled = true;
        }
    }

    private DiffHunk? GetHunkAtCursor()
    {
        var diff = Diff;
        if (diff == null || diff.Hunks.Count == 0) return null;

        int cursorLine = LeftEditor.TextArea.Caret.Line;

        // Find the hunk whose rendered range contains the cursor
        DiffHunk? best = null;
        foreach (var hunk in diff.Hunks)
        {
            if (hunk.RenderedLineNumber <= cursorLine)
                best = hunk;
        }
        return best;
    }
}
