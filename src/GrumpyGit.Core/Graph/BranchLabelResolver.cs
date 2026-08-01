using System.Text.RegularExpressions;

namespace GrumpyGit.Core.Graph;

/// <summary>
/// Works out a human-meaningful branch name for a lane in the commit graph.
///
/// Git does not record which branch a commit was made on — a branch is only a moving
/// pointer at a tip. So a lane's identity has to be inferred, and this is done from the
/// strongest available evidence in order:
///
///   1. A branch ref decorating the lane's tip commit (definitive while the branch exists).
///   2. The merge commit's subject, e.g. "Merge branch 'feature/x'" — the only surviving
///      record of a branch that has since been deleted, which is the common case for
///      merged work.
///   3. Inheritance from the child commit that opened the lane.
///
/// When nothing is known the lane is left unlabelled rather than guessing, so the UI can
/// say "unknown" instead of showing a confidently wrong branch name.
/// </summary>
public static class BranchLabelResolver
{
    /// <summary>"Merge branch 'feature/x'" / "Merge branch 'x' into y".</summary>
    private static readonly Regex MergeBranchPattern = new(
        @"^Merge branch '(?<branch>[^']+)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>"Merge remote-tracking branch 'origin/feature/x'".</summary>
    private static readonly Regex MergeRemotePattern = new(
        @"^Merge remote-tracking branch '(?<branch>[^']+)'", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>"Merge pull request #12 from owner/feature-x".</summary>
    private static readonly Regex MergePrPattern = new(
        @"^Merge pull request #\d+ from (?<branch>\S+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Picks the branch name to show for a commit's decorations, or null if none of them
    /// denote a branch.
    ///
    /// Local branches win over remote-tracking ones because that is what the user thinks
    /// they are on. Tags are excluded — a tag marks a point in history, not a line of
    /// development, so labelling a lane "v1.2.0" would be misleading.
    /// </summary>
    public static string? FromRefNames(IReadOnlyList<string>? refNames)
    {
        if (refNames is null || refNames.Count == 0)
            return null;

        string? remoteCandidate = null;

        foreach (var raw in refNames)
        {
            var refName = raw.Trim();
            if (refName.Length == 0)
                continue;

            // Tags are not branches.
            if (refName.StartsWith("tag:", StringComparison.OrdinalIgnoreCase))
                continue;

            // "HEAD -> main" names the checked-out branch; take what it points at.
            var arrow = refName.IndexOf("->", StringComparison.Ordinal);
            if (arrow >= 0)
            {
                var target = refName[(arrow + 2)..].Trim();
                if (target.Length > 0)
                    return target;
                continue;
            }

            // A bare detached HEAD tells us nothing about a branch.
            if (refName.Equals("HEAD", StringComparison.Ordinal))
                continue;

            if (IsRemote(refName))
            {
                // Hold remotes back in case a local branch also decorates this commit.
                remoteCandidate ??= refName;
                continue;
            }

            return refName;
        }

        return remoteCandidate;
    }

    /// <summary>
    /// Recovers the merged branch's name from a merge commit's subject. This is what
    /// keeps merged-and-deleted branches identifiable in the graph.
    /// </summary>
    public static string? FromMergeSubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return null;

        var trimmed = subject.Trim();

        var m = MergeBranchPattern.Match(trimmed);
        if (m.Success) return m.Groups["branch"].Value;

        m = MergeRemotePattern.Match(trimmed);
        if (m.Success) return m.Groups["branch"].Value;

        m = MergePrPattern.Match(trimmed);
        if (m.Success)
        {
            // "owner/feature-x" — the branch is everything after the owner segment.
            var value = m.Groups["branch"].Value;
            var slash = value.IndexOf('/');
            return slash >= 0 && slash < value.Length - 1 ? value[(slash + 1)..] : value;
        }

        return null;
    }

    private static bool IsRemote(string refName) =>
        refName.StartsWith("origin/", StringComparison.OrdinalIgnoreCase)
        || refName.StartsWith("upstream/", StringComparison.OrdinalIgnoreCase)
        || refName.StartsWith("refs/remotes/", StringComparison.OrdinalIgnoreCase);
}
