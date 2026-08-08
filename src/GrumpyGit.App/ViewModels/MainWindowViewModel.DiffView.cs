using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrumpyGit.Core.Git;
using GrumpyGit.Core.Models;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// Diff viewer controls: focused vs full-file context, whitespace handling,
/// change navigation, and the stats shown in the diff header.
/// </summary>
public partial class MainWindowViewModel
{
    // ── Full-screen diff ──────────────────────────────────────────────────────

    /// <summary>
    /// Review mode: hides the branches sidebar and the commit graph so the diff gets
    /// the window, but deliberately KEEPS the file list.
    ///
    /// Reviewing a changeset means working through it file by file, so removing the
    /// file list would turn this into a single-file viewer and force you back out to
    /// move on. The graph and branch list are the things that aren't needed while
    /// reading.
    /// </summary>
    [ObservableProperty] private bool _isDiffFullScreen;

    [RelayCommand]
    private void ToggleDiffFullScreen() => IsDiffFullScreen = !IsDiffFullScreen;

    partial void OnIsDiffFullScreenChanged(bool value)
    {
        OnPropertyChanged(nameof(IsChromeVisible));
        OnPropertyChanged(nameof(SidebarWidth));
        OnPropertyChanged(nameof(FilesPanelWidth));
        OnPropertyChanged(nameof(CommitLogHeight));
    }

    /// <summary>
    /// Inverse of <see cref="IsDiffFullScreen"/>, for the panels review mode hides.
    /// Note the file list does NOT bind to this — it stays visible in review mode.
    /// </summary>
    public bool IsChromeVisible => !IsDiffFullScreen;

    // Hiding a panel is not enough — its grid track keeps its fixed size and leaves a
    // gap, so the track itself has to collapse to zero.

    public Avalonia.Controls.GridLength SidebarWidth =>
        IsDiffFullScreen ? new Avalonia.Controls.GridLength(0) : new Avalonia.Controls.GridLength(200);

    /// <summary>
    /// The file list survives review mode and gets a little more room, since it becomes
    /// the only way to move between files once the graph is hidden.
    /// </summary>
    public Avalonia.Controls.GridLength FilesPanelWidth =>
        new(IsDiffFullScreen ? 300 : 260);

    public Avalonia.Controls.GridLength CommitLogHeight =>
        IsDiffFullScreen
            ? new Avalonia.Controls.GridLength(0)
            : new Avalonia.Controls.GridLength(1, Avalonia.Controls.GridUnitType.Star);

    // ── Focused staging (full-screen mode) ────────────────────────────────────

    /// <summary>
    /// Every file in the current changeset, in the order shown in the sidebar.
    /// Full-screen mode hides those lists, so navigation has to come from here or the
    /// user would be stranded on one file.
    /// </summary>
    private System.Collections.Generic.List<FileChangeViewModel> AllVisibleFiles()
    {
        var files = new System.Collections.Generic.List<FileChangeViewModel>();
        files.AddRange(ConflictedFiles);
        files.AddRange(StagedFiles);
        files.AddRange(ChangedFiles);
        return files;
    }

    public string CurrentFilePositionLabel
    {
        get
        {
            if (SelectedFile is null) return "–";
            var files = AllVisibleFiles();
            var index = files.FindIndex(f => ReferenceEquals(f, SelectedFile));
            return index < 0 ? "–" : $"{index + 1}/{files.Count}";
        }
    }

    /// <summary>
    /// True when the selected file can be staged or unstaged.
    ///
    /// Deliberately independent of the diff options: whole-file staging runs
    /// <c>git add</c> against the file on disk, so it stages the genuine content
    /// regardless of how the diff was rendered — including whitespace changes the
    /// viewer is currently hiding.
    /// </summary>
    public bool CanToggleStagingForCurrentFile => SelectedFile is { IsWorkingTreeFile: true };

    public string StageCurrentFileLabel =>
        SelectedFile?.IsStaged == true ? "Unstage file" : "Stage file";

    /// <summary>Refreshes everything in the focused-staging strip after a selection change.</summary>
    private void RefreshFocusedStagingState()
    {
        OnPropertyChanged(nameof(CurrentFilePositionLabel));
        OnPropertyChanged(nameof(CanToggleStagingForCurrentFile));
        OnPropertyChanged(nameof(StageCurrentFileLabel));
    }

    [RelayCommand]
    private void ToggleStagingForCurrentFile() =>
        SelectedFile?.ToggleStagingCommand?.Execute(null);

    [RelayCommand]
    private void NextFile() => StepFile(1);

    [RelayCommand]
    private void PreviousFile() => StepFile(-1);

    private void StepFile(int delta)
    {
        var files = AllVisibleFiles();
        if (files.Count == 0) return;

        var index = SelectedFile is null
            ? -1
            : files.FindIndex(f => ReferenceEquals(f, SelectedFile));

        index += delta;
        if (index < 0) index = files.Count - 1;
        if (index >= files.Count) index = 0;

        SelectedFile = files[index];
    }

    // ── Per-file line stats ───────────────────────────────────────────────────

    /// <summary>
    /// Attaches numstat counts to a file row. Paths missing from the map are binary
    /// (git reports "-"), so they are left without stats rather than shown as 0/0.
    /// </summary>
    private static void ApplyLineStats(
        FileChangeViewModel vm,
        System.Collections.Generic.IReadOnlyDictionary<string, (int Added, int Removed)> stats)
    {
        if (stats.TryGetValue(vm.Path, out var churn))
        {
            vm.LinesAdded = churn.Added;
            vm.LinesRemoved = churn.Removed;
            vm.HasLineStats = true;
        }
        else
        {
            vm.HasLineStats = false;
        }
    }

    // ── View options ──────────────────────────────────────────────────────────

    /// <summary>
    /// False = focused (hunks with a few lines of context). True = the whole file
    /// with changes highlighted in place.
    ///
    /// Starts on: a change reads better with its surrounding file available, and
    /// <see cref="CollapseUnchangedRegions"/> (also on) folds the untouched runs so
    /// the whole file is present without being noise. Set as a field initialiser
    /// rather than in the constructor so no diff reload is triggered at startup —
    /// there is nothing loaded to reload yet.
    /// </summary>
    [ObservableProperty] private bool _isFullFileDiff = true;

    [ObservableProperty] private bool _ignoreWhitespace;

    [ObservableProperty] private bool _ignoreBlankLines;

    /// <summary>Context lines in focused mode. Ignored when showing the full file.</summary>
    [ObservableProperty] private int _diffContextLines = 3;

    [ObservableProperty] private int _diffAddedCount;
    [ObservableProperty] private int _diffRemovedCount;
    [ObservableProperty] private int _diffChangeCount;
    [ObservableProperty] private int _currentChangeIndex = -1;

    /// <summary>
    /// False when the active diff options make a constructed patch non-applicable, so
    /// the UI can disable *hunk and line* staging instead of letting it fail or corrupt
    /// the index.
    ///
    /// This says nothing about whole-file staging: <c>git add &lt;file&gt;</c> stages the
    /// real file content from disk and is completely unaffected by how the diff was
    /// displayed, so it stays available in every mode.
    /// </summary>
    [ObservableProperty] private bool _canStageFromDiff = true;

    [ObservableProperty] private string _diffStagingBlockedReason = string.Empty;

    /// <summary>Line numbers (in the rendered diff) of each change block, for navigation.</summary>
    private int[] _changeAnchors = [];

    public string DiffStatsLabel => DiffChangeCount == 0
        ? "No changes"
        : $"+{DiffAddedCount}  −{DiffRemovedCount}  ·  {DiffChangeCount} change{(DiffChangeCount == 1 ? "" : "s")}";

    public string DiffModeLabel => IsFullFileDiff ? "Full file" : "Focused";

    public string ChangePositionLabel => CurrentChangeIndex >= 0 && DiffChangeCount > 0
        ? $"{CurrentChangeIndex + 1}/{DiffChangeCount}"
        : DiffChangeCount > 0 ? $"–/{DiffChangeCount}" : "–";

    /// <summary>Options currently in force, derived from the toggles.</summary>
    public DiffOptions CurrentDiffOptions => new()
    {
        ContextLines = IsFullFileDiff ? DiffOptions.FullFileContext : Math.Max(0, DiffContextLines),
        IgnoreWhitespace = IgnoreWhitespace,
        IgnoreBlankLines = IgnoreBlankLines,
    };

    /// <summary>
    /// Folds long unchanged stretches behind an expander. Only meaningful in full-file
    /// mode — focused mode already only shows a few lines of context, so there is
    /// nothing worth hiding.
    /// </summary>
    [ObservableProperty] private bool _collapseUnchangedRegions = true;

    public bool CanCollapseUnchanged => IsFullFileDiff;

    [RelayCommand]
    private void ToggleCollapseUnchanged() => CollapseUnchangedRegions = !CollapseUnchangedRegions;

    partial void OnIsFullFileDiffChanged(bool value)
    {
        OnPropertyChanged(nameof(DiffModeLabel));
        OnPropertyChanged(nameof(CanCollapseUnchanged));
        OnPropertyChanged(nameof(ShouldCollapseUnchanged));
        _ = ReloadCurrentDiffAsync();
    }

    partial void OnCollapseUnchangedRegionsChanged(bool value) =>
        OnPropertyChanged(nameof(ShouldCollapseUnchanged));

    /// <summary>What the viewer actually binds to: collapsing only applies in full-file mode.</summary>
    public bool ShouldCollapseUnchanged => IsFullFileDiff && CollapseUnchangedRegions;

    partial void OnIgnoreWhitespaceChanged(bool value) => _ = ReloadCurrentDiffAsync();

    partial void OnIgnoreBlankLinesChanged(bool value) => _ = ReloadCurrentDiffAsync();

    partial void OnDiffContextLinesChanged(int value)
    {
        if (!IsFullFileDiff)
            _ = ReloadCurrentDiffAsync();
    }

    partial void OnDiffChangeCountChanged(int value)
    {
        OnPropertyChanged(nameof(DiffStatsLabel));
        OnPropertyChanged(nameof(ChangePositionLabel));
    }

    partial void OnDiffAddedCountChanged(int value) => OnPropertyChanged(nameof(DiffStatsLabel));
    partial void OnDiffRemovedCountChanged(int value) => OnPropertyChanged(nameof(DiffStatsLabel));
    partial void OnCurrentChangeIndexChanged(int value) => OnPropertyChanged(nameof(ChangePositionLabel));

    // ── Experimental presentations ────────────────────────────────────────────
    //
    // Alternative readings of the same ParsedDiff. Each is independent of the others and
    // of the diff options, so any one can be deleted by removing its enum member, its
    // toolbar button and its Apply* method in DiffViewer — nothing else depends on them.

    [ObservableProperty] private DiffViewMode _diffViewMode = DiffViewMode.SideBySide;

    public bool IsSideBySideView => DiffViewMode == DiffViewMode.SideBySide;
    public bool IsGhostView => DiffViewMode == DiffViewMode.Ghost;
    public bool IsNotebookView => DiffViewMode == DiffViewMode.Notebook;

    /// <summary>
    /// The editor draws every mode but this one, which is its own control — so it has to
    /// stand down rather than render underneath.
    /// </summary>
    public bool IsEditorDiffVisible => IsTextDiffVisible && !IsNotebookView;

    public bool IsNotebookDiffVisible => IsTextDiffVisible && IsNotebookView;

    partial void OnDiffViewModeChanged(DiffViewMode value)
    {
        OnPropertyChanged(nameof(IsSideBySideView));
        OnPropertyChanged(nameof(IsGhostView));
        OnPropertyChanged(nameof(IsNotebookView));
        OnPropertyChanged(nameof(IsEditorDiffVisible));
        OnPropertyChanged(nameof(IsNotebookDiffVisible));

        // Hunk buttons and the minimap belong to the side-by-side layout; the other
        // modes have no second pane to anchor them to.
        OnPropertyChanged(nameof(CanToggleStagingForCurrentFile));

        if (value == DiffViewMode.Notebook)
            RebuildNotebook();
    }

    [RelayCommand]
    private void ShowSideBySideView() => DiffViewMode = DiffViewMode.SideBySide;

    [RelayCommand]
    private void ShowGhostView() => DiffViewMode = DiffViewMode.Ghost;

    [RelayCommand]
    private void ShowNotebookView() => DiffViewMode = DiffViewMode.Notebook;

    // ── Notebook ──────────────────────────────────────────────────────────────

    /// <summary>Sections: one hunk each, with the model's reading of it above.</summary>
    public ObservableCollection<NotebookCellViewModel> NotebookCells { get; } = new();

    public bool HasNotebookCells => NotebookCells.Count > 0;

    /// <summary>
    /// Rebuilt whenever the diff or the review changes.
    ///
    /// Cheap enough to do wholesale — it is a projection over hunks already in memory —
    /// and the alternative, patching notes into existing cells as the review lands, means
    /// two code paths for a list that is rebuilt on every file change anyway. Skipped
    /// entirely unless the notebook is the mode on screen, since nothing else reads it.
    /// </summary>
    partial void OnCurrentDiffChanged(ParsedDiff? value)
    {
        RebuildNotebook();
        OnPropertyChanged(nameof(DiffReviewDetail));
        OnPropertyChanged(nameof(HasDiffReviewDetail));
    }

    private void RebuildNotebook()
    {
        NotebookCells.Clear();

        if (IsNotebookView)
            foreach (var cell in NotebookCellViewModel.Build(
                         CurrentDiff, DiffChangeNotes, DiffReviewIssues.Select(i => i.Model).ToList(),
                         DiffChangeConcerns))
            {
                cell.IsReviewRunning = IsDiffReviewRunning;
                NotebookCells.Add(cell);
            }

        OnPropertyChanged(nameof(HasNotebookCells));
    }

    /// <summary>
    /// Sections say "reading…" while the model works rather than "No reading", which would
    /// claim it had looked and found nothing to say.
    /// </summary>
    private void UpdateNotebookRunningState(bool running)
    {
        foreach (var cell in NotebookCells)
            cell.IsReviewRunning = running;
    }


    /// <summary>Colour blocks that only moved as moved. Side-by-side only.</summary>
    [ObservableProperty] private bool _highlightMovedBlocks;

    [RelayCommand]
    private void ToggleMovedBlocks() => HighlightMovedBlocks = !HighlightMovedBlocks;

    // ── Change summary ────────────────────────────────────────────────────────

    /// <summary>Symbols touched by the diff on screen, in the order the diff visits them.</summary>
    public ObservableCollection<SymbolChangeViewModel> ChangeSummary { get; } = new();

    [ObservableProperty] private bool _isChangeSummaryVisible = true;

    /// <summary>
    /// One sentence on what this file's diff did, computed from the diff itself. Always
    /// present — it needs no model, no configuration and no network, so every file has a
    /// description whether or not local review is set up.
    /// </summary>
    [ObservableProperty] private string _fileChangeDescription = string.Empty;

    public bool HasFileChangeDescription => !string.IsNullOrEmpty(FileChangeDescription);

    partial void OnFileChangeDescriptionChanged(string value)
        => OnPropertyChanged(nameof(HasFileChangeDescription));

    [ObservableProperty] private string _changeSummaryHeader = string.Empty;

    public bool HasChangeSummary => ChangeSummary.Count > 0;

    public bool HasNoFileSelected => string.IsNullOrEmpty(DiffFilePath);

    /// <summary>
    /// A file IS selected but produced no symbols — the honest reading is that git has no
    /// language driver for this file type, which is different from nothing being selected.
    /// </summary>
    public bool ShowsNoSymbolDetail => !HasNoFileSelected && ChangeSummary.Count == 0;

    [RelayCommand]
    private void ToggleChangeSummary() => IsChangeSummaryVisible = !IsChangeSummaryVisible;

    /// <summary>Jumps the diff to the first edit inside the clicked symbol.</summary>
    [RelayCommand]
    private void GoToSymbol(SymbolChangeViewModel? symbol)
    {
        if (symbol is null) return;
        ScrollToDiffLineRequested?.Invoke(this, symbol.RenderedLineNumber);
    }

    private void RebuildChangeSummary(ParsedDiff? parsed)
    {
        ChangeSummary.Clear();

        if (parsed is not null && !string.IsNullOrEmpty(DiffFilePath))
        {
            var summary = ChangeSummaryBuilder.Build(DiffFilePath, parsed);
            foreach (var symbol in summary.Symbols)
                ChangeSummary.Add(new SymbolChangeViewModel { Model = symbol });

            ChangeSummaryHeader = summary.Symbols.Count == 1
                ? $"1 symbol  ·  +{summary.Added} −{summary.Removed}"
                : $"{summary.Symbols.Count} symbols  ·  +{summary.Added} −{summary.Removed}";

            FileChangeDescription = FileChangeDescriber.Describe(summary, parsed);
        }
        else
        {
            ChangeSummaryHeader = string.Empty;
            FileChangeDescription = string.Empty;
        }

        OnPropertyChanged(nameof(HasChangeSummary));
        OnPropertyChanged(nameof(HasNoFileSelected));
        OnPropertyChanged(nameof(ShowsNoSymbolDetail));
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleFullFileDiff() => IsFullFileDiff = !IsFullFileDiff;

    [RelayCommand]
    private void ToggleIgnoreWhitespace() => IgnoreWhitespace = !IgnoreWhitespace;

    [RelayCommand]
    private void IncreaseDiffContext() => DiffContextLines = Math.Min(50, DiffContextLines + 3);

    [RelayCommand]
    private void DecreaseDiffContext() => DiffContextLines = Math.Max(0, DiffContextLines - 3);

    /// <summary>Raised when navigation wants the viewer to scroll to a rendered line.</summary>
    public event EventHandler<int>? ScrollToDiffLineRequested;

    [RelayCommand]
    private void NextChange()
    {
        if (_changeAnchors.Length == 0) return;
        CurrentChangeIndex = CurrentChangeIndex + 1 >= _changeAnchors.Length ? 0 : CurrentChangeIndex + 1;
        ScrollToDiffLineRequested?.Invoke(this, _changeAnchors[CurrentChangeIndex]);
    }

    [RelayCommand]
    private void PreviousChange()
    {
        if (_changeAnchors.Length == 0) return;
        CurrentChangeIndex = CurrentChangeIndex - 1 < 0 ? _changeAnchors.Length - 1 : CurrentChangeIndex - 1;
        ScrollToDiffLineRequested?.Invoke(this, _changeAnchors[CurrentChangeIndex]);
    }

    // ── Recomputation ─────────────────────────────────────────────────────────

    /// <summary>
    /// Re-fetches the diff currently on screen using the active options. Whichever
    /// source produced it — commit, working tree, or AI session — is re-driven
    /// through the same path so the toggles apply everywhere.
    /// </summary>
    private async Task ReloadCurrentDiffAsync()
    {
        UpdateStagingAvailability();

        if (string.IsNullOrEmpty(DiffFilePath) || string.IsNullOrEmpty(RepoPath))
            return;

        // Ordinary file selection wins. SelectedReviewFile lingers after the AI Review
        // panel is closed, so preferring it here used to reload a stale AI-session diff
        // whenever the user toggled a diff option against a normally-selected file —
        // the header would change to a path that no longer matched the selection.
        // This matches the precedence CurrentNotePath already uses.
        if (SelectedCommit is not null && SelectedFile is not null)
        {
            await LoadDiffAsync(SelectedCommit, SelectedFile);
            return;
        }

        if (SelectedReviewFile is not null && SelectedAiSession is not null)
        {
            await ShowReviewFileDiffAsync(SelectedReviewFile);
        }
    }

    private void UpdateStagingAvailability()
    {
        var options = CurrentDiffOptions;
        CanStageFromDiff = options.SupportsPatchStaging;
        OnPropertyChanged(nameof(CanToggleStagingForCurrentFile));

        // Be explicit about what is and isn't available. Only per-hunk staging is
        // affected — a whitespace-insensitive patch omits real differences and would
        // not apply. Staging the whole file still works and commits the true file
        // contents, whitespace changes included.
        DiffStagingBlockedReason = CanStageFromDiff
            ? string.Empty
            : "Per-hunk staging is off while whitespace is ignored — that patch would not apply. "
              + "Staging the whole file still works and includes the whitespace changes.";
    }

    /// <summary>
    /// Recomputes header stats and the navigation anchors for a freshly parsed diff.
    /// Consecutive changed lines collapse into one anchor so "next change" moves by
    /// change block, not by line.
    /// </summary>
    private void UpdateDiffStats(ParsedDiff? parsed)
    {
        if (parsed is null)
        {
            DiffAddedCount = 0;
            DiffRemovedCount = 0;
            DiffChangeCount = 0;
            CurrentChangeIndex = -1;
            _changeAnchors = [];
            return;
        }

        DiffAddedCount = parsed.RightColoredLines.Count;
        DiffRemovedCount = parsed.LeftColoredLines.Count;

        var changedLines = parsed.LeftColoredLines
            .Concat(parsed.RightColoredLines)
            .Distinct()
            .OrderBy(n => n)
            .ToArray();

        var anchors = new System.Collections.Generic.List<int>();
        var previous = int.MinValue;
        foreach (var line in changedLines)
        {
            if (line != previous + 1)
                anchors.Add(line);
            previous = line;
        }

        _changeAnchors = [.. anchors];
        DiffChangeCount = _changeAnchors.Length;

        RebuildChangeSummary(parsed);

        // Every diff asks the local model for a reading. Fire and forget by design: this
        // runs on the render path, and the diff must not wait for a model to answer.
        RequestDiffReview(DiffFilePath, parsed);

        // Open on the first change instead of line 1. Full-file mode renders the whole
        // file, so the first edit is routinely hundreds of lines down and the reader
        // would otherwise land on an unchanged header with nothing to review. This runs
        // on every load, including option toggles, so the change stays in view rather
        // than the file snapping back to the top each time a toggle is flipped.
        if (_changeAnchors.Length > 0)
        {
            CurrentChangeIndex = 0;
            ScrollToDiffLineRequested?.Invoke(this, _changeAnchors[0]);
        }
        else
        {
            CurrentChangeIndex = -1;
        }
    }
}
