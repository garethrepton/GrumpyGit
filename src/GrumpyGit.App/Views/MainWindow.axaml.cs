using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GrumpyGit.App.Controls;
using GrumpyGit.App.ViewModels;
using GrumpyGit.Core.Terminal;

namespace GrumpyGit.App.Views;

public partial class MainWindow : Window
{
    // ── Drag-drop fields ──────────────────────────────────────────────────────

    private static readonly DataFormat<string> AppFileFormat =
        DataFormat.CreateStringApplicationFormat("GrumpyGit.FileVm");

    private FileChangeViewModel? _pendingDrag;
    private Point _dragOrigin;
    private FileChangeViewModel? _draggingFile;

    // ── Terminal fields ───────────────────────────────────────────────────────

    private ConPtyTerminal? _terminal;
    private CancellationTokenSource? _terminalReadCts;
    private Task? _readLoopTask;
    private bool _terminalStarted;
    private readonly StringBuilder _terminalBuffer = new();
    private const int MaxTerminalLines = 5000;

    // Regex to strip ANSI escape sequences (CSI, OSC, etc.)
    private static readonly Regex AnsiEscapeRegex = new(
        @"\x1B(?:\[[0-9;]*[A-Za-z]|\].*?(?:\x07|\x1B\\)|\([A-Z0-9]|[>=])",
        RegexOptions.Compiled);

    public MainWindow()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm) return;
            vm.OwnerWindow = this;
            vm.ToastRequested += (_, e) =>
            {
                var host = this.FindControl<ToastHost>("ToastHost");
                host?.ShowToast(e.Message, e.Severity, e.AutoCloseMs);
            };

            // Change navigation lives in the viewmodel, but only the view owns the
            // editor that has to scroll.
            vm.ScrollToDiffLineRequested += (_, line) =>
            {
                var viewer = this.FindControl<DiffViewer>("DiffViewerControl");
                viewer?.ScrollToDiffLine(line);
            };

            vm.PropertyChanged += (_, args) =>
            {
                switch (args.PropertyName)
                {
                    case nameof(MainWindowViewModel.RepoPath):
                        if (_terminalStarted)
                            RestartTerminal();
                        break;
                    case nameof(MainWindowViewModel.IsConsoleVisible):
                        if (vm.IsConsoleVisible && !_terminalStarted)
                            StartTerminal();
                        break;
                }
            };
        };
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        WireFileDragDrop();
        WireTerminalInput();
        WireKeyboardShortcuts();
        WireBlameViewer();
        WireFileTree();
    }

    /// <summary>
    /// Selecting a file node in any of the file trees opens its diff. Directory nodes
    /// are left to the TreeView's own expand/collapse behaviour.
    ///
    /// There is one tree per section (conflicts / staged / unstaged / commit files), so
    /// every one has to be wired. A single lookup by name previously matched nothing
    /// after the trees were split per section, which silently made the whole tree view
    /// non-interactive — it rendered fine and simply ignored clicks.
    /// </summary>
    private void WireFileTree()
    {
        foreach (var name in new[]
                 {
                     "ConflictedTreeView", "StagedTreeView",
                     "UnstagedTreeView", "CommitFilesTreeView",
                 })
        {
            var tree = this.FindControl<TreeView>(name);
            if (tree is null) continue;

            tree.SelectionChanged += (_, _) =>
            {
                if (DataContext is not MainWindowViewModel vm) return;
                if (tree.SelectedItem is not FileTreeNodeViewModel node) return;
                if (node.File is null) return;   // directory

                vm.SelectedFile = node.File;
            };
        }
    }

    private void WireBlameViewer()
    {
        var blameViewer = this.FindControl<BlameViewer>("BlameViewerControl");
        if (blameViewer != null)
        {
            blameViewer.CommitClicked += (_, commitHash) =>
            {
                if (DataContext is MainWindowViewModel vm)
                    vm.NavigateToBlameCommit(commitHash);
            };
        }
    }

    // ── Terminal lifecycle ─────────────────────────────────────────────────────

    private void WireTerminalInput()
    {
        var input = this.FindControl<TextBox>("TerminalInput");
        if (input == null) return;

        input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                var text = input.Text ?? string.Empty;
                SendToTerminal(text + "\r\n");
                input.Text = string.Empty;
                e.Handled = true;
            }
            else if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                // Ctrl+C sends interrupt
                SendToTerminal("\x03");
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                // Up arrow for command history
                SendToTerminal("\x1B[A");
                e.Handled = true;
            }
            else if (e.Key == Key.Down)
            {
                SendToTerminal("\x1B[B");
                e.Handled = true;
            }
            else if (e.Key == Key.Tab)
            {
                // Tab completion
                SendToTerminal("\t");
                e.Handled = true;
            }
        };
    }

    private void SendToTerminal(string data)
    {
        var terminal = _terminal;
        if (terminal == null) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(data);
            terminal.Input.Write(bytes, 0, bytes.Length);
            terminal.Input.Flush();
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    private void StartTerminal()
    {
        if (DataContext is not MainWindowViewModel vm || string.IsNullOrEmpty(vm.RepoPath))
        {
            SetTerminalStatus("Open a repository first");
            return;
        }

        // Reject UNC paths to prevent network authentication to untrusted servers
        if (vm.RepoPath.StartsWith(@"\\"))
        {
            SetTerminalStatus("UNC paths are not supported for terminal");
            return;
        }

        if (!Path.IsPathRooted(vm.RepoPath))
        {
            SetTerminalStatus("Relative paths are not supported for terminal");
            return;
        }

        StopTerminal();

        try
        {
            SetTerminalStatus(string.Empty);
            _terminal = new ConPtyTerminal(120, 30, vm.RepoPath, "powershell.exe -NoProfile -NoLogo");
            _terminalStarted = true;
            _terminalReadCts = new CancellationTokenSource();
            StartReadLoop();
        }
        catch (Exception ex)
        {
            SetTerminalStatus($"Terminal error: {ex.Message}");
        }
    }

    private void RestartTerminal()
    {
        StopTerminal();
        _terminalStarted = false;
        _terminalBuffer.Clear();
        UpdateTerminalDisplay(string.Empty);
        StartTerminal();
    }

    private void StartReadLoop()
    {
        var cts = _terminalReadCts;
        var terminal = _terminal;
        if (terminal == null || cts == null) return;

        _readLoopTask = Task.Run(() =>
        {
            var buffer = new byte[4096];
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    int bytesRead = terminal.Output.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    var text = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    Dispatcher.UIThread.Post(() => AppendTerminalOutput(text));
                }

                if (!cts.Token.IsCancellationRequested)
                {
                    Dispatcher.UIThread.Post(() =>
                        SetTerminalStatus("Terminal process exited"));
                }
            }
            catch when (cts.Token.IsCancellationRequested) { }
            catch (IOException)
            {
                if (!cts.Token.IsCancellationRequested)
                {
                    Dispatcher.UIThread.Post(() =>
                        SetTerminalStatus("Terminal connection lost"));
                }
            }
        });
    }

    private void AppendTerminalOutput(string text)
    {
        _terminalBuffer.Append(text);

        // Trim to max lines to prevent unbounded memory growth
        var content = _terminalBuffer.ToString();
        var lines = content.Split('\n');
        if (lines.Length > MaxTerminalLines)
        {
            var trimmed = string.Join('\n', lines.Skip(lines.Length - MaxTerminalLines));
            _terminalBuffer.Clear();
            _terminalBuffer.Append(trimmed);
            content = trimmed;
        }

        UpdateTerminalDisplay(content);
    }

    private void UpdateTerminalDisplay(string content)
    {
        var outputBlock = this.FindControl<SelectableTextBlock>("TerminalOutput");
        var scrollViewer = this.FindControl<ScrollViewer>("TerminalScrollViewer");

        if (outputBlock != null)
        {
            outputBlock.Inlines?.Clear();
            var runs = AnsiTextParser.Parse(content);
            foreach (var run in runs)
            {
                var inline = new Avalonia.Controls.Documents.Run(run.Text)
                {
                    Foreground = run.Foreground,
                    FontWeight = run.IsBold
                        ? Avalonia.Media.FontWeight.Bold
                        : Avalonia.Media.FontWeight.Normal
                };
                outputBlock.Inlines?.Add(inline);
            }
        }

        // Auto-scroll to bottom
        scrollViewer?.ScrollToEnd();
    }

    private void StopTerminal()
    {
        _terminalReadCts?.Cancel();

        try { _readLoopTask?.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { }
        _readLoopTask = null;

        _terminalReadCts?.Dispose();
        _terminalReadCts = null;

        _terminal?.Dispose();
        _terminal = null;
    }

    private void SetTerminalStatus(string message)
    {
        if (DataContext is MainWindowViewModel vm)
            vm.TerminalStatus = message;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        StopTerminal();
        base.OnClosing(e);
    }

    // ── Keyboard shortcuts ─────────────────────────────────────────────────────

    private void WireKeyboardShortcuts()
    {
        var shortcutsPanel = this.FindControl<KeyboardShortcutsPanel>("ShortcutsPanel");
        var shortcutsOverlay = this.FindControl<Border>("ShortcutsOverlay");

        if (shortcutsPanel != null && shortcutsOverlay != null)
        {
            shortcutsPanel.CloseRequested += (_, _) => shortcutsOverlay.IsVisible = false;
        }

        KeyDown += (_, e) =>
        {
            if (DataContext is not MainWindowViewModel vm) return;

            // Ctrl+/ or Ctrl+? — toggle shortcuts panel
            if (e.Key == Key.OemQuestion && e.KeyModifiers.HasFlag(KeyModifiers.Control)
                || e.Key == Key.Oem2 && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (shortcutsOverlay != null)
                    shortcutsOverlay.IsVisible = !shortcutsOverlay.IsVisible;
                e.Handled = true;
                return;
            }

            // Ctrl+P — repository quick switcher. Checked before the other Ctrl
            // bindings so the palette owns the keystroke while it is open.
            if (e.Key == Key.P && e.KeyModifiers == KeyModifiers.Control)
            {
                vm.ToggleQuickSwitchCommand.Execute(null);
                e.Handled = true;
                return;
            }

            // Ctrl+Shift+R — AI review
            if (e.Key == Key.R && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
            {
                vm.ToggleAiReviewCommand.Execute(null);
                e.Handled = true;
                return;
            }

            // Ctrl+Tab / Ctrl+Shift+Tab — cycle open repositories
            if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                vm.CycleTabCommand.Execute(
                    e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? "prev" : "next");
                e.Handled = true;
                return;
            }

            // Ctrl+W — close the active repository tab
            if (e.Key == Key.W && e.KeyModifiers == KeyModifiers.Control)
            {
                vm.CloseActiveTabCommand.Execute(null);
                e.Handled = true;
                return;
            }

            // Ctrl+1..9 — jump straight to the nth open repository
            if (e.KeyModifiers == KeyModifiers.Control && e.Key >= Key.D1 && e.Key <= Key.D9)
            {
                vm.SwitchToTabIndexCommand.Execute((e.Key - Key.D1 + 1).ToString());
                e.Handled = true;
                return;
            }

            // F7 / F8 — previous / next change in the diff
            if (e.Key == Key.F7)
            {
                vm.PreviousChangeCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F8)
            {
                vm.NextChangeCommand.Execute(null);
                e.Handled = true;
                return;
            }

            // F11 — full-screen diff
            if (e.Key == Key.F11)
            {
                vm.ToggleDiffFullScreenCommand.Execute(null);
                e.Handled = true;
                return;
            }

            // Escape — close any open overlay
            if (e.Key == Key.Escape)
            {
                // Full-screen diff is the outermost "mode", so it unwinds last —
                // after any overlay drawn on top of it has been dismissed.
                if (vm.IsDiffFullScreen
                    && !vm.IsQuickSwitchVisible && !vm.IsAiReviewVisible && !vm.IsSettingsVisible)
                {
                    vm.ToggleDiffFullScreenCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
                if (vm.IsQuickSwitchVisible)
                {
                    vm.CloseQuickSwitchCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
                if (vm.IsAiReviewVisible)
                {
                    vm.ToggleAiReviewCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
                if (vm.IsSettingsVisible)
                {
                    vm.CancelSettingsCommand.Execute(null);
                    e.Handled = true;
                    return;
                }
                if (shortcutsOverlay?.IsVisible == true)
                {
                    shortcutsOverlay.IsVisible = false;
                    e.Handled = true;
                    return;
                }
            }

            // Ctrl+, — toggle settings
            if (e.Key == Key.OemComma && e.KeyModifiers == KeyModifiers.Control)
            {
                vm.ToggleSettingsCommand.Execute(null);
                e.Handled = true;
                return;
            }

            // Ctrl+O — open repo
            if (e.Key == Key.O && e.KeyModifiers == KeyModifiers.Control)
            {
                vm.OpenRepoCommand.Execute(null);
                e.Handled = true;
            }
            // Ctrl+G — toggle graph
            else if (e.Key == Key.G && e.KeyModifiers == KeyModifiers.Control)
            {
                vm.ToggleGraphCommand.Execute(null);
                e.Handled = true;
            }
            // Ctrl+` — toggle terminal
            else if (e.Key == Key.OemTilde && e.KeyModifiers == KeyModifiers.Control)
            {
                vm.ToggleConsoleCommand.Execute(null);
                e.Handled = true;
            }
            // Ctrl+F — focus search (will be wired when search is built)
            else if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
            {
                var searchBox = this.FindControl<TextBox>("CommitSearchBox");
                searchBox?.Focus();
                e.Handled = true;
            }
            // Ctrl+Shift+P — push
            else if (e.Key == Key.P && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
            {
                vm.PushCommand.Execute(null);
                e.Handled = true;
            }
            // Ctrl+Shift+L — pull
            else if (e.Key == Key.L && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
            {
                vm.PullCommand.Execute(null);
                e.Handled = true;
            }
            // Ctrl+Shift+B — new branch
            else if (e.Key == Key.B && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
            {
                vm.StartCreateBranchCommand.Execute(null);
                e.Handled = true;
            }
            // Ctrl+Shift+A — stage all
            else if (e.Key == Key.A && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
            {
                vm.StageAllCommand.Execute(null);
                e.Handled = true;
            }
            // Ctrl+Enter — commit
            else if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control)
            {
                vm.CommitCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    // ── Drag-drop wiring ──────────────────────────────────────────────────────

    private void WireFileDragDrop()
    {
        var stagedLB    = this.FindControl<ListBox>("StagedListBox")!;
        var unstagedLB  = this.FindControl<ListBox>("UnstagedListBox")!;
        var stagedSec   = this.FindControl<StackPanel>("StagedSection")!;
        var unstagedSec = this.FindControl<StackPanel>("UnstagedSection")!;

        foreach (var lb in new[] { stagedLB, unstagedLB })
        {
            lb.AddHandler(InputElement.PointerPressedEvent,
                          OnListPointerPressed,
                          RoutingStrategies.Bubble,
                          handledEventsToo: true);

            lb.AddHandler(InputElement.PointerMovedEvent,
                          OnListPointerMoved,
                          RoutingStrategies.Bubble,
                          handledEventsToo: true);

            lb.PointerReleased += (_, _) => _pendingDrag = null;
        }

        DragDrop.SetAllowDrop(stagedSec,   true);
        DragDrop.SetAllowDrop(unstagedSec, true);

        stagedSec.AddHandler(DragDrop.DropEvent,     OnDropToStaged);
        stagedSec.AddHandler(DragDrop.DragOverEvent, OnDragOver);

        unstagedSec.AddHandler(DragDrop.DropEvent,     OnDropToUnstaged);
        unstagedSec.AddHandler(DragDrop.DragOverEvent, OnDragOver);

        this.FindControl<Button>("StageSelectedButton")!.Click += async (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm) return;
            var selected = unstagedLB.SelectedItems?
                               .OfType<FileChangeViewModel>()
                               .ToList()
                           ?? new List<FileChangeViewModel>();
            if (selected.Count > 0)
                await vm.StageFilesAsync(selected);
        };

        this.FindControl<Button>("UnstageSelectedButton")!.Click += async (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm) return;
            var selected = stagedLB.SelectedItems?
                               .OfType<FileChangeViewModel>()
                               .ToList()
                           ?? new List<FileChangeViewModel>();
            if (selected.Count > 0)
                await vm.UnstageFilesAsync(selected);
        };

        this.FindControl<Button>("DiscardSelectedButton")!.Click += async (_, _) =>
        {
            if (DataContext is not MainWindowViewModel vm) return;
            var selected = unstagedLB.SelectedItems?
                               .OfType<FileChangeViewModel>()
                               .ToList()
                           ?? new List<FileChangeViewModel>();
            if (selected.Count > 0)
                await vm.DiscardFilesAsync(selected);
        };
    }

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            _pendingDrag = null;
            return;
        }
        _dragOrigin  = e.GetPosition(null);
        var item     = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(true);
        _pendingDrag = item?.DataContext as FileChangeViewModel;
    }

    private async void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pendingDrag is null) return;

        if (!e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            _pendingDrag = null;
            return;
        }

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragOrigin.X) < 6 && Math.Abs(pos.Y - _dragOrigin.Y) < 6)
            return;

        _draggingFile = _pendingDrag;
        _pendingDrag  = null;

        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(AppFileFormat, "dragging"));

        try
        {
            await DragDrop.DoDragDropAsync(e, transfer, DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.StatusMessage = $"Drag error: {ex.Message}";
        }
        finally
        {
            _draggingFile = null;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = (_draggingFile is not null && e.DataTransfer.Contains(AppFileFormat))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDropToStaged(object? sender, DragEventArgs e)
    {
        if (_draggingFile is not { IsStaged: false } file) return;
        if (DataContext is not MainWindowViewModel vm) return;
        try { await vm.StageFilesAsync(new[] { file }); }
        catch (Exception ex) { vm.StatusMessage = $"Stage error: {ex.Message}"; }
    }

    private async void OnDropToUnstaged(object? sender, DragEventArgs e)
    {
        if (_draggingFile is not { IsStaged: true } file) return;
        if (DataContext is not MainWindowViewModel vm) return;
        try { await vm.UnstageFilesAsync(new[] { file }); }
        catch (Exception ex) { vm.StatusMessage = $"Unstage error: {ex.Message}"; }
    }
}
