using FluentAssertions;
using GrumpyGit.Core.Git;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests.Git;

/// <summary>
/// Integration tests for the pull request preview against a real git repository:
/// merge base, the commits a merge would introduce, and the conflict simulation.
///
/// The repository is invented here rather than borrowed from anywhere real
/// (commandment 9) — the author identity below is fictional.
/// </summary>
public class GitServicePullRequestTests : IDisposable
{
    private readonly GitService _git = new();
    private readonly string _repoPath;

    public GitServicePullRequestTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), $"grumpygit-pr-test-{Guid.NewGuid():N}");
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

    private void Write(string relativePath, string content)
    {
        var full = Path.Combine(_repoPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private void Commit(string path, string content, string message)
    {
        Write(path, content);
        RunGit($"add \"{path}\"");
        RunGit($"commit -q -m \"{message}\"");
    }

    /// <summary>
    /// main and feature both change shared.txt, and feature also adds its own file.
    /// The two branches therefore diverge, and a merge would conflict on exactly one path.
    /// </summary>
    private void BuildDivergedBranches()
    {
        Commit("shared.txt", "base\n", "base commit");
        RunGit("switch -q -c feature");
        Commit("shared.txt", "feature\n", "feature edit");
        Commit("only-on-feature.txt", "new\n", "feature adds a file");
        RunGit("switch -q main");
        Commit("shared.txt", "main\n", "main edit");
    }

    [Fact]
    public async Task MergeBase_IsTheCommonAncestorNotEitherTip()
    {
        BuildDivergedBranches();

        var mergeBase = await _git.GetMergeBaseAsync(_repoPath, "main", "feature");
        var mainHead = await _git.GetBranchHeadAsync(_repoPath, "main");
        var featureHead = await _git.GetBranchHeadAsync(_repoPath, "feature");

        mergeBase.Should().NotBeEmpty();
        mergeBase.Should().NotBe(mainHead);
        mergeBase.Should().NotBe(featureHead);
    }

    [Fact]
    public async Task CommitsInRange_ExcludeCommitsAlreadyOnTheTarget()
    {
        BuildDivergedBranches();

        var mergeBase = await _git.GetMergeBaseAsync(_repoPath, "main", "feature");
        var featureHead = await _git.GetBranchHeadAsync(_repoPath, "feature");

        var commits = await _git.GetCommitsInRangeAsync(_repoPath, mergeBase, featureHead);

        commits.Select(c => c.Subject)
               .Should().Equal("feature adds a file", "feature edit");
    }

    [Fact]
    public async Task PreviewMerge_NamesTheConflictingPathOnly()
    {
        BuildDivergedBranches();

        var preview = await _git.PreviewMergeAsync(_repoPath, "main", "feature");

        preview.Outcome.Should().Be(MergeOutcome.Conflicts);
        preview.ConflictingPaths.Should().ContainSingle().Which.Should().Be("shared.txt");
    }

    [Fact]
    public async Task PreviewMerge_IsCleanWhenTheBranchesTouchDifferentFiles()
    {
        Commit("shared.txt", "base\n", "base commit");
        RunGit("switch -q -c feature");
        Commit("feature-only.txt", "new\n", "feature adds a file");
        RunGit("switch -q main");
        Commit("main-only.txt", "new\n", "main adds a file");

        var preview = await _git.PreviewMergeAsync(_repoPath, "main", "feature");

        preview.Outcome.Should().Be(MergeOutcome.Clean);
        preview.ConflictingPaths.Should().BeEmpty();
    }

    /// <summary>
    /// The preview must not disturb the checkout — the whole point is reviewing without
    /// interrupting whatever the user is in the middle of.
    /// </summary>
    [Fact]
    public async Task PreviewMerge_LeavesTheWorkingTreeAndCheckoutAlone()
    {
        BuildDivergedBranches();
        Write("uncommitted.txt", "in progress\n");

        await _git.PreviewMergeAsync(_repoPath, "main", "feature");

        var branch = await _git.GetCurrentBranchAsync(_repoPath);
        branch.Should().Be("main");
        File.ReadAllText(Path.Combine(_repoPath, "shared.txt")).Should().Be("main\n");
        File.Exists(Path.Combine(_repoPath, "uncommitted.txt")).Should().BeTrue();
        File.Exists(Path.Combine(_repoPath, "only-on-feature.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Preview_AssemblesCommitsFilesAndMergeVerdict()
    {
        BuildDivergedBranches();

        var preview = await PullRequestPreviewBuilder.BuildAsync(_git, _repoPath, "feature", "main");

        preview.SourceBranch.Should().Be("feature");
        preview.TargetBranch.Should().Be("main");
        preview.Commits.Should().HaveCount(2);
        preview.Files.Select(f => f.Path).Should().BeEquivalentTo("shared.txt", "only-on-feature.txt");
        preview.Merge.HasConflicts.Should().BeTrue();
        preview.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task Preview_OfABranchWithNothingNewIsEmpty()
    {
        Commit("shared.txt", "base\n", "base commit");
        RunGit("switch -q -c feature");

        var preview = await PullRequestPreviewBuilder.BuildAsync(_git, _repoPath, "feature", "main");

        preview.IsEmpty.Should().BeTrue();
        preview.Merge.Outcome.Should().Be(MergeOutcome.Clean);
    }

    [Fact]
    public async Task Preview_RefusesToReviewABranchAgainstItself()
    {
        Commit("shared.txt", "base\n", "base commit");

        var act = () => PullRequestPreviewBuilder.BuildAsync(_git, _repoPath, "main", "main");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Preview_RefusesBranchesWithNoSharedHistory()
    {
        Commit("shared.txt", "base\n", "base commit");
        RunGit("switch -q --orphan unrelated");
        Commit("other.txt", "unrelated\n", "unrelated root");
        RunGit("switch -q main");

        var act = () => PullRequestPreviewBuilder.BuildAsync(_git, _repoPath, "unrelated", "main");

        await act.Should().ThrowAsync<ArgumentException>()
                 .WithMessage("*no common history*");
    }

    [Theory]
    [InlineData("--upload-pack=calc.exe")]
    [InlineData("-c core.fsmonitor=calc.exe")]
    [InlineData("branch; calc.exe")]
    public async Task BranchArgumentsThatCouldActAsFlagsAreRejected(string branch)
    {
        Commit("shared.txt", "base\n", "base commit");

        var previewMerge = () => _git.PreviewMergeAsync(_repoPath, "main", branch);
        var mergeBase = () => _git.GetMergeBaseAsync(_repoPath, "main", branch);
        var branchHead = () => _git.GetBranchHeadAsync(_repoPath, branch);

        await previewMerge.Should().ThrowAsync<ArgumentException>();
        await mergeBase.Should().ThrowAsync<ArgumentException>();
        await branchHead.Should().ThrowAsync<ArgumentException>();
    }
}
