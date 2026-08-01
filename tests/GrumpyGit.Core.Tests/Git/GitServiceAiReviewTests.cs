using FluentAssertions;
using GrumpyGit.Core.Ai;
using GrumpyGit.Core.Git;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests.Git;

/// <summary>
/// Integration tests for the AI-session review data path against a real git repo:
/// trailer parsing out of git log, session grouping, and the net range file list.
/// </summary>
public class GitServiceAiReviewTests : IDisposable
{
    private readonly GitService _git = new();
    private readonly string _repoPath;

    public GitServiceAiReviewTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), $"grumpygit-ai-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoPath);
        RunGit("init");
        RunGit("config user.email test@test.com");
        RunGit("config user.name TestUser");
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.GetFiles(_repoPath, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_repoPath, true);
        }
        catch { /* ignore cleanup failures */ }
    }

    private void RunGit(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = _repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit(10_000);
    }

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_repoPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>Commits with a message file so multi-line trailers survive intact.</summary>
    private void Commit(string message)
    {
        var msgFile = Path.Combine(_repoPath, "COMMIT_MSG.tmp");
        File.WriteAllText(msgFile, message);
        RunGit($"commit -q -F \"{msgFile}\"");
        File.Delete(msgFile);
    }

    [Fact]
    public async Task CoAuthoredByTrailer_IsParsedOutOfGitLog()
    {
        Write("a.txt", "one");
        RunGit("add a.txt");
        Commit("feat: add a\n\nCo-Authored-By: Claude <noreply@anthropic.com>\n");

        var commits = await _git.GetCommitGraphAsync(_repoPath);

        commits.Should().HaveCount(1);
        commits[0].CoAuthors.Should().ContainSingle()
            .Which.Should().Contain("noreply@anthropic.com");
    }

    [Fact]
    public async Task CommitWithoutTrailer_HasNoCoAuthors()
    {
        Write("a.txt", "one");
        RunGit("add a.txt");
        Commit("chore: plain human commit\n");

        var commits = await _git.GetCommitGraphAsync(_repoPath);

        commits[0].CoAuthors.Should().BeEmpty();
    }

    [Fact]
    public async Task AgentCommits_AreGroupedIntoASession_AndDetectedEndToEnd()
    {
        Write("base.txt", "base");
        RunGit("add base.txt");
        Commit("chore: baseline\n");

        Write("a.txt", "one");
        RunGit("add a.txt");
        Commit("feat: agent step one\n\nCo-Authored-By: Claude <noreply@anthropic.com>\n");

        Write("b.txt", "two");
        RunGit("add b.txt");
        Commit("feat: agent step two\n\nCo-Authored-By: Claude <noreply@anthropic.com>\n");

        var commits = await _git.GetCommitGraphAsync(_repoPath);
        var sessions = AiSessionBuilder.Build(commits);

        var session = sessions.Should().ContainSingle().Subject;
        session.AgentName.Should().Be("Claude");
        session.CommitCount.Should().Be(2);
        session.BaseHash.Should().NotBeNull("the session must have a baseline commit to diff against");
    }

    [Fact]
    public async Task SessionFileList_ShowsNetChange_NotPerCommitUnion()
    {
        Write("keep.txt", "base");
        RunGit("add keep.txt");
        Commit("chore: baseline\n");
        var baseHash = (await _git.GetCommitGraphAsync(_repoPath))[0].Hash;

        // Agent creates a file...
        Write("temp.txt", "scratch");
        Write("keep.txt", "changed");
        RunGit("add temp.txt keep.txt");
        Commit("feat: agent adds scratch file\n\nCo-Authored-By: Claude <noreply@anthropic.com>\n");

        // ...then deletes it again within the same session.
        File.Delete(Path.Combine(_repoPath, "temp.txt"));
        RunGit("add -u");
        Commit("chore: agent removes scratch file\n\nCo-Authored-By: Claude <noreply@anthropic.com>\n");

        var headHash = (await _git.GetCommitGraphAsync(_repoPath))[0].Hash;

        var files = await _git.GetCommitRangeFileListAsync(_repoPath, baseHash, headHash);

        // temp.txt was created and removed inside the session, so it is not a net change.
        files.Select(f => f.Path).Should().BeEquivalentTo(["keep.txt"]);
        files[0].Status.Should().Be(FileChangeStatus.Modified);
    }

    [Fact]
    public async Task RangeStats_ReportPerFileChurn()
    {
        Write("a.txt", "l1\nl2\nl3\n");
        RunGit("add a.txt");
        Commit("chore: baseline\n");
        var baseHash = (await _git.GetCommitGraphAsync(_repoPath))[0].Hash;

        Write("a.txt", "l1\nl2\nl3\nl4\nl5\n");
        RunGit("add a.txt");
        Commit("feat: agent appends lines\n\nCo-Authored-By: Claude <noreply@anthropic.com>\n");
        var headHash = (await _git.GetCommitGraphAsync(_repoPath))[0].Hash;

        var stats = await _git.GetCommitRangeStatsAsync(_repoPath, baseHash, headHash);

        stats.Should().ContainKey("a.txt");
        stats["a.txt"].Added.Should().Be(2);
        stats["a.txt"].Removed.Should().Be(0);
    }

    [Fact]
    public async Task RangeFileList_RejectsNonHashArguments()
    {
        Write("a.txt", "one");
        RunGit("add a.txt");
        Commit("chore: baseline\n");
        var hash = (await _git.GetCommitGraphAsync(_repoPath))[0].Hash;

        // A ref starting with '-' is the classic argument-injection vector.
        var act = async () => await _git.GetCommitRangeFileListAsync(_repoPath, "--upload-pack=evil", hash);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RenamedFile_IsReportedWithBothPaths()
    {
        Write("old.txt", string.Concat(Enumerable.Repeat("stable line\n", 20)));
        RunGit("add old.txt");
        Commit("chore: baseline\n");
        var baseHash = (await _git.GetCommitGraphAsync(_repoPath))[0].Hash;

        RunGit("mv old.txt new.txt");
        Commit("refactor: agent renames file\n\nCo-Authored-By: Claude <noreply@anthropic.com>\n");
        var headHash = (await _git.GetCommitGraphAsync(_repoPath))[0].Hash;

        var files = await _git.GetCommitRangeFileListAsync(_repoPath, baseHash, headHash);

        var renamed = files.Should().ContainSingle().Subject;
        renamed.Status.Should().Be(FileChangeStatus.Renamed);
        renamed.Path.Should().Be("new.txt");
        renamed.OldPath.Should().Be("old.txt");
    }
}
