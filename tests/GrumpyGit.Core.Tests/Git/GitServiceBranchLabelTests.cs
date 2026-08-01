using FluentAssertions;
using GrumpyGit.Core.Git;
using GrumpyGit.Core.Graph;

namespace GrumpyGit.Core.Tests.Git;

/// <summary>
/// Verifies the seam between git's real %D decoration output and
/// <see cref="BranchLabelResolver"/> — synthetic unit tests cannot catch a mismatch
/// in the decoration format itself.
/// </summary>
public class GitServiceBranchLabelTests : IDisposable
{
    private readonly GitService _git = new();
    private readonly string _repoPath;

    public GitServiceBranchLabelTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), $"grumpygit-branch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoPath);
        RunGit("init -b main");
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

    private void CommitFile(string name, string content, string message)
    {
        File.WriteAllText(Path.Combine(_repoPath, name), content);
        RunGit($"add {name}");
        RunGit($"commit -q -m \"{message}\"");
    }

    [Fact]
    public async Task CheckedOutBranch_IsResolvedFromRealGitDecorations()
    {
        CommitFile("a.txt", "one", "first");

        var commits = await _git.GetCommitGraphAsync(_repoPath);
        var nodes = GraphLayoutEngine.Compute(commits);

        // Real git emits "HEAD -> main" here; if ParseRefNames or the resolver
        // disagreed on that shape, this would come back null.
        nodes[0].BranchLabel.Should().Be("main");
    }

    [Fact]
    public async Task MergedBranch_IsRecoveredAfterTheBranchIsDeleted()
    {
        CommitFile("base.txt", "base", "base commit");

        RunGit("checkout -q -b feature/login");
        CommitFile("login.txt", "login", "add login");

        RunGit("checkout -q main");
        CommitFile("main.txt", "main work", "main progresses");

        // --no-ff guarantees a real merge commit with the standard subject.
        RunGit("merge -q --no-ff feature/login -m \"Merge branch 'feature/login'\"");
        RunGit("branch -q -D feature/login");

        var commits = await _git.GetCommitGraphAsync(_repoPath);
        var nodes = GraphLayoutEngine.Compute(commits);

        var mergeNode = nodes[0];
        mergeNode.BranchLabel.Should().Be("main");

        // The branch ref is gone, so the merge subject is the only surviving evidence.
        mergeNode.Segments
            .Should().Contain(s => s.BranchLabel == "feature/login",
                "the merged lane must stay identifiable after the branch is deleted");
    }

    [Fact]
    public async Task LiveBranch_IsLabelledFromItsRef()
    {
        CommitFile("base.txt", "base", "base commit");
        RunGit("checkout -q -b feature/live");
        CommitFile("f.txt", "feature", "feature work");

        var commits = await _git.GetCommitGraphAsync(_repoPath);
        var nodes = GraphLayoutEngine.Compute(commits);

        var tip = nodes.First(n => n.Subject == "feature work");
        tip.BranchLabel.Should().Be("feature/live");
    }
}
