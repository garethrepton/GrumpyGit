using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Git;

/// <summary>
/// Assembles a <see cref="PullRequestPreview"/> from the git primitives.
///
/// Kept out of <see cref="GitService"/> because it launches nothing itself — it is
/// orchestration over the seam, so it can be exercised against a fake backend.
/// </summary>
public static class PullRequestPreviewBuilder
{
    /// <summary>
    /// Builds the preview for merging <paramref name="sourceBranch"/> into
    /// <paramref name="targetBranch"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The two branches are the same, or share no history — neither is a pull request.
    /// </exception>
    public static async Task<PullRequestPreview> BuildAsync(
        IGitService git,
        string repoPath,
        string sourceBranch,
        string targetBranch,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(git);

        if (string.Equals(sourceBranch, targetBranch, StringComparison.Ordinal))
            throw new ArgumentException("A branch cannot be reviewed against itself.", nameof(targetBranch));

        var headHash = await git.GetBranchHeadAsync(repoPath, sourceBranch, ct);
        var mergeBase = await git.GetMergeBaseAsync(repoPath, targetBranch, sourceBranch, ct);

        if (string.IsNullOrEmpty(mergeBase))
            throw new ArgumentException(
                $"'{sourceBranch}' and '{targetBranch}' share no common history.", nameof(targetBranch));

        // The three range reads are independent; the merge check is the slow one, so
        // running them together keeps the panel responsive on a large branch.
        var commitsTask = git.GetCommitsInRangeAsync(repoPath, mergeBase, headHash, ct);
        var filesTask = git.GetCommitRangeFileListAsync(repoPath, mergeBase, headHash, ct);
        var statsTask = git.GetCommitRangeStatsAsync(repoPath, mergeBase, headHash, ct);
        var mergeTask = git.PreviewMergeAsync(repoPath, targetBranch, sourceBranch, ct);

        await Task.WhenAll(commitsTask, filesTask, statsTask, mergeTask);

        return new PullRequestPreview
        {
            SourceBranch = sourceBranch,
            TargetBranch = targetBranch,
            MergeBaseHash = mergeBase,
            HeadHash = headHash,
            Commits = await commitsTask,
            Files = await filesTask,
            Stats = await statsTask,
            Merge = await mergeTask,
        };
    }
}
