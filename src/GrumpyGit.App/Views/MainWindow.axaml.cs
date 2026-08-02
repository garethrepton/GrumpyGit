using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using GrumpyGit.App.Controls;
using GrumpyGit.App.ViewModels;

namespace GrumpyGit.App.Views;

public partial class MainWindow : Window
{
    // ── Drag-drop fields ──────────────────────────────────────────────────────

    private static readonly DataFormat<string> AppFileFormat =
        DataFormat.CreateStringApplicationFormat("GrumpyGit.FileVm");

    private FileChangeViewModel? _pendingDrag;
    private Point _dragOrigin;
    private FileChangeViewModel? _draggingFile;

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
        };
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        WireFileDragDrop();
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

            // Ctrl+Shift+R used to open AI review. Withdrawn with the rest of that
            // feature's entry points — a shortcut that still opens a panel nothing
            // advertises is worse than no shortcut, because it fires by accident.

            // Ctrl+Tab / Ctrl+Shift+Tab — walk the repository tree. This steps through
            // worktrees as well as repository roots, because a worktree is somewhere you
            // work, not a detail of the repo that owns it.
            if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                vm.CycleRepoCommand.Execute(
                    e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? "prev" : "next");
                e.Handled = true;
                return;
            }

            // Ctrl+W — close the repository owning the active node
            if (e.Key == Key.W && e.KeyModifiers == KeyModifiers.Control)
            {
                vm.CloseActiveRepoCommand.Execute(null);
                e.Handled = true;
                return;
            }

            // Ctrl+1..9 — jump straight to the nth repository root (roots only, so the
            // number of a given repository does not shift as worktrees come and go)
            if (e.KeyModifiers == KeyModifiers.Control && e.Key >= Key.D1 && e.Key <= Key.D9)
            {
                vm.SwitchToRepoIndexCommand.Execute((e.Key - Key.D1 + 1).ToString());
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
                    && !vm.IsQuickSwitchVisible && !vm.IsSettingsVisible)
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
