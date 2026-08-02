using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Git;

public interface IGitService
{
    Task<IReadOnlyList<CommitNode>> GetCommitGraphAsync(string repoPath, CancellationToken ct = default);

    Task<IReadOnlyList<FileChange>> GetFilesChangedInCommitAsync(string repoPath, string commitHash, CancellationToken ct = default);

    Task<string> GetFileDiffAsync(string repoPath, string commitHash, string filePath, CancellationToken ct = default);

    /// <summary>Diff for one file in a commit, with explicit context/whitespace options.</summary>
    Task<string> GetFileDiffAsync(string repoPath, string commitHash, string filePath, DiffOptions options, CancellationToken ct = default);

    Task<IReadOnlyList<FileChange>> GetWorkingTreeStatusAsync(string repoPath, CancellationToken ct = default);

    Task<string> GetUnstagedDiffAsync(string repoPath, string filePath, CancellationToken ct = default);

    /// <summary>Unstaged diff with explicit context/whitespace options.</summary>
    Task<string> GetUnstagedDiffAsync(string repoPath, string filePath, DiffOptions options, CancellationToken ct = default);

    Task<string> GetStagedDiffAsync(string repoPath, string filePath, CancellationToken ct = default);

    /// <summary>Staged diff with explicit context/whitespace options.</summary>
    Task<string> GetStagedDiffAsync(string repoPath, string filePath, DiffOptions options, CancellationToken ct = default);

    Task StageFileAsync(string repoPath, string filePath, CancellationToken ct = default);

    Task UnstageFileAsync(string repoPath, string filePath, CancellationToken ct = default);

    Task<string> CommitAsync(string repoPath, string message, CancellationToken ct = default);

    Task PushAsync(string repoPath, string remote = "origin", string? branch = null, CancellationToken ct = default);

    Task PullAsync(string repoPath, string remote = "origin", string? branch = null, CancellationToken ct = default);

    Task<string> GetCommitRangeDiffAsync(string repoPath, string fromHash, string toHash, CancellationToken ct = default);

    /// <summary>Net file list between two commits, with both hashes validated.</summary>
    Task<IReadOnlyList<FileChange>> GetCommitRangeFileListAsync(string repoPath, string fromHash, string toHash, CancellationToken ct = default);

    /// <summary>Per-file added/removed line counts between two commits.</summary>
    Task<Dictionary<string, (int Added, int Removed)>> GetCommitRangeStatsAsync(string repoPath, string fromHash, string toHash, CancellationToken ct = default);

    /// <summary>Net diff for a single file between two commits.</summary>
    Task<string> GetCommitRangeFileDiffAsync(string repoPath, string fromHash, string toHash, string filePath, CancellationToken ct = default);

    /// <summary>
    /// Returns the current branch name, or "(detached) &lt;short-hash&gt;" if in detached HEAD state.
    /// </summary>
    Task<string> GetCurrentBranchAsync(string repoPath, CancellationToken ct = default);

    /// <summary>Returns all local branch names.</summary>
    Task<IReadOnlyList<string>> GetBranchesAsync(string repoPath, CancellationToken ct = default);

    /// <summary>Creates a new branch and checks it out immediately (git switch -c).</summary>
    Task CreateBranchAsync(string repoPath, string branchName, CancellationToken ct = default);

    /// <summary>Switches to an existing local branch.</summary>
    Task CheckoutBranchAsync(string repoPath, string branchName, CancellationToken ct = default);

    /// <summary>Merges the named branch into the current branch.</summary>
    Task MergeBranchAsync(string repoPath, string branchName, CancellationToken ct = default);

    /// <summary>
    /// Returns the push URL for the named remote, or an empty string if no remote is configured.
    /// </summary>
    Task<string> GetRemoteUrlAsync(string repoPath, string remote = "origin", CancellationToken ct = default);

    // ── Stash ────────────────────────────────────────────────────────────────

    /// <summary>Returns all stash entries as display strings (newest first).</summary>
    Task<IReadOnlyList<string>> GetStashListAsync(string repoPath, CancellationToken ct = default);

    /// <summary>Pushes current changes onto the stash stack.</summary>
    Task StashAsync(string repoPath, string? message = null, CancellationToken ct = default);

    /// <summary>Pops the most recent stash entry back into the working tree.</summary>
    Task StashPopAsync(string repoPath, CancellationToken ct = default);

    // ── Undo / Revert ─────────────────────────────────────────────────────────

    /// <summary>
    /// Soft-resets HEAD by one commit, moving changes back to the staging area.
    /// Equivalent to: git reset --soft HEAD~1
    /// </summary>
    Task UndoLastCommitAsync(string repoPath, CancellationToken ct = default);

    /// <summary>
    /// Creates a new commit that reverses the changes introduced by the given commit.
    /// Equivalent to: git revert --no-edit &lt;commitHash&gt;
    /// For merge commits, uses -m 1 (keeps first parent).
    /// </summary>
    Task RevertCommitAsync(string repoPath, string commitHash, CancellationToken ct = default);

    /// <summary>
    /// Returns the number of parent commits for the given commit hash.
    /// Used to detect merge commits (parentCount &gt; 1) and initial commits (parentCount == 0).
    /// </summary>
    Task<int> GetParentCountAsync(string repoPath, string commitHash, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the working tree and index are clean (no uncommitted changes).
    /// </summary>
    Task<bool> IsWorkingTreeCleanAsync(string repoPath, CancellationToken ct = default);

    // ── Hunk-level staging ──────────────────────────────────────────────────────

    /// <summary>Stage a patch (pipe to git apply --cached).</summary>
    Task StageHunkAsync(string repoPath, string patchContent, CancellationToken ct = default);

    /// <summary>Unstage a patch (pipe to git apply --cached --reverse).</summary>
    Task UnstageHunkAsync(string repoPath, string patchContent, CancellationToken ct = default);

    /// <summary>Mark an untracked file as intent-to-add so partial staging works (git add -N).</summary>
    Task IntentToAddAsync(string repoPath, string filePath, CancellationToken ct = default);

    // ── Discard changes ──────────────────────────────────────────────────────

    /// <summary>Discards unstaged changes to a tracked file (git restore -- file).</summary>
    Task DiscardFileChangesAsync(string repoPath, string filePath, CancellationToken ct = default);

    /// <summary>Removes an untracked file from the working tree (git clean -f -- file).</summary>
    Task RemoveUntrackedFileAsync(string repoPath, string filePath, CancellationToken ct = default);

    // ── Tag management ───────────────────────────────────────────────────────

    /// <summary>Returns all tags with metadata.</summary>
    Task<IReadOnlyList<TagInfo>> GetTagsAsync(string repoPath, CancellationToken ct = default);

    /// <summary>Creates a lightweight or annotated tag.</summary>
    Task CreateTagAsync(string repoPath, string tagName, string? message = null, string? commitHash = null, CancellationToken ct = default);

    /// <summary>Deletes a local tag.</summary>
    Task DeleteTagAsync(string repoPath, string tagName, CancellationToken ct = default);

    /// <summary>Pushes a tag to a remote.</summary>
    Task PushTagAsync(string repoPath, string tagName, string remote = "origin", CancellationToken ct = default);

    /// <summary>
    /// Hashes of commits reachable from a local branch but from no remote-tracking
    /// branch — everything a push would publish.
    /// </summary>
    Task<IReadOnlySet<string>> GetUnpushedCommitsAsync(string repoPath, CancellationToken ct = default);

    // ── Blame ────────────────────────────────────────────────────────────────

    /// <summary>Returns per-line blame information for a file.</summary>
    Task<IReadOnlyList<BlameLine>> GetBlameAsync(string repoPath, string filePath, string? commitHash = null, CancellationToken ct = default);

    // ── File history ─────────────────────────────────────────────────────────

    /// <summary>Returns the commit history for a single file, following renames.</summary>
    Task<IReadOnlyList<CommitNode>> GetFileHistoryAsync(string repoPath, string filePath, int maxCount = 100, CancellationToken ct = default);

    // ── Search ───────────────────────────────────────────────────────────────

    /// <summary>Searches commits by message and/or author across all branches.</summary>
    Task<IReadOnlyList<CommitNode>> SearchCommitsAsync(string repoPath, string? query = null, string? author = null, int maxCount = 200, CancellationToken ct = default);

    // ── Conflict resolution ─────────────────────────────────────────────────────

    /// <summary>Returns the list of conflicted (unmerged) files from git status --porcelain=v2.</summary>
    Task<IReadOnlyList<ConflictedFile>> GetConflictedFilesAsync(string repoPath, CancellationToken ct = default);

    /// <summary>Returns the content of a conflict version (:1: base, :2: ours, :3: theirs).</summary>
    Task<string> GetConflictVersionAsync(string repoPath, string filePath, ConflictSide side, CancellationToken ct = default);

    /// <summary>Resolves a conflict by choosing --ours or --theirs, then stages the file.</summary>
    Task ResolveConflictWithSideAsync(string repoPath, string filePath, ConflictSide side, CancellationToken ct = default);

    /// <summary>Marks a conflicted file as resolved by staging it (git add).</summary>
    Task MarkConflictResolvedAsync(string repoPath, string filePath, CancellationToken ct = default);

    /// <summary>Aborts the current merge (git merge --abort).</summary>
    Task AbortMergeAsync(string repoPath, CancellationToken ct = default);

    // ── Interactive Rebase ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns the list of commits between <paramref name="ontoCommit"/> and HEAD
    /// in chronological order (oldest first), suitable for an interactive rebase todo list.
    /// </summary>
    Task<IReadOnlyList<RebaseEntry>> GetRebaseCommitsAsync(string repoPath, string ontoCommit, CancellationToken ct = default);

    /// <summary>
    /// Executes an interactive rebase onto <paramref name="ontoCommit"/> using the provided
    /// action list as the todo sequence. Uses GIT_SEQUENCE_EDITOR to inject the todo list.
    /// </summary>
    Task ExecuteRebaseAsync(string repoPath, string ontoCommit, IReadOnlyList<RebaseAction> actions, CancellationToken ct = default);

    /// <summary>Continues a paused interactive rebase (git rebase --continue).</summary>
    Task ContinueRebaseAsync(string repoPath, CancellationToken ct = default);

    /// <summary>Aborts a paused interactive rebase (git rebase --abort).</summary>
    Task AbortRebaseAsync(string repoPath, CancellationToken ct = default);

    /// <summary>Skips the current commit in a paused interactive rebase (git rebase --skip).</summary>
    Task SkipRebaseAsync(string repoPath, CancellationToken ct = default);

    /// <summary>
    /// Returns true if a rebase is currently in progress (checks for .git/rebase-merge or .git/rebase-apply).
    /// </summary>
    Task<bool> IsRebaseInProgressAsync(string repoPath, CancellationToken ct = default);

    // ── Worktrees ────────────────────────────────────────────────────────────
    //
    // Worktrees are bound to a branch: created for one, listed by one, removed by one.
    // <see cref="CheckoutBranchAsync"/> and <see cref="CreateBranchAsync"/> refuse to
    // run inside a linked worktree so that binding cannot be broken after the fact.

    /// <summary>All worktrees for the repository. The main working directory is first.</summary>
    Task<IReadOnlyList<GitWorktree>> GetWorktreesAsync(string repoPath, CancellationToken ct = default);

    /// <summary>
    /// True when <paramref name="repoPath"/> is a linked worktree rather than the
    /// repository's main working directory.
    /// </summary>
    Task<bool> IsLinkedWorktreeAsync(string repoPath, CancellationToken ct = default);

    /// <summary>
    /// Creates a worktree holding <paramref name="branchName"/>. With
    /// <paramref name="createBranch"/> the branch is created from
    /// <paramref name="startPoint"/> (HEAD when null); otherwise it must already exist
    /// and not be checked out in another worktree.
    /// </summary>
    Task AddWorktreeAsync(
        string repoPath,
        string worktreePath,
        string branchName,
        bool createBranch = false,
        string? startPoint = null,
        CancellationToken ct = default);

    /// <summary>Removes the worktree at a path. Refuses to remove the main worktree.</summary>
    Task RemoveWorktreeAsync(string repoPath, string worktreePath, bool force = false, CancellationToken ct = default);

    /// <summary>Removes whichever worktree holds <paramref name="branchName"/>.</summary>
    Task RemoveWorktreeForBranchAsync(string repoPath, string branchName, bool force = false, CancellationToken ct = default);

    /// <summary>Drops administrative entries for worktrees whose directories are gone.</summary>
    Task PruneWorktreesAsync(string repoPath, CancellationToken ct = default);

    // NOTE: RunCommandAsync was deliberately removed from this interface — it was an
    // unvalidated git argument passthrough. See the note at the end of GitService.cs.
}
