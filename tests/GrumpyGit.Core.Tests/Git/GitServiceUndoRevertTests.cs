using FluentAssertions;
using GrumpyGit.Core.Git;

namespace GrumpyGit.Core.Tests.Git;

/// <summary>
/// Integration tests for UndoLastCommitAsync, RevertCommitAsync,
/// GetParentCountAsync, and IsWorkingTreeCleanAsync.
/// Each test creates an isolated temporary git repo.
/// </summary>
public class GitServiceUndoRevertTests : IDisposable
{
    private readonly GitService _git = new();
    private readonly string _repoPath;

    public GitServiceUndoRevertTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), $"grumpygit-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoPath);
        RunGit("init");
        RunGit("config user.email test@test.com");
        RunGit("config user.name TestUser");
    }

    public void Dispose()
    {
        // Best-effort cleanup
        try
        {
            foreach (var file in Directory.GetFiles(_repoPath, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
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

    private void CreateCommit(string message, string filename = "file.txt")
    {
        File.AppendAllText(Path.Combine(_repoPath, filename), $"{message}\n");
        RunGit($"add {filename}");
        RunGit($"commit -m \"{message}\"");
    }

    private string GetHeadHash()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", "rev-parse HEAD")
        {
            WorkingDirectory = _repoPath,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var proc = System.Diagnostics.Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit(10_000);
        return output;
    }

    private int GetCommitCount()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", "rev-list --count HEAD")
        {
            WorkingDirectory = _repoPath,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var proc = System.Diagnostics.Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit(10_000);
        return int.Parse(output);
    }

    // ── IsWorkingTreeCleanAsync ─────────────────────────────────────────────

    [Fact]
    public async Task IsWorkingTreeCleanAsync_CleanRepo_ReturnsTrue()
    {
        CreateCommit("initial");
        var result = await _git.IsWorkingTreeCleanAsync(_repoPath);
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsWorkingTreeCleanAsync_DirtyRepo_ReturnsFalse()
    {
        CreateCommit("initial");
        File.WriteAllText(Path.Combine(_repoPath, "dirty.txt"), "dirty");

        var result = await _git.IsWorkingTreeCleanAsync(_repoPath);
        result.Should().BeFalse();
    }

    // ── GetParentCountAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task GetParentCountAsync_InitialCommit_ReturnsZero()
    {
        CreateCommit("initial");
        var hash = GetHeadHash();

        var count = await _git.GetParentCountAsync(_repoPath, hash);
        count.Should().Be(0);
    }

    [Fact]
    public async Task GetParentCountAsync_NormalCommit_ReturnsOne()
    {
        CreateCommit("first");
        CreateCommit("second");
        var hash = GetHeadHash();

        var count = await _git.GetParentCountAsync(_repoPath, hash);
        count.Should().Be(1);
    }

    [Fact]
    public async Task GetParentCountAsync_MergeCommit_ReturnsTwo()
    {
        CreateCommit("initial");
        RunGit("checkout -b feature");
        CreateCommit("feature-work", "feature.txt");
        RunGit("checkout master");
        CreateCommit("main-work", "main.txt");
        RunGit("merge feature --no-edit");

        var hash = GetHeadHash();
        var count = await _git.GetParentCountAsync(_repoPath, hash);
        count.Should().Be(2);
    }

    // ── UndoLastCommitAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task UndoLastCommitAsync_MovesHeadBack()
    {
        CreateCommit("first");
        var firstHash = GetHeadHash();
        CreateCommit("second");

        await _git.UndoLastCommitAsync(_repoPath);

        GetHeadHash().Should().Be(firstHash);
        GetCommitCount().Should().Be(1);
    }

    [Fact]
    public async Task UndoLastCommitAsync_ChangesReturnToStaging()
    {
        CreateCommit("first");
        CreateCommit("second");

        await _git.UndoLastCommitAsync(_repoPath);

        // After soft reset, the changes from "second" should be staged
        var status = await _git.GetWorkingTreeStatusAsync(_repoPath);
        status.Should().NotBeEmpty();
        status.Any(f => f.IsStaged).Should().BeTrue();
    }

    [Fact]
    public async Task UndoLastCommitAsync_InitialCommit_Throws()
    {
        CreateCommit("only-commit");

        var act = () => _git.UndoLastCommitAsync(_repoPath);
        await act.Should().ThrowAsync<GitException>();
    }

    // ── RevertCommitAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task RevertCommitAsync_CreatesRevertCommit()
    {
        CreateCommit("first");
        CreateCommit("second");
        var secondHash = GetHeadHash();
        var countBefore = GetCommitCount();

        await _git.RevertCommitAsync(_repoPath, secondHash);

        GetCommitCount().Should().Be(countBefore + 1);
    }

    [Fact]
    public async Task RevertCommitAsync_MergeCommit_Succeeds()
    {
        CreateCommit("initial");
        RunGit("checkout -b feature");
        CreateCommit("feature-work", "feature.txt");
        RunGit("checkout master");
        CreateCommit("main-work", "main.txt");
        RunGit("merge feature --no-edit");

        var mergeHash = GetHeadHash();
        var countBefore = GetCommitCount();

        await _git.RevertCommitAsync(_repoPath, mergeHash);

        GetCommitCount().Should().Be(countBefore + 1);
    }

    // ── Validation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task UndoLastCommitAsync_InvalidRepoPath_Throws()
    {
        var act = () => _git.UndoLastCommitAsync("");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RevertCommitAsync_InvalidHash_Throws()
    {
        var act = () => _git.RevertCommitAsync(_repoPath, "not-a-hash!");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetParentCountAsync_InvalidHash_Throws()
    {
        var act = () => _git.GetParentCountAsync(_repoPath, "");
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
