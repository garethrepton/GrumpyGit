namespace GrumpyGit.Core.Models;

/// <summary>Result of merging the source branch into the target, without doing it.</summary>
public enum MergeOutcome
{
    /// <summary>The merge would apply with no conflicts.</summary>
    Clean,

    /// <summary>At least one path would conflict.</summary>
    Conflicts,

    /// <summary>Git could not answer — too old for <c>merge-tree --write-tree</c>, or it failed.</summary>
    Unknown,
}

/// <summary>
/// What a merge of source into target would do, computed without touching the working
/// tree, the index, or the current checkout.
/// </summary>
public sealed record MergePreview(MergeOutcome Outcome, IReadOnlyList<string> ConflictingPaths)
{
    public static readonly MergePreview Unknown = new(MergeOutcome.Unknown, []);

    public bool HasConflicts => Outcome == MergeOutcome.Conflicts;
}

/// <summary>
/// A pull request that does not exist yet: everything a reviewer would see on the
/// hosting provider's page, computed locally from two branches.
///
/// The comparison is against the <em>merge base</em>, not the target's tip, for the same
/// reason every review tool does it — commits landing on the target after this branch
/// diverged are somebody else's work and do not belong in this review.
/// </summary>
public sealed record PullRequestPreview
{
    public required string SourceBranch { get; init; }

    public required string TargetBranch { get; init; }

    /// <summary>Common ancestor of the two branches — what the diff is taken from.</summary>
    public required string MergeBaseHash { get; init; }

    /// <summary>Tip of the source branch — what the diff is taken to.</summary>
    public required string HeadHash { get; init; }

    /// <summary>Commits the merge would introduce, newest first.</summary>
    public required IReadOnlyList<CommitNode> Commits { get; init; }

    /// <summary>Net file changes across the whole range.</summary>
    public required IReadOnlyList<FileChange> Files { get; init; }

    /// <summary>Per-file line counts. Binary files are absent rather than zero.</summary>
    public required IReadOnlyDictionary<string, (int Added, int Removed)> Stats { get; init; }

    public required MergePreview Merge { get; init; }

    /// <summary>True when the source branch has nothing the target does not already have.</summary>
    public bool IsEmpty => Commits.Count == 0 && Files.Count == 0;

    public int LinesAdded => Stats.Values.Sum(s => s.Added);

    public int LinesRemoved => Stats.Values.Sum(s => s.Removed);
}

/// <summary>
/// One file as the reviewer left it — carried back into
/// <see cref="Git.PullRequestSummaryBuilder"/> so the written summary reflects the review,
/// not just the diff.
/// </summary>
public sealed record ReviewedFile(
    string Path,
    int LinesAdded,
    int LinesRemoved,
    bool IsReviewed,
    string Note);
