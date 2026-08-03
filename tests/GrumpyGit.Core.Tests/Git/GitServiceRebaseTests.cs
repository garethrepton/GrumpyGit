using FluentAssertions;
using GrumpyGit.Core.Git;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests.Git;

/// <summary>
/// Integration tests for ExecuteRebaseAsync against a real repository.
///
/// The space-in-path case is not padding. Git runs GIT_SEQUENCE_EDITOR through sh, so
/// the editor string is subject to shell word splitting — and the previous
/// implementation, a generated .cmd, failed for a different reason entirely: sh cannot
/// execute a .cmd and read it as a shell script instead, so interactive rebase never
/// worked at all. Both cases are asserted so neither regression can return unnoticed.
/// </summary>
public class GitServiceRebaseTests : IDisposable
{
    private readonly GitService _git = new();
    private readonly List<string> _created = [];

    public void Dispose()
    {
        foreach (var path in _created)
        {
            try
            {
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(path, true);
            }
            catch { /* best effort */ }
        }
    }

    [Theory]
    [InlineData("grumpygit-test")]
    [InlineData("grumpygit test with spaces")]
    public async Task ExecuteRebaseAsync_drops_a_commit(string folderPrefix)
    {
        var repo = NewRepo(folderPrefix);
        var first = CreateCommit(repo, "commit 1", "a.txt");
        CreateCommit(repo, "commit 2", "b.txt");
        var third = CreateCommit(repo, "commit 3", "c.txt");

        // Separate files so removing the middle commit cannot conflict.
        // Rebase onto commit 1, keeping only commit 3 — commit 2 is dropped by omission.
        await _git.ExecuteRebaseAsync(repo, first,
            [new RebaseAction(RebaseActionType.Pick, third, "commit 3")]);

        Subjects(repo).Should().Equal("commit 3", "commit 1");
    }

    [Theory]
    [InlineData("grumpygit-test")]
    [InlineData("grumpygit test with spaces")]
    public async Task ExecuteRebaseAsync_reorders_commits(string folderPrefix)
    {
        var repo = NewRepo(folderPrefix);
        var first = CreateCommit(repo, "commit 1", "a.txt");
        var second = CreateCommit(repo, "commit 2", "b.txt");
        var third = CreateCommit(repo, "commit 3", "c.txt");

        // Separate files so the swap cannot conflict.
        await _git.ExecuteRebaseAsync(repo, first,
        [
            new RebaseAction(RebaseActionType.Pick, third, "commit 3"),
            new RebaseAction(RebaseActionType.Pick, second, "commit 2"),
        ]);

        Subjects(repo).Should().Equal("commit 2", "commit 3", "commit 1");
    }

    /// <summary>
    /// A subject carrying a newline would inject an extra todo line. GitService strips
    /// them before the todo is written; this pins that down end to end.
    /// </summary>
    [Fact]
    public async Task ExecuteRebaseAsync_does_not_let_a_subject_inject_a_todo_line()
    {
        var repo = NewRepo("grumpygit-test");
        var first = CreateCommit(repo, "commit 1");
        var second = CreateCommit(repo, "commit 2");

        await _git.ExecuteRebaseAsync(repo, first,
            [new RebaseAction(RebaseActionType.Pick, second, "subject\ndrop " + first)]);

        Subjects(repo).Should().Equal("commit 2", "commit 1");
    }

    // ── fixture ──────────────────────────────────────────────────────────────

    private string NewRepo(string folderPrefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{folderPrefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _created.Add(path);

        Run(path, "init");
        Run(path, "config", "user.email", "test@example.invalid");
        Run(path, "config", "user.name", "TestUser");
        return path;
    }

    private static string CreateCommit(string repo, string message, string filename = "file.txt")
    {
        File.AppendAllText(Path.Combine(repo, filename), $"{message}\n");
        Run(repo, "add", filename);
        Run(repo, "commit", "-m", message);
        return Run(repo, "rev-parse", "HEAD").Trim();
    }

    private static string[] Subjects(string repo) =>
        Run(repo, "log", "--format=%s")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .ToArray();

    /// <summary>
    /// Arguments passed as arguments, never as a command line — the repository paths here
    /// deliberately contain spaces, and a string-built command would silently split them.
    /// </summary>
    private static string Run(string repo, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(10_000);
        return stdout;
    }
}
