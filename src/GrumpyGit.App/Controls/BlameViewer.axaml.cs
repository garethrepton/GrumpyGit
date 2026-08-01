using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.TextMate;
using GrumpyGit.Core.Models;
using TextMateSharp.Grammars;

namespace GrumpyGit.App.Controls;

public partial class BlameViewer : UserControl
{
    // ── Avalonia properties ───────────────────────────────────────────────────

    public static readonly StyledProperty<IReadOnlyList<BlameLine>?> BlameDataProperty =
        AvaloniaProperty.Register<BlameViewer, IReadOnlyList<BlameLine>?>(nameof(BlameData));

    public static readonly StyledProperty<string?> FilePathProperty =
        AvaloniaProperty.Register<BlameViewer, string?>(nameof(FilePath));

    public IReadOnlyList<BlameLine>? BlameData
    {
        get => GetValue(BlameDataProperty);
        set => SetValue(BlameDataProperty, value);
    }

    public string? FilePath
    {
        get => GetValue(FilePathProperty);
        set => SetValue(FilePathProperty, value);
    }

    // ── Events ─────────────────────────────────────────────────────────────────

    public event EventHandler<string>? CommitClicked;

    // ── Fields ─────────────────────────────────────────────────────────────────

    private readonly RegistryOptions _registryOptions = new(ThemeName.DarkPlus);
    private TextMate.Installation? _textMateInstallation;
    private ScrollViewer? _editorScrollViewer;
    private bool _syncingScroll;

    // Blame gutter palette, resolved from Themes/Tokens.axaml. The alternating
    // group backgrounds are two adjacent steps on the elevation ramp so the
    // banding reads as grouping rather than as stripes.
    private static IBrush EvenGroupBrush => ThemeTokens.Brush("GutterBgBrush", Brushes.Transparent);
    private static IBrush OddGroupBrush => ThemeTokens.Brush("BgSurfaceBrush", Brushes.Transparent);
    private static IBrush GutterTextBrush => ThemeTokens.Brush("TextTertiaryBrush", Brushes.Gray);
    private static IBrush GutterHashBrush => ThemeTokens.Brush("InfoFgBrush", Brushes.SteelBlue);
    private static IBrush GutterHoverBrush => ThemeTokens.Brush("BgHoverBrush", Brushes.DimGray);

    // ── Constructor ────────────────────────────────────────────────────────────

    public BlameViewer()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            _textMateInstallation = ContentEditor.InstallTextMate(_registryOptions);
        }
        catch
        {
            // TextMate unavailable
        }

        Dispatcher.UIThread.Post(() =>
        {
            _editorScrollViewer = ContentEditor.FindDescendantOfType<ScrollViewer>();
            if (_editorScrollViewer != null)
                _editorScrollViewer.ScrollChanged += OnEditorScrollChanged;

            ApplyBlame();
        }, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_editorScrollViewer != null)
            _editorScrollViewer.ScrollChanged -= OnEditorScrollChanged;
        _textMateInstallation?.Dispose();
    }

    // ── Property changes ───────────────────────────────────────────────────────

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BlameDataProperty || change.Property == FilePathProperty)
            ApplyBlame();
    }

    // ── Scroll sync: editor → gutter ───────────────────────────────────────────

    private void OnEditorScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll) return;
        _syncingScroll = true;
        GutterScrollViewer.Offset = GutterScrollViewer.Offset.WithY(_editorScrollViewer!.Offset.Y);
        _syncingScroll = false;
    }

    // ── Blame rendering ────────────────────────────────────────────────────────

    private void ApplyBlame()
    {
        var data = BlameData;
        var filePath = FilePath;

        // Apply syntax grammar
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
                        _textMateInstallation?.SetGrammar(scope);
                }
            }
            catch { }
        }

        if (data == null || data.Count == 0)
        {
            ContentEditor.Document = new TextDocument();
            GutterItems.ItemsSource = null;
            return;
        }

        // Set file content
        var fullText = string.Join("\n", data.Select(bl => bl.Text));
        ContentEditor.Document = new TextDocument(fullText);

        // Build gutter items with blame groups
        var gutterEntries = new List<BlameGutterEntry>();
        string? lastHash = null;
        int groupIndex = 0;

        foreach (var line in data)
        {
            bool isFirstInGroup = line.CommitHash != lastHash;
            if (isFirstInGroup && lastHash != null)
                groupIndex++;

            gutterEntries.Add(new BlameGutterEntry
            {
                LineNumber = line.LineNumber,
                ShortHash = isFirstInGroup ? line.CommitHash.Length >= 7 ? line.CommitHash[..7] : line.CommitHash : "",
                AuthorName = isFirstInGroup ? TruncateAuthor(line.AuthorName, 16) : "",
                RelativeDate = isFirstInGroup ? FormatRelativeDate(line.AuthorDate) : "",
                FullHash = line.CommitHash,
                IsFirstInGroup = isFirstInGroup,
                GroupIndex = groupIndex
            });

            lastHash = line.CommitHash;
        }

        // Build visual items.
        // Row height tracks the editor's *actual* line height rather than a
        // constant: the gutter is a parallel list of rows, so any per-row drift
        // accumulates down the file and the annotations desync from the code.
        var lineHeight = ContentEditor.TextArea.TextView.DefaultLineHeight;
        if (double.IsNaN(lineHeight) || lineHeight < 1)
            lineHeight = 17;

        var mono = ThemeTokens.Mono;
        var labelSize = ThemeTokens.Size("FontSizeLabel", 11);
        var microSize = ThemeTokens.Size("FontSizeMicro", 10);

        var gutterControls = new List<Control>();
        foreach (var entry in gutterEntries)
        {
            var background = entry.GroupIndex % 2 == 0 ? EvenGroupBrush : OddGroupBrush;

            var panel = new Grid
            {
                ColumnDefinitions = ColumnDefinitions.Parse("60,100,*"),
                Background = background,
                Height = lineHeight,
                Tag = entry.FullHash,
                Cursor = entry.IsFirstInGroup ? new Cursor(StandardCursorType.Hand) : null
            };

            var hashBlock = new TextBlock
            {
                Text = entry.ShortHash,
                Foreground = GutterHashBrush,
                FontFamily = mono,
                FontSize = labelSize,
                Padding = new Thickness(4, 0, 4, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };

            var authorBlock = new TextBlock
            {
                Text = entry.AuthorName,
                Foreground = GutterTextBrush,
                FontFamily = mono,
                FontSize = labelSize,
                Padding = new Thickness(4, 0, 4, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var dateBlock = new TextBlock
            {
                Text = entry.RelativeDate,
                Foreground = GutterTextBrush,
                FontFamily = mono,
                FontSize = microSize,
                Padding = new Thickness(4, 0, 8, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
            };

            Grid.SetColumn(hashBlock, 0);
            Grid.SetColumn(authorBlock, 1);
            Grid.SetColumn(dateBlock, 2);

            panel.Children.Add(hashBlock);
            panel.Children.Add(authorBlock);
            panel.Children.Add(dateBlock);

            if (entry.IsFirstInGroup)
            {
                panel.PointerEntered += (_, _) => panel.Background = GutterHoverBrush;
                panel.PointerExited += (_, _) => panel.Background = entry.GroupIndex % 2 == 0 ? EvenGroupBrush : OddGroupBrush;
                panel.PointerPressed += (_, args) =>
                {
                    if (args.GetCurrentPoint(panel).Properties.IsLeftButtonPressed)
                        CommitClicked?.Invoke(this, entry.FullHash);
                };
            }

            gutterControls.Add(panel);
        }

        GutterItems.ItemsSource = gutterControls;
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static string TruncateAuthor(string name, int maxLen)
    {
        if (name.Length <= maxLen) return name;
        return name[..(maxLen - 1)] + "\u2026";
    }

    private static string FormatRelativeDate(DateTimeOffset date)
    {
        var span = DateTimeOffset.Now - date;
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
        if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)}mo ago";
        return $"{(int)(span.TotalDays / 365)}y ago";
    }

    private class BlameGutterEntry
    {
        public int LineNumber { get; init; }
        public string ShortHash { get; init; } = "";
        public string AuthorName { get; init; } = "";
        public string RelativeDate { get; init; } = "";
        public string FullHash { get; init; } = "";
        public bool IsFirstInGroup { get; init; }
        public int GroupIndex { get; init; }
    }
}
