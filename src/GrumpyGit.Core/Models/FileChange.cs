namespace GrumpyGit.Core.Models;

public record FileChange(string Path, string OldPath, FileChangeStatus Status, bool IsStaged = false);

public enum FileChangeStatus
{
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    Untracked,
    Conflicted
}
