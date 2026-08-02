namespace GrumpyGit.Core.Models;

/// <summary>
/// One entry from <c>git worktree list --porcelain</c>.
///
/// A worktree in this application is always bound to a branch: it is created for a
/// branch, it is identified by that branch, and it is removed by that branch. Git
/// itself already refuses to check the same branch out in two worktrees at once, and
/// the client layers a stricter rule on top — see <see cref="IsLinked"/>.
/// </summary>
public sealed record GitWorktree
{
    /// <summary>Absolute path to the worktree's working directory.</summary>
    public required string Path { get; init; }

    /// <summary>Commit the worktree's HEAD points at. Empty for a bare main worktree.</summary>
    public string Head { get; init; } = string.Empty;

    /// <summary>
    /// Short branch name (<c>refs/heads/</c> stripped), or null when the worktree is
    /// detached or bare. A null branch is what <see cref="IsDetached"/> reports on.
    /// </summary>
    public string? Branch { get; init; }

    /// <summary>True for the repository's original working directory, false for linked worktrees.</summary>
    public bool IsMain { get; init; }

    public bool IsBare { get; init; }

    /// <summary>Detached HEAD — no branch is checked out.</summary>
    public bool IsDetached { get; init; }

    /// <summary>Locked via <c>git worktree lock</c>; removal needs --force.</summary>
    public bool IsLocked { get; init; }

    /// <summary>Reason recorded with the lock, if one was given.</summary>
    public string? LockReason { get; init; }

    /// <summary>
    /// Git considers this entry removable by <c>git worktree prune</c> — usually because
    /// its directory has been deleted from disk behind git's back.
    /// </summary>
    public bool IsPrunable { get; init; }

    public string? PrunableReason { get; init; }

    /// <summary>
    /// A linked worktree, i.e. anything that is not the main working directory. This is
    /// the flag the UI keys its branch lock off: a linked worktree exists to hold one
    /// branch, so switching branches inside it is refused.
    /// </summary>
    public bool IsLinked => !IsMain;

    /// <summary>Last path segment — what the worktree is called in the UI.</summary>
    public string Name
    {
        get
        {
            var trimmed = Path.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar);
            var name = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }
    }

    /// <summary>True when the worktree directory is missing from disk.</summary>
    public bool IsMissing => !Directory.Exists(Path);
}
