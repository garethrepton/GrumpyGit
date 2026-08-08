using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrumpyGit.App.Services;
using GrumpyGit.Core.Shell;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// Partial class — the repository tree, worktree management and the quick switcher.
///
/// This replaced a horizontal tab bar. Tabs flattened a hierarchy that is genuinely
/// nested: a linked worktree belongs to a repository, and five tabs where three were
/// worktrees of the same repo gave no clue they were related. The tree keeps a single
/// root per repository and hangs that repository's worktrees underneath it, so opening
/// a worktree never produces a second root.
/// </summary>
public partial class MainWindowViewModel
{
    public MainWindowViewModel()
    {
        LoadRecentRepos();

        // HasStagedFiles gates the "Unstage all" affordance, and StagedFiles is
        // rebuilt wholesale on every refresh, so it has to be recomputed from the
        // collection rather than set at any single call site.
        StagedFiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasStagedFiles));
        RepoNodes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRepoNodes));

        // Reads the configured model path only — the weights themselves load lazily, on
        // the first diff, so startup pays nothing for a feature that may not be used.
        InitialiseReviewModuleFromSettings();

        // Reopen last session's repositories. Deferred to the dispatcher so the
        // constructor returns immediately and the window paints before repo I/O
        // begins — otherwise startup blocks on git for every restored repo.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => _ = RestoreOpenReposAsync(),
            Avalonia.Threading.DispatcherPriority.Background);
    }

    // ── Repository tree ───────────────────────────────────────────────────────

    /// <summary>Repository roots. Only main working directories appear here.</summary>
    public ObservableCollection<RepoNodeViewModel> RepoNodes { get; } = new();

    /// <summary>The node whose path is currently loaded — a root or a worktree child.</summary>
    [ObservableProperty] private RepoTreeNodeViewModel? _activeNode;

    public bool HasRepoNodes => RepoNodes.Count > 0;

    /// <summary>
    /// True when the loaded repository is a linked worktree. Drives the branch lock in
    /// the UI; <c>GitService</c> enforces the same rule independently so it cannot be
    /// bypassed by a call site that forgets to check.
    /// </summary>
    [ObservableProperty] private bool _isActiveRepoWorktree;

    /// <summary>Branch a locked worktree is pinned to, for the banner text.</summary>
    [ObservableProperty] private string _activeWorktreeBranch = string.Empty;

    /// <summary>Every node in the tree, roots and worktrees, in display order.</summary>
    private IEnumerable<RepoTreeNodeViewModel> FlattenedNodes()
    {
        foreach (var repo in RepoNodes)
        {
            yield return repo;
            foreach (var wt in repo.Worktrees)
                yield return wt;
        }
    }

    /// <summary>
    /// Finds or creates the root node for a repository. Takes the <em>main</em> worktree
    /// path so opening a linked worktree resolves to the repository that owns it rather
    /// than adding a rootless second entry.
    /// </summary>
    private RepoNodeViewModel EnsureRepoNode(string mainPath)
    {
        var existing = RepoNodes.FirstOrDefault(n =>
            string.Equals(n.Path, mainPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        var node = new RepoNodeViewModel { Path = mainPath, IsExpanded = true };
        RepoNodes.Add(node);
        return node;
    }

    /// <summary>
    /// Resolves the main working directory for any path inside a repository. A linked
    /// worktree reports the main worktree first in <c>git worktree list</c>, which makes
    /// this a single call rather than a walk up the directory tree.
    /// </summary>
    private async Task<string> ResolveMainWorktreePathAsync(string path)
    {
        try
        {
            var worktrees = await _git.GetWorktreesAsync(path);
            return worktrees.Count > 0 ? worktrees[0].Path : path;
        }
        catch
        {
            // Not a repository, or a git too old to answer. Treat the path as its own
            // root so the tree still shows something the user can act on.
            return path;
        }
    }

    /// <summary>
    /// The single entry point for opening a repository or worktree. Resolves the owning
    /// repository, refreshes its worktrees, marks the matching node active and loads it.
    /// </summary>
    public async Task OpenRepositoryAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            StatusMessage = $"Path no longer exists: {path}";
            return;
        }

        var mainPath = await ResolveMainWorktreePathAsync(path);
        var repoNode = EnsureRepoNode(mainPath);

        await RefreshRepoChildrenAsync(repoNode);
        SetActiveNodeForPath(path);

        AppSettings.Load().AddRecentRepo(mainPath);
        LoadRecentRepos();
        PersistOpenRepos();

        await LoadRepoAsync(path);
    }

    /// <summary>
    /// Bound to the TreeView's selection. Selecting a row loads it, which also gives
    /// arrow-key navigation for free.
    /// </summary>
    [ObservableProperty] private RepoTreeNodeViewModel? _selectedTreeNode;

    /// <summary>
    /// Set while the viewmodel is driving the selection itself (restore, activation,
    /// worktree removal). Without it, syncing the highlight would re-enter
    /// <see cref="ActivateNodeAsync"/> and reload the repository a second time.
    /// </summary>
    private bool _suppressTreeSelection;

    partial void OnSelectedTreeNodeChanged(RepoTreeNodeViewModel? value)
    {
        if (_suppressTreeSelection || value is null) return;

        switch (value)
        {
            // Headings are structure, not destinations — selecting one only expands it.
            case RepoGroupNodeViewModel:
                value.IsExpanded = !value.IsExpanded;
                break;
            case BranchNodeViewModel branch:
                _ = ActivateBranchAsync(branch);
                break;
            default:
                _ = ActivateNodeAsync(value);
                break;
        }
    }

    /// <summary>
    /// Selecting a branch checks it out. If a worktree already holds it, jump to that
    /// worktree instead: git refuses to check one branch out twice, and the worktree is
    /// where that branch actually lives.
    /// </summary>
    private async Task ActivateBranchAsync(BranchNodeViewModel branch)
    {
        if (branch.HasWorktree)
        {
            await OpenRepositoryAsync(branch.WorktreePath!);
            return;
        }

        if (branch.IsCurrent)
        {
            await OpenRepositoryAsync(branch.RepoPath);
            return;
        }

        StatusMessage = $"Switching to {branch.Branch}…";
        try
        {
            await _git.CheckoutBranchAsync(branch.RepoPath, branch.Branch);
            await OpenRepositoryAsync(branch.RepoPath);
            StatusMessage = $"Switched to {branch.Branch}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not switch branch: {ex.Message}";
        }
    }

    /// <summary>Highlights whichever node owns <paramref name="path"/>, clearing the rest.</summary>
    private void SetActiveNodeForPath(string path)
    {
        RepoTreeNodeViewModel? match = null;

        foreach (var node in FlattenedNodes())
        {
            var isMatch = string.Equals(node.Path, path, StringComparison.OrdinalIgnoreCase);
            node.IsActive = isMatch;
            if (isMatch) match = node;
        }

        ActiveNode = match;

        // Keep a repository expanded while one of its worktrees is the active node,
        // otherwise activating a worktree hides the very row that just lit up.
        if (match is WorktreeNodeViewModel wt)
        {
            var owner = RepoNodes.FirstOrDefault(r =>
                string.Equals(r.Path, wt.RepoPath, StringComparison.OrdinalIgnoreCase));
            if (owner is not null) owner.IsExpanded = true;
        }

        _suppressTreeSelection = true;
        SelectedTreeNode = match;
        _suppressTreeSelection = false;
    }

    /// <summary>Click handler for any row in the tree.</summary>
    [RelayCommand]
    private async Task ActivateNodeAsync(RepoTreeNodeViewModel? node)
    {
        if (node is null) return;
        if (node.IsActive && string.Equals(RepoPath, node.Path, StringComparison.OrdinalIgnoreCase))
            return;

        if (!Directory.Exists(node.Path))
        {
            StatusMessage = $"{node.DisplayName} — directory is missing on disk.";
            return;
        }

        SetActiveNodeForPath(node.Path);
        PersistOpenRepos();
        await LoadRepoAsync(node.Path);
    }

    [RelayCommand]
    private async Task AddRepositoryAsync()
    {
        if (OwnerWindow is null) return;

        var results = await OwnerWindow.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Add Git Repository", AllowMultiple = false });
        if (results.Count == 0) return;

        var path = results[0].TryGetLocalPath() ?? results[0].Path.LocalPath;
        await OpenRepositoryAsync(path);
    }

    /// <summary>
    /// Opens a checkout's directory in Explorer. Takes the base node so one command serves
    /// both a repository root and a worktree; group headings and worktree-less branches
    /// have no directory and are ignored.
    /// </summary>
    [RelayCommand]
    private void OpenInExplorer(RepoTreeNodeViewModel? node)
    {
        if (node is null || string.IsNullOrEmpty(node.Path)) return;

        try
        {
            FileExplorer.OpenDirectory(node.Path);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open folder: {ex.Message}";
        }
    }

    /// <summary>
    /// Removes a repository from the tree. This only closes it in the UI — worktrees on
    /// disk are left alone, since closing a view should never delete a checkout.
    /// </summary>
    [RelayCommand]
    private async Task CloseRepoNodeAsync(RepoNodeViewModel? node)
    {
        if (node is null) return;

        var wasActive = node.IsActive || node.Worktrees.Any(w => w.IsActive);
        RepoNodes.Remove(node);

        if (!wasActive)
        {
            PersistOpenRepos();
            return;
        }

        var next = RepoNodes.LastOrDefault();
        if (next is not null)
        {
            SetActiveNodeForPath(next.Path);
            PersistOpenRepos();
            await LoadRepoAsync(next.Path);
            return;
        }

        ClearLoadedRepository();
        PersistOpenRepos();
    }

    /// <summary>Resets everything that describes a loaded repository.</summary>
    private void ClearLoadedRepository()
    {
        ActiveNode = null;
        RepoPath = string.Empty;
        Commits.Clear();
        ChangedFiles.Clear();
        StagedFiles.Clear();
        AiSessions.Clear();
        HasAiSessions = false;
        CurrentBranch = "No repo";
        IsActiveRepoWorktree = false;
        ActiveWorktreeBranch = string.Empty;
    }

    /// <summary>Ctrl+W — close the repository that owns the active node.</summary>
    [RelayCommand]
    private async Task CloseActiveRepoAsync()
    {
        var owner = ActiveNode switch
        {
            RepoNodeViewModel repo => repo,
            WorktreeNodeViewModel wt => RepoNodes.FirstOrDefault(r =>
                string.Equals(r.Path, wt.RepoPath, StringComparison.OrdinalIgnoreCase)),
            _ => null,
        };
        await CloseRepoNodeAsync(owner);
    }

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab — walk the flattened tree.</summary>
    [RelayCommand]
    private async Task CycleRepoAsync(string? direction)
    {
        var nodes = FlattenedNodes().ToList();
        if (nodes.Count < 2) return;

        var index = ActiveNode is null ? 0 : nodes.IndexOf(ActiveNode);
        index += string.Equals(direction, "prev", StringComparison.OrdinalIgnoreCase) ? -1 : 1;

        if (index < 0) index = nodes.Count - 1;
        if (index >= nodes.Count) index = 0;

        await ActivateNodeAsync(nodes[index]);
    }

    /// <summary>Ctrl+1..9 — jump to the nth repository root.</summary>
    [RelayCommand]
    private async Task SwitchToRepoIndexAsync(string? indexText)
    {
        if (!int.TryParse(indexText, out var oneBased)) return;
        var index = oneBased - 1;
        if (index < 0 || index >= RepoNodes.Count) return;
        await ActivateNodeAsync(RepoNodes[index]);
    }

    [RelayCommand]
    private void ToggleRepoExpanded(RepoNodeViewModel? node)
    {
        if (node is null) return;
        node.IsExpanded = !node.IsExpanded;
        if (node.IsExpanded)
            _ = RefreshRepoChildrenAsync(node);
    }

    // ── Worktrees ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds a repository's worktree children from <c>git worktree list</c>. The main
    /// worktree is dropped — it is already the root node — so only linked worktrees
    /// become children.
    /// </summary>
    private async Task RefreshRepoChildrenAsync(RepoNodeViewModel? node)
    {
        if (node is null || !Directory.Exists(node.Path)) return;

        node.IsLoadingChildren = true;
        try
        {
            var worktrees = await _git.GetWorktreesAsync(node.Path);
            var branches = await _git.GetBranchesAsync(node.Path);

            var main = worktrees.FirstOrDefault(w => w.IsMain);
            var currentBranch = main?.Branch;
            if (main is not null)
                node.Branch = currentBranch ?? "(detached)";

            // Preserve the active highlight across the rebuild — the collections are
            // replaced wholesale, so IsActive would otherwise be lost on every refresh.
            var activePath = ActiveNode?.Path;

            node.Worktrees.Clear();
            foreach (var wt in worktrees.Where(w => w.IsLinked))
            {
                node.Worktrees.Add(new WorktreeNodeViewModel
                {
                    Path = wt.Path,
                    Branch = wt.Branch ?? "(detached)",
                    RepoPath = node.Path,
                    IsLocked = wt.IsLocked,
                    IsPrunable = wt.IsPrunable,
                    IsActive = string.Equals(wt.Path, activePath, StringComparison.OrdinalIgnoreCase),
                });
            }

            // Branch → the worktree holding it, so a branch checked out elsewhere links
            // there rather than offering a checkout git would refuse.
            var worktreeByBranch = worktrees
                .Where(w => w.IsLinked && w.Branch is not null)
                .GroupBy(w => w.Branch!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Path, StringComparer.Ordinal);

            node.Branches.Clear();
            foreach (var name in branches)
            {
                worktreeByBranch.TryGetValue(name, out var worktreePath);
                node.Branches.Add(new BranchNodeViewModel
                {
                    Branch = name,
                    RepoPath = node.Path,
                    IsCurrent = string.Equals(name, currentBranch, StringComparison.Ordinal),
                    WorktreePath = worktreePath,
                });
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not read repository: {ex.Message}";
        }
        finally
        {
            node.IsLoadingChildren = false;
        }
    }

    /// <summary>Re-reads a repository's worktrees without changing its expansion.</summary>
    [RelayCommand]
    private Task RefreshRepoChildrenCommandAsync(RepoNodeViewModel? node) =>
        RefreshRepoChildrenAsync(node ?? OwningRepoNode());

    /// <summary>The root node owning the active selection, whichever kind it is.</summary>
    private RepoNodeViewModel? OwningRepoNode() => ActiveNode switch
    {
        RepoNodeViewModel repo => repo,
        WorktreeNodeViewModel wt => RepoNodes.FirstOrDefault(r =>
            string.Equals(r.Path, wt.RepoPath, StringComparison.OrdinalIgnoreCase)),
        _ => RepoNodes.FirstOrDefault(),
    };

    // ── Create worktree ───────────────────────────────────────────────────────

    [ObservableProperty] private bool _isCreateWorktreeVisible;
    [ObservableProperty] private string _newWorktreeBranch = string.Empty;
    [ObservableProperty] private string _newWorktreePath = string.Empty;
    [ObservableProperty] private bool _newWorktreeCreatesBranch;
    [ObservableProperty] private string _newWorktreeStartPoint = string.Empty;
    [ObservableProperty] private string _createWorktreeError = string.Empty;

    private RepoNodeViewModel? _worktreeTargetRepo;

    /// <summary>Branches with no worktree yet — the only ones a new worktree can take.</summary>
    public ObservableCollection<string> AvailableWorktreeBranches { get; } = new();

    public bool HasCreateWorktreeError => !string.IsNullOrEmpty(CreateWorktreeError);

    partial void OnCreateWorktreeErrorChanged(string value) =>
        OnPropertyChanged(nameof(HasCreateWorktreeError));

    /// <summary>
    /// Recomputes the suggested directory whenever the branch changes, so the path field
    /// tracks the branch instead of going stale after the first keystroke.
    /// </summary>
    partial void OnNewWorktreeBranchChanged(string value)
    {
        if (_worktreeTargetRepo is null) return;
        NewWorktreePath = SuggestWorktreePath(_worktreeTargetRepo.Path, value);
    }

    [RelayCommand]
    private async Task StartCreateWorktreeAsync(RepoNodeViewModel? node)
    {
        node ??= OwningRepoNode();
        if (node is null) return;

        _worktreeTargetRepo = node;
        CreateWorktreeError = string.Empty;
        NewWorktreeCreatesBranch = false;
        NewWorktreeStartPoint = string.Empty;

        AvailableWorktreeBranches.Clear();
        try
        {
            var branches = await _git.GetBranchesAsync(node.Path);
            var worktrees = await _git.GetWorktreesAsync(node.Path);

            // Git refuses to check a branch out twice, so offering a taken branch would
            // only produce an error on submit. Filter them out up front.
            var taken = worktrees
                .Where(w => w.Branch is not null)
                .Select(w => w.Branch!)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var b in branches.Where(b => !taken.Contains(b)))
                AvailableWorktreeBranches.Add(b);
        }
        catch (Exception ex)
        {
            CreateWorktreeError = ex.Message;
        }

        NewWorktreeBranch = AvailableWorktreeBranches.FirstOrDefault() ?? string.Empty;
        NewWorktreePath = SuggestWorktreePath(node.Path, NewWorktreeBranch);
        IsCreateWorktreeVisible = true;
    }

    [RelayCommand]
    private void CancelCreateWorktree()
    {
        IsCreateWorktreeVisible = false;
        CreateWorktreeError = string.Empty;
        _worktreeTargetRepo = null;
    }

    [RelayCommand]
    private async Task ConfirmCreateWorktreeAsync()
    {
        var repo = _worktreeTargetRepo;
        if (repo is null) return;

        var branch = NewWorktreeBranch?.Trim() ?? string.Empty;
        var path = NewWorktreePath?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(branch))
        {
            CreateWorktreeError = "Choose a branch for the worktree.";
            return;
        }
        if (string.IsNullOrEmpty(path))
        {
            CreateWorktreeError = "Choose a directory for the worktree.";
            return;
        }

        CreateWorktreeError = string.Empty;
        StatusMessage = $"Creating worktree for {branch}…";

        try
        {
            var startPoint = NewWorktreeCreatesBranch && !string.IsNullOrWhiteSpace(NewWorktreeStartPoint)
                ? NewWorktreeStartPoint.Trim()
                : null;

            await _git.AddWorktreeAsync(
                repo.Path, path, branch,
                createBranch: NewWorktreeCreatesBranch,
                startPoint: startPoint);

            IsCreateWorktreeVisible = false;
            _worktreeTargetRepo = null;

            await RefreshRepoChildrenAsync(repo);
            repo.IsExpanded = true;
            StatusMessage = $"Created worktree for {branch}";

            // Opening it immediately is almost always what the user wants next —
            // creating a worktree is a prelude to working in it.
            await OpenRepositoryAsync(path);
        }
        catch (Exception ex)
        {
            CreateWorktreeError = ex.Message;
            StatusMessage = $"Worktree creation failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Default location: a <c>&lt;repo&gt;-worktrees</c> folder beside the repository,
    /// one directory per branch. Keeping them outside the repository means they never
    /// show up as untracked files inside it.
    /// </summary>
    private static string SuggestWorktreePath(string repoPath, string branch)
    {
        if (string.IsNullOrWhiteSpace(repoPath)) return string.Empty;

        var trimmed = Path.TrimEndingDirectorySeparator(repoPath);
        var repoName = Path.GetFileName(trimmed);
        var parent = Path.GetDirectoryName(trimmed);
        if (string.IsNullOrEmpty(parent)) return string.Empty;

        var folder = SanitiseForFolderName(branch);
        if (string.IsNullOrEmpty(folder)) folder = "worktree";

        return Path.Combine(parent, $"{repoName}-worktrees", folder);
    }

    /// <summary>
    /// Branch names legally contain '/' and other characters a directory name cannot,
    /// so "feature/tabs" becomes "feature-tabs".
    /// </summary>
    private static string SanitiseForFolderName(string branch)
    {
        if (string.IsNullOrWhiteSpace(branch)) return string.Empty;

        var invalid = Path.GetInvalidFileNameChars();
        var chars = branch
            .Select(c => c is '/' or '\\' ? '-' : invalid.Contains(c) ? '-' : c)
            .ToArray();

        return new string(chars).Trim('-', ' ', '.');
    }

    // ── Remove worktree ───────────────────────────────────────────────────────

    [RelayCommand]
    private Task RemoveWorktreeAsync(WorktreeNodeViewModel? node) =>
        RemoveWorktreeCoreAsync(node, force: false);

    /// <summary>
    /// Second chance after git refuses because the worktree has local changes. Kept as a
    /// separate command so a destructive removal is never the first click.
    /// </summary>
    [RelayCommand]
    private Task ForceRemoveWorktreeAsync(WorktreeNodeViewModel? node) =>
        RemoveWorktreeCoreAsync(node, force: true);

    private async Task RemoveWorktreeCoreAsync(WorktreeNodeViewModel? node, bool force)
    {
        if (node is null) return;

        var owner = RepoNodes.FirstOrDefault(r =>
            string.Equals(r.Path, node.RepoPath, StringComparison.OrdinalIgnoreCase));
        if (owner is null) return;

        var wasActive = node.IsActive;
        StatusMessage = $"Removing worktree {node.DisplayName}…";

        try
        {
            // Removal is keyed on the branch, matching how the worktree was created.
            await _git.RemoveWorktreeForBranchAsync(owner.Path, node.Branch, force);

            await RefreshRepoChildrenAsync(owner);
            StatusMessage = $"Removed worktree for {node.Branch}";

            // The active checkout just went away — fall back to the owning repository
            // rather than leaving the UI pointed at a deleted directory.
            if (wasActive)
            {
                SetActiveNodeForPath(owner.Path);
                PersistOpenRepos();
                await LoadRepoAsync(owner.Path);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = force
                ? $"Could not remove worktree: {ex.Message}"
                : $"Could not remove worktree: {ex.Message} — use Force remove to discard its changes.";
        }
    }

    [RelayCommand]
    private async Task PruneWorktreesAsync()
    {
        var owner = OwningRepoNode();
        if (owner is null) return;

        try
        {
            await _git.PruneWorktreesAsync(owner.Path);
            await RefreshRepoChildrenAsync(owner);
            StatusMessage = "Pruned stale worktree entries";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Prune failed: {ex.Message}";
        }
    }

    // ── Quick switcher (Ctrl+P) ───────────────────────────────────────────────

    [ObservableProperty] private bool _isQuickSwitchVisible;
    [ObservableProperty] private string _quickSwitchQuery = string.Empty;
    [ObservableProperty] private QuickSwitchEntryViewModel? _selectedQuickSwitchEntry;

    /// <summary>Filtered results shown in the switcher.</summary>
    public ObservableCollection<QuickSwitchEntryViewModel> QuickSwitchResults { get; } = new();

    partial void OnQuickSwitchQueryChanged(string value) => RefreshQuickSwitchResults();

    [RelayCommand]
    private void ToggleQuickSwitch()
    {
        IsQuickSwitchVisible = !IsQuickSwitchVisible;
        if (IsQuickSwitchVisible)
        {
            QuickSwitchQuery = string.Empty;
            RefreshQuickSwitchResults();
        }
    }

    [RelayCommand]
    private void CloseQuickSwitch() => IsQuickSwitchVisible = false;

    /// <summary>
    /// Rebuilds the switcher list: everything already in the tree first — repositories
    /// and their worktrees, since switching between open things is the common case —
    /// then recents, deduplicated.
    /// </summary>
    private void RefreshQuickSwitchResults()
    {
        QuickSwitchResults.Clear();

        var query = QuickSwitchQuery?.Trim() ?? string.Empty;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in FlattenedNodes())
        {
            if (!seen.Add(node.Path)) continue;
            if (!Matches(node.Path, query) && !Matches(node.DisplayName, query)) continue;

            QuickSwitchResults.Add(new QuickSwitchEntryViewModel
            {
                Path = node.Path,
                IsOpen = true,
                IsActive = node.IsActive,
                IsWorktree = node is WorktreeNodeViewModel,
                Branch = node.Branch,
            });
        }

        foreach (var recent in RecentRepositories)
        {
            if (!seen.Add(recent)) continue;
            if (!Matches(recent, query)) continue;
            QuickSwitchResults.Add(new QuickSwitchEntryViewModel { Path = recent, IsOpen = false });
        }

        SelectedQuickSwitchEntry = QuickSwitchResults.FirstOrDefault();
    }

    /// <summary>
    /// Subsequence match, so "gg" finds "grumpygit" and "src/gg" narrows by folder —
    /// the behaviour people expect from a Ctrl+P palette.
    /// </summary>
    private static bool Matches(string haystackRaw, string query)
    {
        if (string.IsNullOrEmpty(query)) return true;

        var haystack = haystackRaw.ToLowerInvariant();
        var needle = query.ToLowerInvariant();

        var h = 0;
        foreach (var c in needle)
        {
            if (c == ' ') continue;
            h = haystack.IndexOf(c, h);
            if (h < 0) return false;
            h++;
        }
        return true;
    }

    [RelayCommand]
    private async Task ActivateQuickSwitchEntryAsync(QuickSwitchEntryViewModel? entry)
    {
        entry ??= SelectedQuickSwitchEntry;
        if (entry is null) return;

        IsQuickSwitchVisible = false;
        await OpenRepositoryAsync(entry.Path);
    }

    /// <summary>Moves the switcher selection without leaving the text box.</summary>
    [RelayCommand]
    private void MoveQuickSwitchSelection(string? direction)
    {
        if (QuickSwitchResults.Count == 0) return;

        var index = SelectedQuickSwitchEntry is null
            ? -1
            : QuickSwitchResults.IndexOf(SelectedQuickSwitchEntry);

        index += string.Equals(direction, "up", StringComparison.OrdinalIgnoreCase) ? -1 : 1;

        if (index < 0) index = QuickSwitchResults.Count - 1;
        if (index >= QuickSwitchResults.Count) index = 0;

        SelectedQuickSwitchEntry = QuickSwitchResults[index];
    }

    // ── Recents and persistence ───────────────────────────────────────────────

    [RelayCommand]
    private async Task OpenRecentRepoAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        await OpenRepositoryAsync(path);
    }

    private void LoadRecentRepos()
    {
        var settings = AppSettings.Load();
        RecentRepositories.Clear();
        foreach (var r in settings.RecentRepositories)
            RecentRepositories.Add(r);
    }

    /// <summary>
    /// Persists repository roots only. Worktrees are rediscovered from git on load, so
    /// storing them would just let the file drift out of date with the repository.
    /// </summary>
    private void PersistOpenRepos() =>
        AppSettings.Load().SaveOpenRepos(
            RepoNodes.Select(n => n.Path),
            ActiveNode?.Path ?? string.Empty);

    /// <summary>
    /// Restores the tree from the previous run. Repositories that have since been moved
    /// or deleted are skipped silently — a stale entry should not block startup.
    /// </summary>
    public async Task RestoreOpenReposAsync()
    {
        var settings = AppSettings.Load();
        var paths = settings.OpenRepositories
            .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
            .ToList();

        if (paths.Count == 0) return;

        foreach (var path in paths)
            EnsureRepoNode(path);

        // Worktree discovery is one git call per repository. Run it for all of them
        // before loading, so the tree is complete the first time it paints.
        foreach (var node in RepoNodes.ToList())
            await RefreshRepoChildrenAsync(node);

        var activePath = !string.IsNullOrWhiteSpace(settings.ActiveRepository)
                         && Directory.Exists(settings.ActiveRepository)
            ? settings.ActiveRepository
            : RepoNodes.FirstOrDefault()?.Path;

        if (string.IsNullOrEmpty(activePath)) return;

        SetActiveNodeForPath(activePath);
        await LoadRepoAsync(activePath);
    }
}
