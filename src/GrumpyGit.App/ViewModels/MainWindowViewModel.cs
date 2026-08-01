using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrumpyGit.App.Controls;
using GrumpyGit.App.Services;
using GrumpyGit.Core.Git;
using GrumpyGit.Core.Graph;
using GrumpyGit.Core.Models;

namespace GrumpyGit.App.ViewModels;

public class ToastEventArgs : EventArgs
{
    public string Message { get; init; } = string.Empty;
    public ToastSeverity Severity { get; init; } = ToastSeverity.Info;
    public int AutoCloseMs { get; init; } = 4000;
}

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly GitService _git = new();
    private readonly GitHubService _github = new();

    // ── Toast notifications ─────────────────────────────────────────────────────

    public event EventHandler<ToastEventArgs>? ToastRequested;

    private void ShowToast(string message, ToastSeverity severity = ToastSeverity.Info, int autoCloseMs = 4000)
    {
        ToastRequested?.Invoke(this, new ToastEventArgs
        {
            Message = message,
            Severity = severity,
            AutoCloseMs = autoCloseMs
        });
    }

    // ── Scalar properties ─────────────────────────────────────────────────────

    [ObservableProperty] private string _repoPath = string.Empty;
    [ObservableProperty] private string _currentBranch = "No repo";
    [ObservableProperty] private string _remoteUrl = string.Empty;
    [ObservableProperty] private int _pendingChangesCount;
    [ObservableProperty] private ParsedDiff? _currentDiff;
    [ObservableProperty] private string? _diffFilePath;
    [ObservableProperty] private bool _isDiffFromStagedFile;
    [ObservableProperty] private string _statusMessage = "Ready";
    [ObservableProperty] private string _commitMessage = string.Empty;
    [ObservableProperty] private bool _isGraphVisible = true;
    [ObservableProperty] private bool _isConsoleVisible;
    [ObservableProperty] private string _terminalStatus = string.Empty;

    [ObservableProperty] private bool _hasStashEntries;
    [ObservableProperty] private bool _canLoadMoreCommits;

    // ── Paged commit loading ────────────────────────────────────────────────────

    private IReadOnlyList<GraphNode>? _allGraphNodes;
    private int _loadedCommitCount;
    private int _totalLanes;
    private const int CommitPageSize = 500;

    // ── Search ──────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _isSearchVisible;
    [ObservableProperty] private bool _isSearching;
    private List<CommitRowViewModel>? _allCommits;

    // ── Commit range comparison ─────────────────────────────────────────────────

    [ObservableProperty] private CommitRowViewModel? _compareFromCommit;
    [ObservableProperty] private bool _isComparing;
    [ObservableProperty] private string _compareHeader = string.Empty;

    // ── Tags ────────────────────────────────────────────────────────────────────

    public ObservableCollection<TagViewModel> Tags { get; } = new();
    [ObservableProperty] private bool _isCreatingTag;
    [ObservableProperty] private string _newTagName = string.Empty;
    [ObservableProperty] private string _newTagMessage = string.Empty;
    [ObservableProperty] private string? _tagTargetCommit;

    // ── Settings ────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isSettingsVisible;
    [ObservableProperty] private string _settingsGitUserName = string.Empty;
    [ObservableProperty] private string _settingsGitUserEmail = string.Empty;
    [ObservableProperty] private string _settingsDefaultRemote = "origin";
    [ObservableProperty] private string _settingsTerminalFontSize = "13";
    [ObservableProperty] private string _settingsDiffContextLines = "3";
    [ObservableProperty] private string _settingsTheme = "Dark";
    [ObservableProperty] private string _settingsAutoFetchInterval = "0";

    // ── Blame View ──────────────────────────────────────────────────────────────

    [ObservableProperty] private IReadOnlyList<BlameLine>? _blameData;
    [ObservableProperty] private bool _isBlameVisible;
    [ObservableProperty] private string? _blameCommitHash;
    [ObservableProperty] private string? _blameFilePath;

    // ── File History ────────────────────────────────────────────────────────────

    public ObservableCollection<CommitRowViewModel> FileHistoryCommits { get; } = new();
    [ObservableProperty] private bool _isFileHistoryVisible;
    [ObservableProperty] private string _fileHistoryPath = string.Empty;
    [ObservableProperty] private CommitRowViewModel? _selectedFileHistoryCommit;

    // ── GitHub / Pull Requests ───────────────────────────────────────────────────

    public ObservableCollection<PullRequestViewModel> PullRequests { get; } = new();
    public ObservableCollection<IssueViewModel> LinkedIssues { get; } = new();
    [ObservableProperty] private bool _isPrPanelVisible;
    [ObservableProperty] private bool _isCreatePrVisible;
    [ObservableProperty] private bool _isLoadingPrs;
    [ObservableProperty] private string _newPrTitle = string.Empty;
    [ObservableProperty] private string _newPrBody = string.Empty;
    [ObservableProperty] private string? _newPrBaseBranch;
    [ObservableProperty] private bool _newPrIsDraft;
    private bool _prsFetched;

    // ── Repository tabs ────────────────────────────────────────────────────────

    public ObservableCollection<RepoTabViewModel> RepoTabs { get; } = new();
    [ObservableProperty] private RepoTabViewModel? _activeTab;
    [ObservableProperty] private bool _hasMultipleTabs;
    public ObservableCollection<string> RecentRepositories { get; } = new();

    // ── Collections ───────────────────────────────────────────────────────────

    public ObservableCollection<CommitRowViewModel> Commits { get; } = new();
    /// <summary>Staged (indexed) files — shown in the STAGED section.</summary>
    public ObservableCollection<FileChangeViewModel> StagedFiles { get; } = new();
    /// <summary>Unstaged / untracked files, or commit-specific file list.</summary>
    public ObservableCollection<FileChangeViewModel> ChangedFiles { get; } = new();
    public ObservableCollection<string> Branches { get; } = new();
    public ObservableCollection<string> StashEntries { get; } = new();
    public ObservableCollection<DiffHunkViewModel> DiffHunks { get; } = new();

    // ── Selected commit → loads changed files ─────────────────────────────────

    [ObservableProperty] private CommitRowViewModel? _selectedCommit;

    partial void OnSelectedCommitChanged(CommitRowViewModel? value)
    {
        ChangedFiles.Clear();
        StagedFiles.Clear();
        CurrentDiff = null;
        ClearImageDiff();
        DiffFilePath = null;
        SelectedFile = null;
        LinkedIssues.Clear();
        OnPropertyChanged(nameof(IsWorkingTreeSelected));
        if (value is not null)
        {
            _ = value.IsWorkingTree ? LoadWorkingTreeFilesAsync() : LoadCommitFilesAsync(value);
        }
    }

    /// <summary>True when the working-tree row is the selected commit.</summary>
    public bool IsWorkingTreeSelected => SelectedCommit?.IsWorkingTree == true;

    private async Task LoadWorkingTreeFilesAsync()
    {
        StatusMessage = "Loading working tree…";
        try
        {
            var filesTask = _git.GetWorkingTreeStatusAsync(RepoPath);
            var stashTask = _git.GetStashListAsync(RepoPath);
            var unstagedStatsTask = _git.GetWorkingTreeStatsAsync(RepoPath, staged: false);
            var stagedStatsTask = _git.GetWorkingTreeStatsAsync(RepoPath, staged: true);
            await Task.WhenAll(filesTask, stashTask, unstagedStatsTask, stagedStatsTask);

            var files = filesTask.Result;
            StagedFiles.Clear();
            ChangedFiles.Clear();
            ConflictedFiles.Clear();
            foreach (var f in files)
            {
                var vm = ToWorkingTreeFileChangeViewModel(f);

                // Staged and unstaged churn are different numbers for the same path,
                // so each list is populated from its own numstat.
                ApplyLineStats(vm, f.IsStaged ? stagedStatsTask.Result : unstagedStatsTask.Result);

                if (f.Status == FileChangeStatus.Conflicted)
                    ConflictedFiles.Add(vm);
                else if (f.IsStaged)
                    StagedFiles.Add(vm);
                else
                    ChangedFiles.Add(vm);
            }
            HasConflicts = ConflictedFiles.Count > 0;
            MarkNotedFiles();
            RebuildFileTree();

            StashEntries.Clear();
            foreach (var s in stashTask.Result)
                StashEntries.Add(s);
            HasStashEntries = StashEntries.Count > 0;

            StatusMessage = files.Count > 0 ? $"{files.Count} uncommitted file(s)" : "Working tree clean";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading working tree: {ex.Message}";
        }
    }

    private async Task LoadCommitFilesAsync(CommitRowViewModel commit)
    {
        StatusMessage = "Loading files…";
        try
        {
            var filesTask = _git.GetFilesChangedInCommitAsync(RepoPath, commit.Hash);
            var statsTask = _git.GetCommitStatsAsync(RepoPath, commit.Hash);
            await Task.WhenAll(filesTask, statsTask);

            var files = filesTask.Result;
            ChangedFiles.Clear();
            foreach (var f in files)
            {
                var vm = ToFileChangeViewModel(f);
                ApplyLineStats(vm, statsTask.Result);
                ChangedFiles.Add(vm);
            }
            MarkNotedFiles();
            RebuildFileTree();
            StatusMessage = $"{files.Count} file(s) changed";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading files: {ex.Message}";
        }
    }

    // ── Selected file → loads diff ────────────────────────────────────────────

    [ObservableProperty] private FileChangeViewModel? _selectedFile;

    partial void OnSelectedFileChanged(FileChangeViewModel? value)
    {
        // Picking a normal file ends AI-review browsing. Without this the review
        // selection outlives the panel and later diff-option toggles reload it
        // instead of the file the user actually has selected.
        if (value is not null)
            SelectedReviewFile = null;

        RefreshFocusedStagingState();
        LoadNoteForCurrentFile();
        CurrentDiff = null;
        ClearImageDiff();
        DiffFilePath = null;
        if (value is not null && SelectedCommit is not null)
            _ = LoadDiffAsync(SelectedCommit, value);
    }

    private async Task LoadDiffAsync(CommitRowViewModel commit, FileChangeViewModel file)
    {
        StatusMessage = "Loading diff…";
        try
        {
            // Picture files get rendered rather than diffed as text — a unified diff of
            // a PNG conveys nothing. Falls through to the text path for everything else.
            if (await TryLoadImageDiffAsync(commit, file))
            {
                StatusMessage = string.Empty;
                return;
            }

            ClearImageDiff();

            ParsedDiff parsed;
            bool isStaged = false;
            if (commit.IsWorkingTree)
            {
                if (file.StatusLabel == "?")
                {
                    // Untracked file — git diff returns nothing, show raw content as all-added
                    var fullPath = Path.GetFullPath(Path.Combine(RepoPath, file.Path));
                    var repoRoot = Path.GetFullPath(RepoPath) + Path.DirectorySeparatorChar;
                    var raw = fullPath.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase)
                              && File.Exists(fullPath)
                        ? await File.ReadAllTextAsync(fullPath)
                        : string.Empty;
                    parsed = UnifiedDiffParser.ParseRawContent(raw);
                }
                else
                {
                    isStaged = file.IsStaged;
                    var options = CurrentDiffOptions;
                    var raw = isStaged
                        ? await _git.GetStagedDiffAsync(RepoPath, file.Path, options)
                        : await _git.GetUnstagedDiffAsync(RepoPath, file.Path, options);
                    parsed = UnifiedDiffParser.Parse(raw);
                }
            }
            else
            {
                var raw = await _git.GetFileDiffAsync(RepoPath, commit.Hash, file.Path, CurrentDiffOptions);
                parsed = UnifiedDiffParser.Parse(raw);
            }
            DiffFilePath = file.Path;
            IsDiffFromStagedFile = isStaged;
            CurrentDiff = parsed;
            UpdateDiffStats(parsed);
            UpdateStagingAvailability();
            PopulateDiffHunks(parsed, isStaged);
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading diff: {ex.Message}";
        }
    }

    private void PopulateDiffHunks(ParsedDiff parsed, bool isStaged)
    {
        DiffHunks.Clear();

        // Only show hunk buttons for working tree diffs
        if (!IsWorkingTreeSelected || parsed.Hunks.Count == 0)
            return;

        int total = parsed.Hunks.Count;
        foreach (var hunk in parsed.Hunks)
        {
            var capturedHunk = hunk;
            var capturedParsed = parsed;
            DiffHunks.Add(new DiffHunkViewModel
            {
                Hunk = capturedHunk,
                RenderedLineNumber = capturedHunk.RenderedLineNumber,
                IsStaged = isStaged,
                HunkLabel = $"Hunk {capturedHunk.Index + 1} of {total}",
                StageHunkCommand = new AsyncRelayCommand(() => StageHunkInternalAsync(capturedParsed, capturedHunk)),
                UnstageHunkCommand = new AsyncRelayCommand(() => UnstageHunkInternalAsync(capturedParsed, capturedHunk))
            });
        }
    }

    private async Task StageHunkInternalAsync(ParsedDiff diff, DiffHunk hunk)
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        try
        {
            var patch = PatchBuilder.BuildFromHunks(diff.FileHeaderLines, new[] { hunk });
            if (string.IsNullOrEmpty(patch)) return;
            await _git.StageHunkAsync(RepoPath, patch);
            StatusMessage = $"Staged hunk {hunk.Index + 1}";
            await RefreshCurrentDiffAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Stage hunk failed: {ex.Message}";
        }
    }

    private async Task UnstageHunkInternalAsync(ParsedDiff diff, DiffHunk hunk)
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        try
        {
            var patch = PatchBuilder.BuildFromHunks(diff.FileHeaderLines, new[] { hunk });
            if (string.IsNullOrEmpty(patch)) return;
            await _git.UnstageHunkAsync(RepoPath, patch);
            StatusMessage = $"Unstaged hunk {hunk.Index + 1}";
            await RefreshCurrentDiffAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Unstage hunk failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Stages selected lines from a hunk. Called from the DiffViewer context menu.
    /// </summary>
    public async Task StageLinesAsync(ParsedDiff diff, DiffHunk hunk, IReadOnlySet<int> selectedLineIndices)
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        try
        {
            var patch = PatchBuilder.BuildFromSelectedLines(diff.FileHeaderLines, hunk, selectedLineIndices);
            if (string.IsNullOrEmpty(patch)) return;
            await _git.StageHunkAsync(RepoPath, patch);
            StatusMessage = "Staged selected lines";
            await RefreshCurrentDiffAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Stage lines failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Unstages selected lines from a hunk. Called from the DiffViewer context menu.
    /// </summary>
    public async Task UnstageLinesAsync(ParsedDiff diff, DiffHunk hunk, IReadOnlySet<int> selectedLineIndices)
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        try
        {
            // forReverseApply: UnstageHunkAsync applies with --reverse, so unselected
            // lines must be treated from the index's point of view, not the worktree's.
            var patch = PatchBuilder.BuildFromSelectedLines(
                diff.FileHeaderLines, hunk, selectedLineIndices, forReverseApply: true);
            if (string.IsNullOrEmpty(patch)) return;
            await _git.UnstageHunkAsync(RepoPath, patch);
            StatusMessage = "Unstaged selected lines";
            await RefreshCurrentDiffAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Unstage lines failed: {ex.Message}";
        }
    }

    private async Task RefreshCurrentDiffAsync()
    {
        // Refresh file lists and re-load the current diff
        await LoadWorkingTreeFilesAsync();

        if (DiffFilePath is not null && SelectedCommit is not null)
        {
            // Try to find the file in updated lists
            var file = ChangedFiles.FirstOrDefault(f => f.Path == DiffFilePath)
                       ?? StagedFiles.FirstOrDefault(f => f.Path == DiffFilePath);
            if (file is not null)
            {
                SelectedFile = file;
            }
            else
            {
                // File no longer has changes (fully staged/unstaged)
                CurrentDiff = null;
                ClearImageDiff();
                DiffFilePath = null;
                DiffHunks.Clear();
            }
        }
    }

    // ── Window reference (for folder picker) ──────────────────────────────────

    public Window? OwnerWindow { get; set; }

    // ── Commands: repo ────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task OpenRepoAsync()
    {
        if (OwnerWindow is null)
        {
            StatusMessage = "Cannot open dialog — window reference not set.";
            return;
        }

        var results = await OwnerWindow.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Open Git Repository", AllowMultiple = false });

        if (results.Count == 0)
            return;

        var path = results[0].TryGetLocalPath() ?? results[0].Path.LocalPath;
        await LoadRepoAsync(path);
    }

    private async Task LoadRepoAsync(string path)
    {
        RepoPath = path;

        // Auto-create tab if this repo isn't already tabbed
        if (!RepoTabs.Any(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase)))
            AddRepoTab(path);

        Commits.Clear();
        ChangedFiles.Clear();
        CurrentDiff = null;
        ClearImageDiff();
        DiffFilePath = null;
        SelectedCommit = null;
        SelectedFile = null;
        StatusMessage = "Loading repository…";

        try
        {
            var branchTask = _git.GetCurrentBranchAsync(RepoPath);
            var commitsTask = _git.GetCommitGraphAsync(RepoPath);
            var statusTask = _git.GetWorkingTreeStatusAsync(RepoPath);
            var branchListTask = _git.GetBranchesAsync(RepoPath);
            var remoteTask = _git.GetRemoteUrlAsync(RepoPath);
            var stashTask = _git.GetStashListAsync(RepoPath);

            await Task.WhenAll(branchTask, commitsTask, statusTask, branchListTask, remoteTask, stashTask);

            CurrentBranch = branchTask.Result;
            RemoteUrl = remoteTask.Result;

            _suppressBranchSwitch = true;
            Branches.Clear();
            foreach (var b in branchListTask.Result)
                Branches.Add(b);
            SelectedBranch = CurrentBranch;
            _suppressBranchSwitch = false;

            OnPropertyChanged(nameof(MergeBranches));

            // Attribution must be computed before commit rows are built, since each
            // row reads its AI badge from the attribution map.
            RebuildAiSessions(commitsTask.Result);
            InitialiseReviewTools();

            var nodes = GraphLayoutEngine.Compute(commitsTask.Result);

            // Compute the total number of active lanes for sizing the graph column
            int maxLane = 0;
            foreach (var node in nodes)
            {
                if (node.Lane > maxLane) maxLane = node.Lane;
                foreach (var seg in node.Segments)
                {
                    if (seg.FromLane > maxLane) maxLane = seg.FromLane;
                    if (seg.ToLane > maxLane) maxLane = seg.ToLane;
                }
            }
            int totalLanes = maxLane + 1;

            StashEntries.Clear();
            foreach (var s in stashTask.Result)
                StashEntries.Add(s);
            HasStashEntries = StashEntries.Count > 0;

            var workingFiles = statusTask.Result;
            PendingChangesCount = workingFiles.Count;

            Commits.Add(new CommitRowViewModel
            {
                Hash = CommitRowViewModel.WorkingTreeHash,
                Subject = workingFiles.Count > 0
                    ? $"  Working Changes  ({workingFiles.Count} file(s))"
                    : "  Working Tree  (clean)",
                TotalLanes = totalLanes
            });

            // Paged loading — store all nodes but only render first page
            _allGraphNodes = nodes;
            _totalLanes = totalLanes;
            _loadedCommitCount = 0;
            LoadNextCommitPage();

            StatusMessage = HasAiSessions
                ? $"Loaded {nodes.Count} commit(s) · {AiSummaryLabel}"
                : $"Loaded {nodes.Count} commit(s)";

            // Check for rebase in progress
            IsRebaseInProgress = await _git.IsRebaseInProgressAsync(RepoPath);

            // Load tags in parallel (fire-and-forget, non-critical)
            _ = LoadTagsAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            CurrentBranch = "Error";
        }
    }

    [RelayCommand]
    private async Task PullAsync()
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        StatusMessage = "Pulling…";
        try
        {
            await _git.PullAsync(RepoPath);
            await LoadRepoAsync(RepoPath);
            ShowToast("Pull complete", ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"Pull failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    [RelayCommand]
    private async Task PushAsync()
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        StatusMessage = "Pushing…";
        try
        {
            await _git.PushAsync(RepoPath);
            ShowToast("Push complete", ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"Push failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    // ── Commands: staging / committing ────────────────────────────────────────

    [RelayCommand]
    private async Task CommitAsync()
    {
        if (string.IsNullOrEmpty(RepoPath) || string.IsNullOrWhiteSpace(CommitMessage)) return;
        StatusMessage = "Committing…";
        try
        {
            await _git.CommitAsync(RepoPath, CommitMessage);
            CommitMessage = string.Empty;
            await LoadRepoAsync(RepoPath);
            ShowToast("Commit created", ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"Commit failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    [RelayCommand]
    private async Task StageAllAsync()
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        StatusMessage = "Staging all files…";
        try
        {
            var unstaged = ChangedFiles.Where(f => !f.IsStaged).ToList();
            foreach (var file in unstaged)
                await _git.StageFileAsync(RepoPath, file.Path);
            await LoadWorkingTreeFilesAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Stage all failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task UnstageAllAsync()
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        StatusMessage = "Unstaging all files…";
        try
        {
            // Snapshot first: LoadWorkingTreeFilesAsync rebuilds StagedFiles, so
            // iterating it directly would mutate the collection mid-enumeration.
            var staged = StagedFiles.ToList();
            foreach (var file in staged)
                await _git.UnstageFileAsync(RepoPath, file.Path);
            await LoadWorkingTreeFilesAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Unstage all failed: {ex.Message}";
        }
    }

    /// <summary>True when there is anything staged to unstage.</summary>
    public bool HasStagedFiles => StagedFiles.Count > 0;

    // ── Batch staging (called from code-behind for multi-select / drag-drop) ──

    public async Task StageFilesAsync(IEnumerable<FileChangeViewModel> files)
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        StatusMessage = "Staging…";
        try
        {
            foreach (var f in files.ToList())
                await _git.StageFileAsync(RepoPath, f.Path);
            await LoadWorkingTreeFilesAsync();
        }
        catch (Exception ex) { StatusMessage = $"Staging error: {ex.Message}"; }
    }

    public async Task UnstageFilesAsync(IEnumerable<FileChangeViewModel> files)
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        StatusMessage = "Unstaging…";
        try
        {
            foreach (var f in files.ToList())
                await _git.UnstageFileAsync(RepoPath, f.Path);
            await LoadWorkingTreeFilesAsync();
        }
        catch (Exception ex) { StatusMessage = $"Unstaging error: {ex.Message}"; }
    }

    // ── Commands: branch management ───────────────────────────────────────────

    // Branch ComboBox — suppress change handler while reloading to avoid spurious checkouts
    private bool _suppressBranchSwitch;
    [ObservableProperty] private string? _selectedBranch;

    partial void OnSelectedBranchChanged(string? value)
    {
        if (_suppressBranchSwitch || value is null || value == CurrentBranch) return;
        _ = SwitchBranchInternalAsync(value);
    }

    private async Task SwitchBranchInternalAsync(string branchName)
    {
        StatusMessage = $"Switching to '{branchName}'…";
        try
        {
            await _git.CheckoutBranchAsync(RepoPath, branchName);
            await LoadRepoAsync(RepoPath);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Switch failed: {ex.Message}";
            // Restore ComboBox to the still-current branch
            _suppressBranchSwitch = true;
            SelectedBranch = CurrentBranch;
            _suppressBranchSwitch = false;
        }
    }

    // Create branch
    [ObservableProperty] private bool _isCreatingBranch;
    [ObservableProperty] private string _newBranchName = string.Empty;

    partial void OnIsCreatingBranchChanged(bool value)
        => OnPropertyChanged(nameof(IsBranchBarVisible));

    [RelayCommand]
    private void StartCreateBranch()
    {
        NewBranchName = string.Empty;
        IsMerging = false;
        IsCreatingBranch = true;
    }

    [RelayCommand]
    private async Task ConfirmCreateBranchAsync()
    {
        if (string.IsNullOrWhiteSpace(NewBranchName)) return;
        var name = NewBranchName.Trim();
        StatusMessage = $"Creating branch '{name}'…";
        try
        {
            await _git.CreateBranchAsync(RepoPath, name);
            IsCreatingBranch = false;
            NewBranchName = string.Empty;
            await LoadRepoAsync(RepoPath);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Create branch failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CancelCreateBranch()
    {
        IsCreatingBranch = false;
        NewBranchName = string.Empty;
    }

    // Merge
    [ObservableProperty] private bool _isMerging;
    [ObservableProperty] private string? _selectedMergeBranch;

    partial void OnIsMergingChanged(bool value)
        => OnPropertyChanged(nameof(IsBranchBarVisible));

    /// <summary>Branches available as merge sources (all except current).</summary>
    public IReadOnlyList<string> MergeBranches =>
        Branches.Where(b => b != CurrentBranch).ToList();

    public bool IsBranchBarVisible => IsCreatingBranch || IsMerging;

    [RelayCommand]
    private void StartMerge()
    {
        SelectedMergeBranch = null;
        IsCreatingBranch = false;
        IsMerging = true;
    }

    [RelayCommand]
    private async Task ConfirmMergeAsync()
    {
        if (string.IsNullOrEmpty(SelectedMergeBranch)) return;
        StatusMessage = $"Merging '{SelectedMergeBranch}' → '{CurrentBranch}'…";
        try
        {
            await _git.MergeBranchAsync(RepoPath, SelectedMergeBranch);
            IsMerging = false;
            SelectedMergeBranch = null;
            await LoadRepoAsync(RepoPath);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Merge failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CancelMerge()
    {
        IsMerging = false;
        SelectedMergeBranch = null;
    }

    // ── Commands: stash ───────────────────────────────────────────────────────

    [RelayCommand]
    private async Task StashAsync()
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        StatusMessage = "Stashing changes…";
        try
        {
            await _git.StashAsync(RepoPath);
            await LoadRepoAsync(RepoPath);
            ShowToast("Changes stashed", ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"Stash failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    [RelayCommand]
    private async Task StashPopAsync()
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        StatusMessage = "Applying stash…";
        try
        {
            await _git.StashPopAsync(RepoPath);
            await LoadRepoAsync(RepoPath);
            ShowToast("Stash applied", ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"Stash pop failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    // ── Confirmation dialog ────────────────────────────────────────────────────

    [ObservableProperty] private bool _isConfirmDialogVisible;
    [ObservableProperty] private string _confirmDialogTitle = string.Empty;
    [ObservableProperty] private string _confirmDialogMessage = string.Empty;

    private TaskCompletionSource<bool>? _confirmTcs;

    private async Task<bool> ShowConfirmationAsync(string title, string message)
    {
        ConfirmDialogTitle = title;
        ConfirmDialogMessage = message;
        _confirmTcs = new TaskCompletionSource<bool>();
        IsConfirmDialogVisible = true;
        return await _confirmTcs.Task;
    }

    [RelayCommand]
    private void ConfirmDialogYes()
    {
        IsConfirmDialogVisible = false;
        _confirmTcs?.TrySetResult(true);
    }

    [RelayCommand]
    private void ConfirmDialogNo()
    {
        IsConfirmDialogVisible = false;
        _confirmTcs?.TrySetResult(false);
    }

    // ── Commands: undo / revert ─────────────────────────────────────────────

    [RelayCommand]
    private async Task UndoLastCommitAsync()
    {
        if (string.IsNullOrEmpty(RepoPath)) return;

        // Find the first non-working-tree commit
        var headCommit = Commits.FirstOrDefault(c => !c.IsWorkingTree);
        if (headCommit is null)
        {
            StatusMessage = "No commits to undo.";
            return;
        }

        // Build confirmation message
        string title;
        string message;
        if (headCommit.IsMergeCommit)
        {
            title = "Undo Merge Commit?";
            message = $"This is a merge commit. Undoing it will move HEAD back and place all merged changes in the staging area.\n\nCommit: {headCommit.ShortHash} {headCommit.Subject}\n\nIf this commit has already been pushed, you may need to force-push.";
        }
        else
        {
            title = "Undo Last Commit?";
            message = $"This will move HEAD back by one commit. All changes from that commit will be placed back in the staging area.\n\nCommit: {headCommit.ShortHash} {headCommit.Subject}\n\nIf this commit has already been pushed, you may need to force-push.";
        }

        var confirmed = await ShowConfirmationAsync(title, message);
        if (!confirmed) return;

        StatusMessage = "Undoing last commit…";
        try
        {
            await _git.UndoLastCommitAsync(RepoPath);
            await LoadRepoAsync(RepoPath);
        }
        catch (GitException ex) when (ex.GitOutput.Contains("HEAD~1", StringComparison.OrdinalIgnoreCase)
                                      || ex.GitOutput.Contains("unknown revision", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "Cannot undo — this is the initial commit.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Undo failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RevertCommitAsync(string? commitHash)
    {
        if (string.IsNullOrEmpty(RepoPath) || string.IsNullOrEmpty(commitHash)) return;
        if (commitHash == CommitRowViewModel.WorkingTreeHash) return;

        var commit = Commits.FirstOrDefault(c => c.Hash == commitHash);
        if (commit is null)
        {
            StatusMessage = "Commit not found.";
            return;
        }

        string title;
        string message;
        if (commit.IsMergeCommit)
        {
            title = "Revert Merge Commit?";
            message = $"This will create a new commit that undoes all changes introduced by this merge:\n\n{commit.ShortHash} {commit.Subject}\n\nThe first parent (mainline) will be preserved.";
        }
        else
        {
            title = "Revert Commit?";
            message = $"This will create a new commit that undoes the changes from:\n\n{commit.ShortHash} {commit.Subject}\n\nThis is safe for shared branches.";
        }

        var confirmed = await ShowConfirmationAsync(title, message);
        if (!confirmed) return;

        StatusMessage = "Reverting commit…";
        try
        {
            await _git.RevertCommitAsync(RepoPath, commitHash);
            await LoadRepoAsync(RepoPath);
        }
        catch (GitException ex) when (ex.GitOutput.Contains("conflict", StringComparison.OrdinalIgnoreCase)
                                      || ex.GitOutput.Contains("CONFLICT", StringComparison.Ordinal))
        {
            StatusMessage = "Revert produced merge conflicts. Resolve and commit, or run `git revert --abort`.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Revert failed: {ex.Message}";
        }
    }

    // ── Mapping helpers ───────────────────────────────────────────────────────

    private CommitRowViewModel ToCommitRowViewModel(GraphNode node, int totalLanes)
    {
        _aiAttributions.TryGetValue(node.Hash, out var ai);
        return new CommitRowViewModel
        {
            Hash = node.Hash,
            Subject = node.Subject,
            AuthorName = node.AuthorName,
            AuthorDate = node.AuthorDate,
            RefNames = node.RefNames,
            Lane = node.Lane,
            Segments = node.Segments,
            TotalLanes = totalLanes,
            IsMergeCommit = node.ParentHashes.Length > 1,
            AiAgentName = ai?.AgentName ?? string.Empty,
            AiEvidenceDetail = ai?.Detail ?? string.Empty,
        };
    }

    private void LoadNextCommitPage()
    {
        if (_allGraphNodes == null) return;

        int end = Math.Min(_loadedCommitCount + CommitPageSize, _allGraphNodes.Count);
        for (int i = _loadedCommitCount; i < end; i++)
            Commits.Add(ToCommitRowViewModel(_allGraphNodes[i], _totalLanes));
        _loadedCommitCount = end;
        CanLoadMoreCommits = _loadedCommitCount < _allGraphNodes.Count;
    }

    [RelayCommand]
    private void LoadMoreCommits() => LoadNextCommitPage();

    [RelayCommand]
    private void ToggleGraph() => IsGraphVisible = !IsGraphVisible;

    [RelayCommand]
    private void ToggleConsole() => IsConsoleVisible = !IsConsoleVisible;

    // ── Commands: search ────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleSearch()
    {
        IsSearchVisible = !IsSearchVisible;
        if (!IsSearchVisible)
        {
            SearchQuery = string.Empty;
            RestoreFullCommitList();
        }
    }

    partial void OnSearchQueryChanged(string value)
    {
        _ = PerformSearchAsync(value);
    }

    private async Task PerformSearchAsync(string query)
    {
        if (string.IsNullOrEmpty(RepoPath)) return;

        if (string.IsNullOrWhiteSpace(query))
        {
            RestoreFullCommitList();
            return;
        }

        // Save current full list if not already saved
        _allCommits ??= Commits.ToList();

        IsSearching = true;
        try
        {
            var results = await _git.SearchCommitsAsync(RepoPath, query: query);
            var matchHashes = new HashSet<string>(results.Select(r => r.Hash));

            Commits.Clear();

            // Always show working tree row
            var workingRow = _allCommits.FirstOrDefault(c => c.IsWorkingTree);
            if (workingRow != null)
                Commits.Add(workingRow);

            // Show matching commits from the full list (preserves graph data)
            foreach (var c in _allCommits.Where(c => !c.IsWorkingTree && matchHashes.Contains(c.Hash)))
                Commits.Add(c);

            StatusMessage = $"Search: {Commits.Count - 1} result(s)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Search error: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    private void RestoreFullCommitList()
    {
        if (_allCommits == null) return;
        Commits.Clear();
        foreach (var c in _allCommits)
            Commits.Add(c);
        _allCommits = null;
        StatusMessage = $"{Commits.Count - 1} commit(s)";
    }

    // ── Commands: commit range comparison ───────────────────────────────────

    [RelayCommand]
    private void StartCompare(string? commitHash)
    {
        if (string.IsNullOrEmpty(commitHash) || commitHash == CommitRowViewModel.WorkingTreeHash) return;
        var commit = Commits.FirstOrDefault(c => c.Hash == commitHash);
        if (commit == null) return;

        if (CompareFromCommit == null)
        {
            CompareFromCommit = commit;
            IsComparing = true;
            CompareHeader = $"Comparing from {commit.ShortHash}… (select second commit)";
            ShowToast($"Select second commit to compare with {commit.ShortHash}", ToastSeverity.Info, 3000);
        }
        else
        {
            _ = LoadRangeComparisonAsync(CompareFromCommit.Hash, commitHash);
        }
    }

    [RelayCommand]
    private void CancelCompare()
    {
        CompareFromCommit = null;
        IsComparing = false;
        CompareHeader = string.Empty;
    }

    private async Task LoadRangeComparisonAsync(string fromHash, string toHash)
    {
        StatusMessage = "Loading range diff…";
        try
        {
            var raw = await _git.GetCommitRangeDiffAsync(RepoPath, fromHash, toHash);
            var parsed = UnifiedDiffParser.Parse(raw);

            // File list for the range. Uses the validated API rather than a raw
            // interpolated argument string, so both hashes go through ValidateHash
            // and are passed as discrete argv entries.
            var rangeFiles = await _git.GetCommitRangeFileListAsync(RepoPath, fromHash, toHash);
            ChangedFiles.Clear();
            StagedFiles.Clear();
            foreach (var change in rangeFiles)
            {
                ChangedFiles.Add(new FileChangeViewModel
                {
                    Path = change.Path,
                    StatusLabel = change.Status switch
                    {
                        FileChangeStatus.Added => "A",
                        FileChangeStatus.Deleted => "D",
                        FileChangeStatus.Renamed => "R",
                        FileChangeStatus.Copied => "C",
                        _ => "M",
                    }
                });
            }

            var fromCommit = Commits.FirstOrDefault(c => c.Hash == fromHash);
            var toCommit = Commits.FirstOrDefault(c => c.Hash == toHash);
            CompareHeader = $"Comparing {fromCommit?.ShortHash ?? fromHash[..7]}..{toCommit?.ShortHash ?? toHash[..7]} ({ChangedFiles.Count} files)";
            StatusMessage = $"Range diff: {ChangedFiles.Count} file(s) changed";
            ShowToast($"Comparing {ChangedFiles.Count} files across range", ToastSeverity.Info);
        }
        catch (Exception ex)
        {
            ShowToast($"Range diff failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    // ── Commands: tag management ────────────────────────────────────────────

    [RelayCommand]
    private void StartCreateTag(string? commitHash)
    {
        TagTargetCommit = commitHash;
        NewTagName = string.Empty;
        NewTagMessage = string.Empty;
        IsCreatingTag = true;
    }

    [RelayCommand]
    private async Task ConfirmCreateTagAsync()
    {
        if (string.IsNullOrWhiteSpace(NewTagName) || string.IsNullOrEmpty(RepoPath)) return;
        try
        {
            var message = string.IsNullOrWhiteSpace(NewTagMessage) ? null : NewTagMessage;
            await _git.CreateTagAsync(RepoPath, NewTagName.Trim(), message, TagTargetCommit);
            IsCreatingTag = false;
            ShowToast($"Tag '{NewTagName.Trim()}' created", ToastSeverity.Success);
            await LoadTagsAsync();
        }
        catch (Exception ex)
        {
            ShowToast($"Create tag failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    [RelayCommand]
    private void CancelCreateTag() => IsCreatingTag = false;

    [RelayCommand]
    private async Task DeleteTagAsync(string? tagName)
    {
        if (string.IsNullOrEmpty(tagName) || string.IsNullOrEmpty(RepoPath)) return;
        var confirmed = await ShowConfirmationAsync("Delete Tag?", $"Delete tag '{tagName}'? This only removes the local tag.");
        if (!confirmed) return;
        try
        {
            await _git.DeleteTagAsync(RepoPath, tagName);
            ShowToast($"Tag '{tagName}' deleted", ToastSeverity.Success);
            await LoadTagsAsync();
        }
        catch (Exception ex)
        {
            ShowToast($"Delete tag failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    [RelayCommand]
    private async Task PushTagAsync(string? tagName)
    {
        if (string.IsNullOrEmpty(tagName) || string.IsNullOrEmpty(RepoPath)) return;
        try
        {
            await _git.PushTagAsync(RepoPath, tagName);
            ShowToast($"Tag '{tagName}' pushed", ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"Push tag failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    private async Task LoadTagsAsync()
    {
        try
        {
            var tags = await _git.GetTagsAsync(RepoPath);
            Tags.Clear();
            foreach (var t in tags)
                Tags.Add(new TagViewModel { Name = t.Name, ShortHash = t.ShortHash, Message = t.Message });
        }
        catch { }
    }

    // ── Commands: settings ─────────────────────────────────────────────────

    [RelayCommand]
    private async Task ToggleSettingsAsync()
    {
        if (!IsSettingsVisible)
            await LoadSettingsAsync();
        IsSettingsVisible = !IsSettingsVisible;
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            // Save git identity
            var userName = SettingsGitUserName.Trim();
            var userEmail = SettingsGitUserEmail.Trim();
            if (!string.IsNullOrEmpty(userName))
                await GitConfigService.SetGlobalConfigAsync("user.name", userName);
            if (!string.IsNullOrEmpty(userEmail))
                await GitConfigService.SetGlobalConfigAsync("user.email", userEmail);

            // Save app settings
            var settings = AppSettings.Load();
            settings.DefaultRemote = SettingsDefaultRemote.Trim();
            settings.Theme = SettingsTheme.ToLowerInvariant();

            if (int.TryParse(SettingsTerminalFontSize, out var fontSize) && fontSize > 0)
                settings.TerminalFontSize = fontSize;
            if (int.TryParse(SettingsDiffContextLines, out var contextLines) && contextLines >= 0)
                settings.DiffContextLines = contextLines;
            if (int.TryParse(SettingsAutoFetchInterval, out var fetchInterval) && fetchInterval >= 0)
                settings.AutoFetchIntervalSeconds = fetchInterval;

            settings.Save();
            IsSettingsVisible = false;
            ShowToast("Settings saved", Controls.ToastSeverity.Info);
        }
        catch (Exception ex)
        {
            ShowToast($"Failed to save settings: {ex.Message}", Controls.ToastSeverity.Error);
        }
    }

    [RelayCommand]
    private void CancelSettings()
    {
        IsSettingsVisible = false;
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            var settings = AppSettings.Load();
            SettingsDefaultRemote = settings.DefaultRemote;
            SettingsTerminalFontSize = settings.TerminalFontSize.ToString();
            SettingsDiffContextLines = settings.DiffContextLines.ToString();
            SettingsTheme = settings.Theme == "light" ? "Light" : "Dark";
            SettingsAutoFetchInterval = settings.AutoFetchIntervalSeconds.ToString();

            SettingsGitUserName = await GitConfigService.GetGlobalConfigAsync("user.name");
            SettingsGitUserEmail = await GitConfigService.GetGlobalConfigAsync("user.email");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading settings: {ex.Message}";
        }
    }

    // ── Commands: discard changes ────────────────────────────────────────────

    [RelayCommand]
    private async Task DiscardSelectedAsync()
    {
        if (string.IsNullOrEmpty(RepoPath) || SelectedFile is null) return;
        if (!SelectedFile.IsWorkingTreeFile || SelectedFile.IsStaged) return;

        var confirmed = await ShowConfirmationAsync(
            "Discard Changes?",
            $"This will permanently discard all unstaged changes to:\n\n{SelectedFile.Path}\n\nThis cannot be undone.");
        if (!confirmed) return;

        StatusMessage = $"Discarding changes to {SelectedFile.Path}…";
        try
        {
            if (SelectedFile.StatusLabel == "?")
                await _git.RemoveUntrackedFileAsync(RepoPath, SelectedFile.Path);
            else
                await _git.DiscardFileChangesAsync(RepoPath, SelectedFile.Path);
            await LoadWorkingTreeFilesAsync();
            StatusMessage = "Changes discarded.";
        }
        catch (Exception ex) { StatusMessage = $"Discard failed: {ex.Message}"; }
    }

    public async Task DiscardFilesAsync(IEnumerable<FileChangeViewModel> files)
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        var fileList = files.Where(f => f.IsWorkingTreeFile && !f.IsStaged).ToList();
        if (fileList.Count == 0) return;

        var confirmed = await ShowConfirmationAsync(
            "Discard Changes?",
            $"This will permanently discard all unstaged changes to {fileList.Count} file(s).\n\nThis cannot be undone.");
        if (!confirmed) return;

        StatusMessage = $"Discarding {fileList.Count} file(s)…";
        try
        {
            foreach (var f in fileList)
            {
                if (f.StatusLabel == "?")
                    await _git.RemoveUntrackedFileAsync(RepoPath, f.Path);
                else
                    await _git.DiscardFileChangesAsync(RepoPath, f.Path);
            }
            await LoadWorkingTreeFilesAsync();
            StatusMessage = $"Discarded {fileList.Count} file(s).";
        }
        catch (Exception ex) { StatusMessage = $"Discard failed: {ex.Message}"; }
    }

    private static FileChangeViewModel ToFileChangeViewModel(FileChange fc) => new()
    {
        Path = fc.Path,
        OldPath = fc.OldPath ?? string.Empty,
        StatusLabel = MapStatusLabel(fc.Status)
    };

    private FileChangeViewModel ToWorkingTreeFileChangeViewModel(FileChange fc)
    {
        var filePath = fc.Path;
        var isStaged = fc.IsStaged;
        var repoPath = RepoPath;

        return new FileChangeViewModel
        {
            Path = filePath,
            OldPath = fc.OldPath ?? string.Empty,
            StatusLabel = MapStatusLabel(fc.Status),
            IsStaged = isStaged,
            IsWorkingTreeFile = true,
            ToggleStagingCommand = new AsyncRelayCommand(async () =>
            {
                try
                {
                    if (isStaged)
                    {
                        // Unstaging is a corrective action — keep the file selected so
                        // the user can see what they just put back.
                        await _git.UnstageFileAsync(repoPath, filePath);
                        await LoadWorkingTreeFilesAsync();
                    }
                    else
                    {
                        // Staging means "done with this one", so advance to the next
                        // unstaged file. Capture the position first: the reload rebuilds
                        // the collections and this file leaves the unstaged list.
                        var position = IndexOfUnstaged(filePath);

                        await _git.StageFileAsync(repoPath, filePath);
                        await LoadWorkingTreeFilesAsync();

                        SelectUnstagedAt(position);
                    }
                }
                catch (Exception ex)
                {
                    StatusMessage = $"Staging error: {ex.Message}";
                }
            })
        };
    }

    /// <summary>Position of a path in the unstaged list, or -1 if absent.</summary>
    private int IndexOfUnstaged(string filePath)
    {
        for (var i = 0; i < ChangedFiles.Count; i++)
        {
            if (string.Equals(ChangedFiles[i].Path, filePath, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Selects the unstaged file that has taken the given position after a stage.
    ///
    /// The staged file has been removed from the list, so whatever followed it has
    /// shifted down into its index — selecting that index lands on the next file
    /// without needing to track identity across the reload. Staging the last file
    /// keeps the selection on the new last entry rather than clearing it, so the diff
    /// pane does not blank out mid-review.
    /// </summary>
    private void SelectUnstagedAt(int position)
    {
        if (position < 0) return;

        if (ChangedFiles.Count == 0)
        {
            SelectedFile = null;
            return;
        }

        SelectedFile = ChangedFiles[Math.Min(position, ChangedFiles.Count - 1)];
    }

    private static string MapStatusLabel(FileChangeStatus status) => status switch
    {
        FileChangeStatus.Added => "A",
        FileChangeStatus.Modified => "M",
        FileChangeStatus.Deleted => "D",
        FileChangeStatus.Renamed => "R",
        FileChangeStatus.Copied => "C",
        FileChangeStatus.Untracked => "?",
        FileChangeStatus.Conflicted => "U",
        _ => "?"
    };

    // ── Conflict resolution ─────────────────────────────────────────────────

    public ObservableCollection<FileChangeViewModel> ConflictedFiles { get; } = new();

    [ObservableProperty] private bool _hasConflicts;
    [ObservableProperty] private bool _isConflictResolverVisible;
    [ObservableProperty] private string _conflictOursContent = string.Empty;
    [ObservableProperty] private string _conflictTheirsContent = string.Empty;
    [ObservableProperty] private string _conflictResultContent = string.Empty;
    [ObservableProperty] private string? _conflictFilePath;

    [RelayCommand]
    private async Task ShowConflictResolverAsync(string? filePath)
    {
        if (string.IsNullOrEmpty(RepoPath) || string.IsNullOrEmpty(filePath)) return;

        StatusMessage = "Loading conflict versions…";
        try
        {
            var oursTask = _git.GetConflictVersionAsync(RepoPath, filePath, ConflictSide.Ours);
            var theirsTask = _git.GetConflictVersionAsync(RepoPath, filePath, ConflictSide.Theirs);
            await Task.WhenAll(oursTask, theirsTask);

            ConflictOursContent = oursTask.Result;
            ConflictTheirsContent = theirsTask.Result;
            ConflictResultContent = oursTask.Result; // Default to ours
            ConflictFilePath = filePath;
            IsConflictResolverVisible = true;
            StatusMessage = $"Resolving conflict: {filePath}";
        }
        catch (Exception ex)
        {
            ShowToast($"Failed to load conflict: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    [RelayCommand]
    private void AcceptOurs()
    {
        ConflictResultContent = ConflictOursContent;
    }

    [RelayCommand]
    private void AcceptTheirs()
    {
        ConflictResultContent = ConflictTheirsContent;
    }

    [RelayCommand]
    private void AcceptBoth()
    {
        ConflictResultContent = ConflictOursContent + "\n" + ConflictTheirsContent;
    }

    [RelayCommand]
    private async Task MarkResolvedAsync()
    {
        if (string.IsNullOrEmpty(RepoPath) || string.IsNullOrEmpty(ConflictFilePath)) return;

        try
        {
            // Write the result content to the file
            var fullPath = Path.GetFullPath(Path.Combine(RepoPath, ConflictFilePath));
            var repoRoot = Path.GetFullPath(RepoPath) + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(repoRoot, StringComparison.OrdinalIgnoreCase))
            {
                ShowToast("File path escapes repository root.", ToastSeverity.Error);
                return;
            }

            // An absent conflict stage yields an empty string (normal for add/add and
            // "added by them" conflicts, where :2: does not exist), and that empty
            // string is the default result. Writing it would silently destroy the file
            // and stage the destruction behind a success toast, so confirm first —
            // deliberately emptying a file is legitimate, doing so by accident is not.
            if (string.IsNullOrEmpty(ConflictResultContent))
            {
                var confirmed = await ShowConfirmationAsync(
                    "Write an empty file?",
                    $"The resolved content for:\n\n{ConflictFilePath}\n\nis empty. Marking it resolved "
                    + "will overwrite the file with nothing and stage that.\n\nThis cannot be undone.");
                if (!confirmed) return;
            }

            await File.WriteAllTextAsync(fullPath, ConflictResultContent);
            await _git.MarkConflictResolvedAsync(RepoPath, ConflictFilePath);

            IsConflictResolverVisible = false;
            ConflictFilePath = null;
            ShowToast("Conflict resolved", ToastSeverity.Success);

            // Refresh working tree
            await LoadWorkingTreeFilesAsync();
        }
        catch (Exception ex)
        {
            ShowToast($"Resolve failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    [RelayCommand]
    private async Task AbortMergeAsync()
    {
        if (string.IsNullOrEmpty(RepoPath)) return;

        var confirmed = await ShowConfirmationAsync(
            "Abort Merge?",
            "This will abort the current merge and discard all conflict resolutions. Are you sure?");
        if (!confirmed) return;

        try
        {
            await _git.AbortMergeAsync(RepoPath);
            IsConflictResolverVisible = false;
            ConflictFilePath = null;
            HasConflicts = false;
            ConflictedFiles.Clear();
            ShowToast("Merge aborted", ToastSeverity.Success);
            await LoadRepoAsync(RepoPath);
        }
        catch (Exception ex)
        {
            ShowToast($"Abort merge failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    // ── Commands: GitHub / Pull Requests ────────────────────────────────────

    [RelayCommand]
    private async Task TogglePrPanelAsync()
    {
        IsPrPanelVisible = !IsPrPanelVisible;
        if (IsPrPanelVisible && !_prsFetched)
            await LoadPullRequestsAsync();
    }

    [RelayCommand]
    private async Task RefreshPrsAsync()
    {
        await LoadPullRequestsAsync();
    }

    private async Task LoadPullRequestsAsync()
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        IsLoadingPrs = true;
        try
        {
            var prs = await _github.GetPullRequestsAsync(RepoPath);
            PullRequests.Clear();
            foreach (var pr in prs)
            {
                var labels = pr.Labels?.Select(l => l.Name) ?? Enumerable.Empty<string>();
                PullRequests.Add(new PullRequestViewModel
                {
                    Number = pr.Number,
                    Title = pr.Title,
                    AuthorLogin = pr.User?.Login ?? "unknown",
                    State = pr.State.StringValue,
                    CreatedAt = pr.CreatedAt,
                    HeadBranch = pr.Head?.Ref ?? string.Empty,
                    BaseBranch = pr.Base?.Ref ?? string.Empty,
                    IsDraft = pr.Draft,
                    Labels = string.Join(", ", labels)
                });
            }
            _prsFetched = true;
            StatusMessage = $"{prs.Count} open PR(s)";
        }
        catch (Exception ex)
        {
            ShowToast($"Load PRs failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
        finally
        {
            IsLoadingPrs = false;
        }
    }

    [RelayCommand]
    private void ShowCreatePr()
    {
        NewPrTitle = string.Empty;
        NewPrBody = string.Empty;
        NewPrBaseBranch = Branches.FirstOrDefault(b => b == "main")
                          ?? Branches.FirstOrDefault(b => b == "master")
                          ?? Branches.FirstOrDefault();
        NewPrIsDraft = false;
        IsCreatePrVisible = true;
    }

    [RelayCommand]
    private void CancelCreatePr()
    {
        IsCreatePrVisible = false;
    }

    [RelayCommand]
    private async Task SubmitPrAsync()
    {
        if (string.IsNullOrEmpty(RepoPath) || string.IsNullOrWhiteSpace(NewPrTitle) || string.IsNullOrEmpty(NewPrBaseBranch))
        {
            ShowToast("Title and base branch are required.", ToastSeverity.Warning);
            return;
        }

        StatusMessage = "Creating pull request...";
        try
        {
            var pr = await _github.CreatePullRequestAsync(
                RepoPath, NewPrTitle.Trim(), NewPrBody.Trim(), CurrentBranch, NewPrBaseBranch, NewPrIsDraft);
            IsCreatePrVisible = false;
            ShowToast($"PR #{pr.Number} created: {pr.Title}", ToastSeverity.Success, 6000);
            _prsFetched = false;
            if (IsPrPanelVisible)
                await LoadPullRequestsAsync();
        }
        catch (Exception ex)
        {
            ShowToast($"Create PR failed: {ex.Message}", ToastSeverity.Error, 8000);
        }
    }

    // ── Issue linking (scan commit subject for #N references) ───────────────

    [RelayCommand]
    private async Task FetchLinkedIssuesAsync()
    {
        if (SelectedCommit is null || SelectedCommit.IsWorkingTree) return;
        await LoadLinkedIssuesAsync(SelectedCommit.Subject);
    }

    private async Task LoadLinkedIssuesAsync(string commitSubject)
    {
        if (string.IsNullOrEmpty(RepoPath)) return;

        var issueNumbers = GitHubService.ParseIssueReferences(commitSubject);
        if (issueNumbers.Count == 0) return;

        try
        {
            var issues = await _github.GetIssuesByNumbersAsync(RepoPath, issueNumbers);
            LinkedIssues.Clear();
            foreach (var issue in issues)
            {
                var labels = issue.Labels?.Select(l => l.Name) ?? Enumerable.Empty<string>();
                LinkedIssues.Add(new IssueViewModel
                {
                    Number = issue.Number,
                    Title = issue.Title,
                    State = issue.State.StringValue,
                    Labels = string.Join(", ", labels)
                });
            }
        }
        catch
        {
            // Issue linking is non-critical — silently ignore errors
        }
    }

    // ── Interactive Rebase ──────────────────────────────────────────────────────

    public ObservableCollection<RebaseEntryViewModel> RebaseEntries { get; } = new();

    [ObservableProperty] private bool _isRebaseVisible;
    [ObservableProperty] private string _rebaseOntoCommit = string.Empty;
    [ObservableProperty] private string _rebaseOntoDisplay = string.Empty;
    [ObservableProperty] private bool _isRebaseInProgress;

    [RelayCommand]
    private async Task StartInteractiveRebaseAsync(string? commitHash)
    {
        if (string.IsNullOrEmpty(RepoPath) || string.IsNullOrEmpty(commitHash)) return;
        if (commitHash == CommitRowViewModel.WorkingTreeHash) return;

        StatusMessage = "Loading rebase commits...";
        try
        {
            var entries = await _git.GetRebaseCommitsAsync(RepoPath, commitHash);
            if (entries.Count == 0)
            {
                ShowToast("No commits to rebase (HEAD is at or before the selected commit).", ToastSeverity.Info);
                return;
            }

            RebaseEntries.Clear();
            foreach (var entry in entries)
            {
                RebaseEntries.Add(new RebaseEntryViewModel
                {
                    Hash = entry.Hash,
                    Subject = entry.Subject,
                    SelectedAction = RebaseActionType.Pick
                });
            }

            RebaseOntoCommit = commitHash;
            var commit = Commits.FirstOrDefault(c => c.Hash == commitHash);
            RebaseOntoDisplay = commit?.ShortHash ?? commitHash[..Math.Min(7, commitHash.Length)];
            IsRebaseVisible = true;
            StatusMessage = $"{entries.Count} commit(s) to rebase";
        }
        catch (Exception ex)
        {
            ShowToast($"Failed to load rebase commits: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    [RelayCommand]
    private async Task ExecuteRebaseAsync()
    {
        if (string.IsNullOrEmpty(RepoPath) || string.IsNullOrEmpty(RebaseOntoCommit)) return;
        if (RebaseEntries.Count == 0) return;

        var confirmed = await ShowConfirmationAsync(
            "Start Interactive Rebase?",
            $"This will rebase {RebaseEntries.Count} commit(s) onto {RebaseOntoDisplay}.\n\nThis rewrites history. If these commits have been pushed, you will need to force-push.");
        if (!confirmed) return;

        StatusMessage = "Rebasing...";
        try
        {
            var actions = RebaseEntries.Select(e =>
                new RebaseAction(e.SelectedAction, e.Hash, e.Subject)).ToList();

            await _git.ExecuteRebaseAsync(RepoPath, RebaseOntoCommit, actions);

            IsRebaseVisible = false;
            RebaseEntries.Clear();

            // Check if rebase paused
            IsRebaseInProgress = await _git.IsRebaseInProgressAsync(RepoPath);
            if (IsRebaseInProgress)
            {
                ShowToast("Rebase paused — resolve conflicts or continue.", ToastSeverity.Info, 6000);
            }
            else
            {
                ShowToast("Rebase completed successfully!", ToastSeverity.Success);
            }

            await LoadRepoAsync(RepoPath);
        }
        catch (Exception ex)
        {
            ShowToast($"Rebase failed: {ex.Message}", ToastSeverity.Error, 6000);
            // Check if rebase is in progress (paused on conflict)
            try
            {
                IsRebaseInProgress = await _git.IsRebaseInProgressAsync(RepoPath);
                if (IsRebaseInProgress)
                {
                    IsRebaseVisible = false;
                    RebaseEntries.Clear();
                    await LoadRepoAsync(RepoPath);
                }
            }
            catch { }
        }
    }

    [RelayCommand]
    private void CancelRebase()
    {
        IsRebaseVisible = false;
        RebaseEntries.Clear();
        RebaseOntoCommit = string.Empty;
        RebaseOntoDisplay = string.Empty;
    }

    [RelayCommand]
    private async Task ContinueRebaseAsync()
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        StatusMessage = "Continuing rebase...";
        try
        {
            await _git.ContinueRebaseAsync(RepoPath);
            IsRebaseInProgress = await _git.IsRebaseInProgressAsync(RepoPath);
            if (!IsRebaseInProgress)
                ShowToast("Rebase completed successfully!", ToastSeverity.Success);
            else
                ShowToast("Rebase paused again — resolve conflicts or continue.", ToastSeverity.Info, 6000);
            await LoadRepoAsync(RepoPath);
        }
        catch (Exception ex)
        {
            ShowToast($"Rebase continue failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    [RelayCommand]
    private async Task AbortRebaseAsync()
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        var confirmed = await ShowConfirmationAsync(
            "Abort Rebase?",
            "This will abort the current rebase and restore the branch to its original state.");
        if (!confirmed) return;

        StatusMessage = "Aborting rebase...";
        try
        {
            await _git.AbortRebaseAsync(RepoPath);
            IsRebaseInProgress = false;
            ShowToast("Rebase aborted", ToastSeverity.Success);
            await LoadRepoAsync(RepoPath);
        }
        catch (Exception ex)
        {
            ShowToast($"Abort rebase failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    [RelayCommand]
    private async Task SkipRebaseAsync()
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        StatusMessage = "Skipping commit...";
        try
        {
            await _git.SkipRebaseAsync(RepoPath);
            IsRebaseInProgress = await _git.IsRebaseInProgressAsync(RepoPath);
            if (!IsRebaseInProgress)
                ShowToast("Rebase completed successfully!", ToastSeverity.Success);
            else
                ShowToast("Rebase paused — resolve conflicts or continue.", ToastSeverity.Info, 6000);
            await LoadRepoAsync(RepoPath);
        }
        catch (Exception ex)
        {
            ShowToast($"Skip failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    [RelayCommand]
    private void MoveRebaseEntryUp(RebaseEntryViewModel? entry)
    {
        if (entry is null) return;
        var index = RebaseEntries.IndexOf(entry);
        if (index <= 0) return;
        RebaseEntries.Move(index, index - 1);
    }

    [RelayCommand]
    private void MoveRebaseEntryDown(RebaseEntryViewModel? entry)
    {
        if (entry is null) return;
        var index = RebaseEntries.IndexOf(entry);
        if (index < 0 || index >= RebaseEntries.Count - 1) return;
        RebaseEntries.Move(index, index + 1);
    }

    // ── Commands: blame ─────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ShowBlameAsync(string? filePath)
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        var path = filePath ?? SelectedFile?.Path;
        if (string.IsNullOrEmpty(path)) return;
        StatusMessage = "Loading blame...";
        try
        {
            string? commitHash = null;
            if (SelectedCommit != null && !SelectedCommit.IsWorkingTree)
                commitHash = SelectedCommit.Hash;
            var blameLines = await _git.GetBlameAsync(RepoPath, path, commitHash);
            BlameData = blameLines;
            BlameFilePath = path;
            BlameCommitHash = commitHash;
            IsBlameVisible = true;
            StatusMessage = $"Blame: {blameLines.Count} line(s)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Blame failed: {ex.Message}";
            ShowToast($"Blame failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    [RelayCommand]
    private void CloseBlame()
    {
        IsBlameVisible = false;
        BlameData = null;
        BlameFilePath = null;
    }

    public void NavigateToBlameCommit(string commitHash)
    {
        IsBlameVisible = false;
        BlameData = null;
        var commit = Commits.FirstOrDefault(c => c.Hash == commitHash);
        if (commit != null)
            SelectedCommit = commit;
    }

    // ── Commands: file history ──────────────────────────────────────────────

    [RelayCommand]
    private async Task ShowFileHistoryAsync(string? filePath)
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        var path = filePath ?? SelectedFile?.Path;
        if (string.IsNullOrEmpty(path)) return;
        StatusMessage = "Loading file history...";
        try
        {
            var commits = await _git.GetFileHistoryAsync(RepoPath, path);
            FileHistoryCommits.Clear();
            foreach (var c in commits)
            {
                FileHistoryCommits.Add(new CommitRowViewModel
                {
                    Hash = c.Hash, Subject = c.Subject, AuthorName = c.AuthorName,
                    AuthorDate = c.AuthorDate, RefNames = c.RefNames,
                    IsMergeCommit = c.ParentHashes.Length > 1
                });
            }
            FileHistoryPath = path;
            IsFileHistoryVisible = true;
            StatusMessage = $"History: {commits.Count} commit(s) for {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"File history failed: {ex.Message}";
            ShowToast($"File history failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    [RelayCommand]
    private void CloseFileHistory()
    {
        IsFileHistoryVisible = false;
        FileHistoryCommits.Clear();
        FileHistoryPath = string.Empty;
        SelectedFileHistoryCommit = null;
    }

    partial void OnSelectedFileHistoryCommitChanged(CommitRowViewModel? value)
    {
        if (value == null || string.IsNullOrEmpty(RepoPath) || string.IsNullOrEmpty(FileHistoryPath))
            return;
        _ = LoadFileHistoryDiffAsync(value.Hash, FileHistoryPath);
    }

    private async Task LoadFileHistoryDiffAsync(string commitHash, string filePath)
    {
        StatusMessage = "Loading diff...";
        try
        {
            var raw = await _git.GetFileDiffAsync(RepoPath, commitHash, filePath);
            var parsed = UnifiedDiffParser.Parse(raw);
            DiffFilePath = filePath;
            CurrentDiff = parsed;
            DiffHunks.Clear();
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading diff: {ex.Message}";
        }
    }
}
