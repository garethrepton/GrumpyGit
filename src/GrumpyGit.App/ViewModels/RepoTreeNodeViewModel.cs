using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// Shared surface for every node kind in the repository tree, so the view can bind
/// selection, expansion and children without caring which kind it has.
/// </summary>
public abstract partial class RepoTreeNodeViewModel : ObservableObject
{
    /// <summary>
    /// Absolute path this node opens. Empty on nodes that are not checkouts — group
    /// headers, and branches that have no worktree of their own.
    /// </summary>
    [ObservableProperty] private string _path = string.Empty;

    /// <summary>True when this node is the checkout currently loaded.</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>Branch at <see cref="Path"/>, blank until known.</summary>
    [ObservableProperty] private string _branch = string.Empty;

    /// <summary>
    /// Bound two-way by the TreeViewItem style. Lives on the base so that one style
    /// applies to every row without logging a binding failure on the leaves.
    /// </summary>
    [ObservableProperty] private bool _isExpanded;

    /// <summary>Uncommitted file count at this checkout, refreshed when it is loaded.</summary>
    [ObservableProperty] private int _pendingChanges;

    /// <summary>Empty on leaves; the TreeView binds it uniformly for every node kind.</summary>
    public ObservableCollection<RepoTreeNodeViewModel> Children { get; } = new();

    public bool HasPendingChanges => PendingChanges > 0;

    partial void OnPendingChangesChanged(int value) => OnPropertyChanged(nameof(HasPendingChanges));

    public abstract string DisplayName { get; }

    /// <summary>The directory has gone missing since it was registered.</summary>
    public bool IsMissing => !string.IsNullOrEmpty(Path) && !System.IO.Directory.Exists(Path);

    protected static string LeafName(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var trimmed = path.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar);
        var name = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }
}

/// <summary>
/// A repository root — always the <em>main</em> working directory. Opening a linked
/// worktree does not create a second root; it activates the worktree child under the
/// root it belongs to.
/// </summary>
public partial class RepoNodeViewModel : RepoTreeNodeViewModel
{
    /// <summary>A children refresh is in flight for this repository.</summary>
    [ObservableProperty] private bool _isLoadingChildren;

    /// <summary>
    /// The two groups are created once and kept, rather than rebuilt on each refresh,
    /// so expansion state survives a reload. Only their contents are replaced.
    /// </summary>
    public RepoGroupNodeViewModel BranchesGroup { get; } = new("Branches");

    public RepoGroupNodeViewModel WorktreesGroup { get; } = new("Worktrees");

    public RepoNodeViewModel()
    {
        Children.Add(WorktreesGroup);
        Children.Add(BranchesGroup);
    }

    public override string DisplayName => LeafName(Path);

    public ObservableCollection<RepoTreeNodeViewModel> Branches => BranchesGroup.Children;
    public ObservableCollection<RepoTreeNodeViewModel> Worktrees => WorktreesGroup.Children;
}

/// <summary>
/// A "Branches" / "Worktrees" heading. Structure rather than a target: selecting one
/// expands it instead of loading anything.
/// </summary>
public partial class RepoGroupNodeViewModel : RepoTreeNodeViewModel
{
    public RepoGroupNodeViewModel(string title)
    {
        Title = title;
        IsExpanded = true;

        // Contents are replaced wholesale on every refresh, so the counts cannot be
        // raised from a single call site.
        Children.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Count));
            OnPropertyChanged(nameof(IsEmpty));
        };
    }

    public string Title { get; }

    public override string DisplayName => Title;

    public int Count => Children.Count;

    public bool IsEmpty => Children.Count == 0;
}

/// <summary>
/// A local branch. Selecting one checks it out — unless a worktree already holds it,
/// in which case selecting it jumps to that worktree, because git will not check the
/// same branch out twice and the worktree is where that branch actually lives.
/// </summary>
public partial class BranchNodeViewModel : RepoTreeNodeViewModel
{
    /// <summary>Repository the branch belongs to — where checkout runs.</summary>
    public string RepoPath { get; init; } = string.Empty;

    /// <summary>The branch checked out in the repository's main working directory.</summary>
    [ObservableProperty] private bool _isCurrent;

    /// <summary>
    /// Path of the worktree holding this branch, when one does. Doubles as the target
    /// for selection, which is why it is mirrored into <see cref="RepoTreeNodeViewModel.Path"/>.
    /// </summary>
    [ObservableProperty] private string? _worktreePath;

    public bool HasWorktree => !string.IsNullOrEmpty(WorktreePath);

    partial void OnWorktreePathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasWorktree));
        Path = value ?? string.Empty;
    }

    public override string DisplayName => Branch;
}

/// <summary>
/// A linked worktree. Its branch is fixed at creation — <c>GitService</c> refuses to
/// switch branches inside one — so the branch is shown as an identity, not a control.
/// </summary>
public partial class WorktreeNodeViewModel : RepoTreeNodeViewModel
{
    /// <summary>Locked via <c>git worktree lock</c>; removal requires --force.</summary>
    [ObservableProperty] private bool _isLocked;

    /// <summary>Git reports the entry as prunable — usually a deleted directory.</summary>
    [ObservableProperty] private bool _isPrunable;

    /// <summary>Owning repository, so remove/open commands know where to run git.</summary>
    public string RepoPath { get; init; } = string.Empty;

    /// <summary>
    /// Worktree directories are named after their branch, but the folder can be renamed
    /// afterwards. The branch is what actually identifies it.
    /// </summary>
    public override string DisplayName =>
        string.IsNullOrEmpty(Branch) ? LeafName(Path) : Branch;

    public string FolderName => LeafName(Path);
}
