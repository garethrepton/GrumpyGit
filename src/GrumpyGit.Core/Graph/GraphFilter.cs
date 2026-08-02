using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Graph;

/// <summary>
/// Which commits the graph should show.
/// </summary>
public sealed record GraphFilterOptions
{
    public static readonly GraphFilterOptions Unfiltered = new();

    /// <summary>
    /// When set, show only that branch's own line of development — see
    /// <see cref="GraphFilter.FirstParentChain"/>. Null disables branch mode.
    /// </summary>
    public string? BranchMode { get; init; }

    /// <summary>Branch labels the user has toggled off in the key.</summary>
    public IReadOnlySet<string> HiddenBranches { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    public bool IsActive =>
        !string.IsNullOrEmpty(BranchMode) || HiddenBranches.Count > 0;
}

/// <summary>
/// Narrows a commit list before it is laid out.
///
/// Filtering happens on commits, not on the rendered graph: re-running
/// <see cref="GraphLayoutEngine"/> over the surviving commits produces clean lane
/// assignments, whereas hiding rows after layout would leave lines running to commits
/// that are no longer there.
/// </summary>
public static class GraphFilter
{
    /// <summary>
    /// The commits on a branch's own line: the branch tip, then its first parent, then
    /// that commit's first parent, and so on.
    ///
    /// This is what "commits and merges into this branch" means. A merge commit's first
    /// parent is the branch being merged <em>into</em>, so following first parents stays
    /// on the target branch and never descends into merged-in work. The merge commits
    /// themselves are kept — they are the record that something landed — but the dozens
    /// of commits that arrived with each one are not.
    /// </summary>
    public static IReadOnlySet<string> FirstParentChain(
        IReadOnlyList<CommitNode> commits, string branch)
    {
        var chain = new HashSet<string>(StringComparer.Ordinal);
        if (commits.Count == 0 || string.IsNullOrWhiteSpace(branch))
            return chain;

        var byHash = new Dictionary<string, CommitNode>(StringComparer.Ordinal);
        foreach (var c in commits)
            byHash[c.Hash] = c;

        var tip = FindBranchTip(commits, branch);
        if (tip is null)
            return chain;

        var current = tip;
        while (current is not null && chain.Add(current.Hash))
        {
            if (current.ParentHashes.Length == 0)
                break;

            byHash.TryGetValue(current.ParentHashes[0], out current);
        }

        return chain;
    }

    /// <summary>
    /// The commit a branch name points at. Matches the decorations git reports, so both
    /// "feature/x" and "origin/feature/x" find the branch a user would expect.
    /// </summary>
    private static CommitNode? FindBranchTip(IReadOnlyList<CommitNode> commits, string branch)
    {
        foreach (var commit in commits)
        {
            foreach (var raw in commit.RefNames)
            {
                var refName = raw.Trim();
                if (refName.Length == 0) continue;

                if (refName.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
                    continue;

                // "HEAD -> main" decorates the checked-out branch.
                var arrow = refName.IndexOf("->", StringComparison.Ordinal);
                if (arrow >= 0)
                    refName = refName[(arrow + 2)..].Trim();

                if (string.Equals(refName, branch, StringComparison.Ordinal))
                    return commit;

                // origin/feature/x should be found by "feature/x", and vice versa.
                if (StripRemote(refName) == StripRemote(branch))
                    return commit;
            }
        }

        return null;
    }

    private static string StripRemote(string refName)
    {
        foreach (var prefix in new[] { "refs/remotes/", "origin/", "upstream/" })
        {
            if (refName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return refName[prefix.Length..];
        }
        return refName;
    }

    /// <summary>
    /// Applies branch mode and the hidden-branch set.
    ///
    /// <paramref name="labelByHash"/> comes from a first layout pass — a commit's branch
    /// is inferred, not recorded by git, so the labels the key shows are the labels this
    /// has to filter on for the two to agree.
    /// </summary>
    public static IReadOnlyList<CommitNode> Apply(
        IReadOnlyList<CommitNode> commits,
        IReadOnlyDictionary<string, string?> labelByHash,
        GraphFilterOptions options)
    {
        if (commits.Count == 0 || !options.IsActive)
            return commits;

        IEnumerable<CommitNode> result = commits;

        if (!string.IsNullOrEmpty(options.BranchMode))
        {
            var chain = FirstParentChain(commits, options.BranchMode);

            // An unresolvable branch would otherwise blank the graph entirely, which
            // reads as a bug rather than as "that branch is not in this history".
            if (chain.Count > 0)
                result = result.Where(c => chain.Contains(c.Hash));
        }

        if (options.HiddenBranches.Count > 0)
        {
            result = result.Where(c =>
            {
                labelByHash.TryGetValue(c.Hash, out var label);

                // Commits whose branch could not be inferred are never hidden: there is
                // no key entry to turn them back on with, so they would be unreachable.
                return label is null || !options.HiddenBranches.Contains(label);
            });
        }

        return result.ToList();
    }
}
