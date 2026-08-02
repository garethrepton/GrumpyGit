using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Git;

/// <summary>
/// Parses <c>git worktree list --porcelain</c>.
///
/// The format is one attribute per line, records separated by a blank line:
/// <code>
/// worktree C:/src/repo
/// HEAD 8f3a...
/// branch refs/heads/master
///
/// worktree C:/src/repo-worktrees/feature-x
/// HEAD 1c9d...
/// branch refs/heads/feature/x
/// locked holding an in-progress bisect
/// </code>
/// <c>bare</c>, <c>detached</c>, <c>locked</c> and <c>prunable</c> are valueless or
/// take an optional trailing reason. The first record is always the main worktree,
/// which is the only reliable way to tell it apart — git does not label it.
/// </summary>
public static class WorktreeListParser
{
    public static IReadOnlyList<GitWorktree> Parse(string porcelainOutput)
    {
        var worktrees = new List<GitWorktree>();
        if (string.IsNullOrWhiteSpace(porcelainOutput))
            return worktrees;

        // Normalise line endings before splitting: git writes LF, but a buffered read
        // through a Windows pipe can surface CRLF, and a stray CR would end up inside
        // the parsed path.
        var lines = porcelainOutput
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n');

        string? path = null;
        var head = string.Empty;
        string? branch = null;
        bool bare = false, detached = false, locked = false, prunable = false;
        string? lockReason = null, prunableReason = null;

        void Flush()
        {
            if (path is null) return;

            worktrees.Add(new GitWorktree
            {
                Path = path,
                Head = head,
                Branch = branch,
                // Position, not an attribute: git emits the main worktree first.
                IsMain = worktrees.Count == 0,
                IsBare = bare,
                IsDetached = detached,
                IsLocked = locked,
                LockReason = lockReason,
                IsPrunable = prunable,
                PrunableReason = prunableReason,
            });

            path = null;
            head = string.Empty;
            branch = null;
            bare = detached = locked = prunable = false;
            lockReason = prunableReason = null;
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            // Blank line terminates the current record.
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            var space = line.IndexOf(' ');
            var key = space < 0 ? line : line[..space];
            var value = space < 0 ? string.Empty : line[(space + 1)..];

            switch (key)
            {
                case "worktree":
                    // A new "worktree" line without an intervening blank line still starts
                    // a new record — tolerated so a truncated stream cannot merge two entries.
                    Flush();
                    path = value;
                    break;
                case "HEAD":
                    head = value;
                    break;
                case "branch":
                    branch = StripRefsHeads(value);
                    break;
                case "bare":
                    bare = true;
                    break;
                case "detached":
                    detached = true;
                    break;
                case "locked":
                    locked = true;
                    lockReason = value.Length > 0 ? value : null;
                    break;
                case "prunable":
                    prunable = true;
                    prunableReason = value.Length > 0 ? value : null;
                    break;
            }
        }

        // Final record: git's output ends with a blank line, but do not depend on it.
        Flush();
        return worktrees;
    }

    private static string StripRefsHeads(string refName) =>
        refName.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? refName["refs/heads/".Length..]
            : refName;
}
