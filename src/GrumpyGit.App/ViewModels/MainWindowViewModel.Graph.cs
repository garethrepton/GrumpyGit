using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrumpyGit.Core.Graph;
using GrumpyGit.Core.Models;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// Partial class — the standalone commit graph panel, its key, and the filters that
/// drive both it and the commit list.
///
/// Filtering is applied to commits and the graph is then laid out again from scratch,
/// rather than hiding rows in a finished layout. Lane assignment depends on which
/// commits are present, so a post-hoc hide would leave lines running to commits that
/// are no longer on screen.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>Unfiltered history, kept so filters can be re-applied without touching git.</summary>
    private IReadOnlyList<CommitNode> _unfilteredHistory = Array.Empty<CommitNode>();

    /// <summary>
    /// Inferred branch per commit, from a layout pass over the unfiltered history. Git
    /// does not record which branch a commit was made on, so the key and the filter have
    /// to agree on the same inference — which means computing it once, here.
    /// </summary>
    private IReadOnlyDictionary<string, string?> _labelByHash =
        new Dictionary<string, string?>(StringComparer.Ordinal);

    private readonly HashSet<string> _hiddenBranches = new(StringComparer.Ordinal);

    /// <summary>Nodes for the graph panel, after filtering.</summary>
    [ObservableProperty] private IReadOnlyList<GraphNode>? _graphNodes;

    [ObservableProperty] private int _graphTotalLanes = 1;

    /// <summary>Mirrors the commit list's scroll position so the two stay aligned.</summary>
    [ObservableProperty] private double _graphScrollOffset;

    [ObservableProperty] private IReadOnlyDictionary<string, int>? _branchColors;

    /// <summary>The key: every branch the graph can attribute a commit to.</summary>
    public ObservableCollection<BranchLegendEntryViewModel> GraphLegend { get; } = new();

    /// <summary>
    /// The commit list starts with the working-tree row, which has no graph node. The
    /// panel needs to know so every node does not draw one row too high.
    /// </summary>
    public int GraphRowOffset => 1;

    // ── Branch mode ───────────────────────────────────────────────────────────

    /// <summary>
    /// When set, the graph shows only that branch's own line of development: commits made
    /// on it plus the merge commits where other work landed, and nothing that arrived
    /// inside those merges. See <see cref="GraphFilter.FirstParentChain"/>.
    /// </summary>
    [ObservableProperty] private string? _branchModeTarget;

    public bool IsBranchMode => !string.IsNullOrEmpty(BranchModeTarget);

    [ObservableProperty] private bool _isGraphKeyVisible = true;

    /// <summary>
    /// Set while <see cref="InitialiseGraph"/> is establishing state that it will render
    /// once at the end. Without it, clearing a stale branch-mode target part-way through
    /// would lay the graph out against half-built state and then immediately do it again.
    /// </summary>
    private bool _suppressGraphRebuild;

    partial void OnBranchModeTargetChanged(string? value)
    {
        OnPropertyChanged(nameof(IsBranchMode));
        OnPropertyChanged(nameof(BranchModeSummary));

        foreach (var entry in GraphLegend)
            entry.IsBranchModeTarget = string.Equals(entry.Name, value, StringComparison.Ordinal);

        if (!_suppressGraphRebuild)
            RebuildGraphView();
    }

    public string BranchModeSummary => IsBranchMode
        ? $"Only {BranchModeTarget} — merges shown, merged commits hidden"
        : "All branches";

    [RelayCommand]
    private void SetBranchMode(string? branch)
    {
        // Clicking the active target again clears it, so the same row toggles both ways.
        BranchModeTarget = string.Equals(BranchModeTarget, branch, StringComparison.Ordinal)
            ? null
            : branch;
    }

    [RelayCommand]
    private void ClearBranchMode() => BranchModeTarget = null;

    [RelayCommand]
    private void ToggleGraphKey() => IsGraphKeyVisible = !IsGraphKeyVisible;

    // ── Show / hide branches ──────────────────────────────────────────────────

    /// <summary>
    /// The key's checkboxes bind straight to <see cref="BranchLegendEntryViewModel.IsVisible"/>,
    /// so the hidden set is maintained by watching the entries rather than by routing every
    /// tick through a command — which would have to fight the checkbox over who owns the state.
    /// </summary>
    private void OnLegendEntryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not BranchLegendEntryViewModel entry) return;
        if (e.PropertyName != nameof(BranchLegendEntryViewModel.IsVisible)) return;

        if (entry.IsVisible) _hiddenBranches.Remove(entry.Name);
        else _hiddenBranches.Add(entry.Name);

        if (!_suppressGraphRebuild)
            RebuildGraphView();
    }

    [RelayCommand]
    private void ShowAllBranches()
    {
        _suppressGraphRebuild = true;
        try
        {
            _hiddenBranches.Clear();
            foreach (var entry in GraphLegend)
                entry.IsVisible = true;
        }
        finally
        {
            _suppressGraphRebuild = false;
        }

        RebuildGraphView();
    }

    /// <summary>Isolates one branch in the key without engaging branch mode.</summary>
    [RelayCommand]
    private void ShowOnlyBranch(BranchLegendEntryViewModel? entry)
    {
        if (entry is null) return;

        _suppressGraphRebuild = true;
        try
        {
            _hiddenBranches.Clear();
            foreach (var other in GraphLegend)
            {
                other.IsVisible = ReferenceEquals(other, entry);
                if (!other.IsVisible) _hiddenBranches.Add(other.Name);
            }
        }
        finally
        {
            _suppressGraphRebuild = false;
        }

        RebuildGraphView();
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called once per repository load. Runs the label pass over the whole history,
    /// builds the key, then renders the first view through the current filters.
    /// </summary>
    private void InitialiseGraph(IReadOnlyList<CommitNode> commits)
    {
        _suppressGraphRebuild = true;
        try
        {
            BuildGraphState(commits);
        }
        finally
        {
            _suppressGraphRebuild = false;
        }

        RebuildGraphView();
    }

    private void BuildGraphState(IReadOnlyList<CommitNode> commits)
    {
        _unfilteredHistory = commits;

        // Layout pass purely to infer branch labels. Its lane assignments are discarded —
        // the view is laid out again after filtering, over a different set of commits.
        var labelled = GraphLayoutEngine.Compute(commits);

        var labels = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var node in labelled)
            labels[node.Hash] = node.BranchLabel;
        _labelByHash = labels;

        BranchColors = BranchPalette.Assign(labelled.Select(n => n.BranchLabel));

        var counts = labelled
            .Where(n => !string.IsNullOrEmpty(n.BranchLabel))
            .GroupBy(n => n.BranchLabel!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        // A branch hidden before a reload stays hidden only if it still exists.
        _hiddenBranches.RemoveWhere(b => !counts.ContainsKey(b));

        foreach (var old in GraphLegend)
            old.PropertyChanged -= OnLegendEntryChanged;
        GraphLegend.Clear();

        foreach (var (name, count) in counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var entry = new BranchLegendEntryViewModel
            {
                Name = name,
                CommitCount = count,
                ColorSlot = BranchColors.TryGetValue(name, out var slot) ? slot : 0,
                IsVisible = !_hiddenBranches.Contains(name),
                IsBranchModeTarget = string.Equals(name, BranchModeTarget, StringComparison.Ordinal),
            };

            // Subscribed after the initial state is set, so restoring a hidden branch
            // does not read as a fresh user toggle.
            entry.PropertyChanged += OnLegendEntryChanged;
            GraphLegend.Add(entry);
        }

        // A branch-mode target from the previous repository is meaningless here.
        if (IsBranchMode && !counts.ContainsKey(BranchModeTarget!))
            BranchModeTarget = null;
    }

    /// <summary>
    /// Re-applies the filters and repopulates both the graph and the commit list. Runs on
    /// every key toggle, so it does no git I/O — everything comes from <see cref="_unfilteredHistory"/>.
    /// </summary>
    private void RebuildGraphView()
    {
        var options = new GraphFilterOptions
        {
            BranchMode = BranchModeTarget,
            HiddenBranches = _hiddenBranches,
        };

        var filtered = GraphFilter.Apply(_unfilteredHistory, _labelByHash, options);
        var nodes = GraphLayoutEngine.Compute(filtered);

        // Laying out a subset can infer a different branch for the same commit — with the
        // merged-in commits gone, a lane's identity is inherited from somewhere else. Put
        // the unfiltered labels back so the key, the node colours and the row tints all
        // agree; the key is built from these labels and must stay the authority.
        foreach (var node in nodes)
        {
            if (_labelByHash.TryGetValue(node.Hash, out var label))
                node.BranchLabel = label;
        }

        GraphNodes = nodes;
        GraphTotalLanes = CountLanes(nodes);

        // The selected commit may have just been filtered out; remember it so the
        // selection can be restored when it comes back rather than silently resetting.
        var previousHash = SelectedCommit?.Hash;

        // Drop the search backup: it holds rows from the previous filter, and restoring
        // it after a branch toggle would put commits back that are now hidden.
        _allCommits = null;

        Commits.Clear();
        Commits.Add(BuildWorkingTreeRow());

        _allGraphNodes = nodes;
        _totalLanes = GraphTotalLanes;
        _loadedCommitCount = 0;
        LoadNextCommitPage();

        SelectedCommit =
            Commits.FirstOrDefault(c => string.Equals(c.Hash, previousHash, StringComparison.Ordinal))
            ?? Commits.FirstOrDefault(c => c.IsWorkingTree);

        OnPropertyChanged(nameof(FilteredCommitSummary));
    }

    private CommitRowViewModel BuildWorkingTreeRow() => new()
    {
        Hash = CommitRowViewModel.WorkingTreeHash,
        Subject = PendingChangesCount > 0
            ? $"  Working Changes  ({PendingChangesCount} file(s))"
            : "  Working Tree  (clean)",
    };

    private static int CountLanes(IReadOnlyList<GraphNode> nodes)
    {
        var maxLane = 0;
        foreach (var node in nodes)
        {
            if (node.Lane > maxLane) maxLane = node.Lane;
            foreach (var seg in node.Segments)
            {
                if (seg.FromLane > maxLane) maxLane = seg.FromLane;
                if (seg.ToLane > maxLane) maxLane = seg.ToLane;
            }
        }
        return maxLane + 1;
    }

    /// <summary>Status text so a filtered view never looks like a short history.</summary>
    public string FilteredCommitSummary
    {
        get
        {
            var shown = GraphNodes?.Count ?? 0;
            var total = _unfilteredHistory.Count;
            return shown == total
                ? $"{total} commit(s)"
                : $"{shown} of {total} commit(s)";
        }
    }
}
