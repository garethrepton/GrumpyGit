using FluentAssertions;
using GrumpyGit.Core.Git;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests.Git;

/// <summary>
/// Integration tests for the repository-level operations: init, clone, fetch, remotes,
/// branch delete/rename, remote-branch checkout, amend, cherry-pick and reset.
///
/// Everything runs against repositories built here in a temp directory — a clone from a
/// local path exercises the same code as a clone from a URL without a network in the
/// test, and a bare repository stands in for the server so push has somewhere to go.
/// </summary>
public class GitServiceRepoOpsTests : IDisposable
{
    private readonly GitService _git = new();
    private readonly string _root;

    public GitServiceRepoOpsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"grumpygit-repoops-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch { /* ignore cleanup failures */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string RunGit(string workingDirectory, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        var proc = System.Diagnostics.Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(15_000);
        return output.Trim();
    }

    private string NewDirectory(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A repository with one commit on its default branch.</summary>
    private string NewRepo(string name)
    {
        var path = NewDirectory(name);
        RunGit(path, "init");
        RunGit(path, "config", "user.email", "test@test.com");
        RunGit(path, "config", "user.name", "TestUser");
        Commit(path, "first", "file.txt");
        return path;
    }

    private static void Commit(string repo, string message, string file = "file.txt")
    {
        File.AppendAllText(Path.Combine(repo, file), $"{message}\n");
        RunGit(repo, "add", file);
        RunGit(repo, "commit", "-m", message);
    }

    private static string Head(string repo) => RunGit(repo, "rev-parse", "HEAD");

    private static int CommitCount(string repo) =>
        int.Parse(RunGit(repo, "rev-list", "--count", "HEAD"));

    // ── Init ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task InitRepository_MakesTheFolderARepository()
    {
        var path = NewDirectory("fresh");

        await _git.InitRepositoryAsync(path);

        (await _git.IsRepositoryAsync(path)).Should().BeTrue();
    }

    [Fact]
    public async Task InitRepository_RefusesAnExistingRepository()
    {
        var repo = NewRepo("already");

        var act = () => _git.InitRepositoryAsync(repo);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task IsRepository_IsFalseForAPlainFolder()
    {
        (await _git.IsRepositoryAsync(NewDirectory("plain"))).Should().BeFalse();
    }

    // ── Clone ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Clone_CopiesHistoryIntoTheNamedFolder()
    {
        var source = NewRepo("source");
        var destinations = NewDirectory("clones");

        var path = await _git.CloneAsync(destinations, source, "mine");

        path.Should().Be(Path.Combine(destinations, "mine"));
        (await _git.IsRepositoryAsync(path)).Should().BeTrue();
        CommitCount(path).Should().Be(1);
    }

    [Fact]
    public async Task Clone_DerivesTheFolderNameFromTheUrl()
    {
        var source = NewRepo("derive-me");
        var destinations = NewDirectory("clones-derived");

        var path = await _git.CloneAsync(destinations, source);

        Path.GetFileName(path).Should().Be("derive-me");
    }

    [Theory]
    [InlineData("--upload-pack=calc.exe")]
    [InlineData("ext::sh -c whoami")]
    [InlineData("javascript:alert(1)")]
    public async Task Clone_RejectsAUrlThatIsNotATransportItSupports(string url)
    {
        var destinations = NewDirectory($"clones-rejected-{Math.Abs(url.GetHashCode())}");

        var act = () => _git.CloneAsync(destinations, url);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Theory]
    [InlineData("..")]
    [InlineData("sub/dir")]
    [InlineData("../escape")]
    public async Task Clone_RejectsAFolderNameThatIsNotASingleSegment(string folderName)
    {
        var source = NewRepo($"src-{Math.Abs(folderName.GetHashCode())}");
        var destinations = NewDirectory($"dest-{Math.Abs(folderName.GetHashCode())}");

        var act = () => _git.CloneAsync(destinations, source, folderName);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Clone_RefusesToWriteIntoAFolderThatHasContent()
    {
        var source = NewRepo("occupied-source");
        var destinations = NewDirectory("occupied-dest");
        var target = Path.Combine(destinations, "taken");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "mine");

        var act = () => _git.CloneAsync(destinations, source, "taken");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Fetch / upstream ──────────────────────────────────────────────────────

    /// <summary>A bare repository to push to, and a working clone of it.</summary>
    private (string Origin, string Work) NewClonePair(string name)
    {
        var source = NewRepo($"{name}-source");
        var origin = Path.Combine(_root, $"{name}-origin.git");
        RunGit(_root, "clone", "--bare", source, origin);

        var work = Path.Combine(_root, $"{name}-work");
        RunGit(_root, "clone", origin, work);
        RunGit(work, "config", "user.email", "test@test.com");
        RunGit(work, "config", "user.name", "TestUser");
        return (origin, work);
    }

    [Fact]
    public async Task Fetch_SeesABranchCreatedOnTheRemote()
    {
        var (origin, work) = NewClonePair("fetch");
        RunGit(origin, "branch", "colleague-work");

        (await _git.GetRemoteBranchesAsync(work)).Should().NotContain("origin/colleague-work");

        await _git.FetchAsync(work);

        (await _git.GetRemoteBranchesAsync(work)).Should().Contain("origin/colleague-work");
    }

    [Fact]
    public async Task Fetch_WithPrune_DropsATrackingRefForADeletedBranch()
    {
        var (origin, work) = NewClonePair("prune");
        RunGit(origin, "branch", "temporary");
        await _git.FetchAsync(work);
        (await _git.GetRemoteBranchesAsync(work)).Should().Contain("origin/temporary");

        RunGit(origin, "branch", "-D", "temporary");
        await _git.FetchAsync(work, prune: true);

        (await _git.GetRemoteBranchesAsync(work)).Should().NotContain("origin/temporary");
    }

    [Fact]
    public async Task GetRemoteBranches_LeavesOutTheRemoteHeadAlias()
    {
        var (_, work) = NewClonePair("head-alias");

        (await _git.GetRemoteBranchesAsync(work)).Should().NotContain(b => b.EndsWith("/HEAD"));
    }

    [Fact]
    public async Task Push_WithSetUpstream_GivesANewBranchSomewhereToGo()
    {
        var (_, work) = NewClonePair("upstream");
        await _git.CreateBranchAsync(work, "feature/new");
        Commit(work, "work on the feature");

        (await _git.HasUpstreamAsync(work, "feature/new")).Should().BeFalse();

        await _git.PushAsync(work, "origin", "feature/new", setUpstream: true);

        (await _git.HasUpstreamAsync(work, "feature/new")).Should().BeTrue();
    }

    [Fact]
    public async Task Push_SetUpstreamWithoutABranch_IsRejected()
    {
        var (_, work) = NewClonePair("upstream-noname");

        var act = () => _git.PushAsync(work, "origin", null, setUpstream: true);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── Remote-branch checkout ────────────────────────────────────────────────

    [Fact]
    public async Task CheckoutRemoteBranch_CreatesALocalBranchThatTracksIt()
    {
        var (origin, work) = NewClonePair("checkout-remote");
        RunGit(origin, "branch", "theirs");
        await _git.FetchAsync(work);

        var local = await _git.CheckoutRemoteBranchAsync(work, "origin/theirs");

        local.Should().Be("theirs");
        (await _git.GetCurrentBranchAsync(work)).Should().Be("theirs");
        (await _git.HasUpstreamAsync(work, "theirs")).Should().BeTrue();
    }

    [Fact]
    public async Task CheckoutRemoteBranch_SwitchesToTheLocalBranchOnASecondVisit()
    {
        var (origin, work) = NewClonePair("checkout-twice");
        RunGit(origin, "branch", "theirs");
        await _git.FetchAsync(work);
        var first = await _git.CheckoutRemoteBranchAsync(work, "origin/theirs");
        await _git.CheckoutBranchAsync(work, await DefaultBranchAsync(work, first));

        var second = await _git.CheckoutRemoteBranchAsync(work, "origin/theirs");

        second.Should().Be("theirs");
        (await _git.GetCurrentBranchAsync(work)).Should().Be("theirs");
    }

    /// <summary>Whichever branch the clone started on — init's default is not fixed.</summary>
    private async Task<string> DefaultBranchAsync(string repo, string exclude)
    {
        var branches = await _git.GetBranchesAsync(repo);
        return branches.First(b => b != exclude);
    }

    [Fact]
    public async Task CheckoutRemoteBranch_RejectsAPlainBranchName()
    {
        var (_, work) = NewClonePair("checkout-plain");

        var act = () => _git.CheckoutRemoteBranchAsync(work, "theirs");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── Branch delete / rename ────────────────────────────────────────────────

    [Fact]
    public async Task DeleteBranch_RemovesAMergedBranch()
    {
        var repo = NewRepo("delete-merged");
        var start = await _git.GetCurrentBranchAsync(repo);
        RunGit(repo, "branch", "spare");

        await _git.DeleteBranchAsync(repo, "spare");

        (await _git.GetBranchesAsync(repo)).Should().Equal(start);
    }

    [Fact]
    public async Task DeleteBranch_RefusesTheBranchYouAreStandingOn()
    {
        var repo = NewRepo("delete-current");
        var current = await _git.GetCurrentBranchAsync(repo);

        var act = () => _git.DeleteBranchAsync(repo, current);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task DeleteBranch_RefusesUnmergedWorkUntilForced()
    {
        var repo = NewRepo("delete-unmerged");
        var start = await _git.GetCurrentBranchAsync(repo);
        await _git.CreateBranchAsync(repo, "unmerged");
        Commit(repo, "only on this branch", "other.txt");
        await _git.CheckoutBranchAsync(repo, start);

        var act = () => _git.DeleteBranchAsync(repo, "unmerged");
        await act.Should().ThrowAsync<GitException>();

        await _git.DeleteBranchAsync(repo, "unmerged", force: true);
        (await _git.GetBranchesAsync(repo)).Should().NotContain("unmerged");
    }

    [Fact]
    public async Task RenameBranch_KeepsTheCheckoutOnTheRenamedBranch()
    {
        var repo = NewRepo("rename");
        var start = await _git.GetCurrentBranchAsync(repo);

        await _git.RenameBranchAsync(repo, start, "renamed");

        (await _git.GetCurrentBranchAsync(repo)).Should().Be("renamed");
        (await _git.GetBranchesAsync(repo)).Should().Equal("renamed");
    }

    // ── Amend ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Amend_ReplacesTheTipRatherThanAddingToIt()
    {
        var repo = NewRepo("amend");
        Commit(repo, "second");
        var before = Head(repo);

        var hash = await _git.AmendCommitAsync(repo, "second, said better");

        hash.Should().NotBe(before);
        CommitCount(repo).Should().Be(2);
        (await _git.GetHeadCommitMessageAsync(repo)).Should().Be("second, said better");
    }

    [Fact]
    public async Task GetHeadCommitMessage_ReturnsTheWholeMessage()
    {
        var repo = NewRepo("head-message");

        (await _git.GetHeadCommitMessageAsync(repo)).Should().Be("first");
    }

    [Fact]
    public async Task Amend_RejectsAnEmptyMessage()
    {
        var repo = NewRepo("amend-empty");

        var act = () => _git.AmendCommitAsync(repo, "   ");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ── Cherry-pick ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CherryPick_CopiesTheChangeOntoTheCurrentBranch()
    {
        var repo = NewRepo("cherry-pick");
        var start = await _git.GetCurrentBranchAsync(repo);
        await _git.CreateBranchAsync(repo, "side");
        Commit(repo, "side work", "side.txt");
        var picked = Head(repo);

        // The mainline has to move on, or the copy lands on the same parent with the same
        // tree and message and git hands back the identical commit — which would make the
        // "this is a new commit" assertion below pass or fail on the clock.
        await _git.CheckoutBranchAsync(repo, start);
        Commit(repo, "mainline moves on", "main.txt");

        await _git.CherryPickAsync(repo, picked);

        File.Exists(Path.Combine(repo, "side.txt")).Should().BeTrue();
        (await _git.GetHeadCommitMessageAsync(repo)).Should().Be("side work");
        Head(repo).Should().NotBe(picked);
        CommitCount(repo).Should().Be(3);
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResetSoft_MovesTheBranchAndKeepsTheChangesStaged()
    {
        var repo = NewRepo("reset-soft");
        var first = Head(repo);
        Commit(repo, "second");

        await _git.ResetToCommitAsync(repo, first, ResetMode.Soft);

        Head(repo).Should().Be(first);
        var staged = await _git.GetWorkingTreeStatusAsync(repo);
        staged.Should().Contain(f => f.IsStaged);
    }

    [Fact]
    public async Task ResetMixed_MovesTheBranchAndLeavesTheChangesUnstaged()
    {
        var repo = NewRepo("reset-mixed");
        var first = Head(repo);
        Commit(repo, "second");

        await _git.ResetToCommitAsync(repo, first, ResetMode.Mixed);

        Head(repo).Should().Be(first);
        var status = await _git.GetWorkingTreeStatusAsync(repo);
        status.Should().NotBeEmpty();
        status.Should().NotContain(f => f.IsStaged);
    }

    [Fact]
    public async Task ResetHard_ThrowsTheLaterWorkAway()
    {
        var repo = NewRepo("reset-hard");
        var first = Head(repo);
        Commit(repo, "second", "gone.txt");

        await _git.ResetToCommitAsync(repo, first, ResetMode.Hard);

        Head(repo).Should().Be(first);
        File.Exists(Path.Combine(repo, "gone.txt")).Should().BeFalse();
        (await _git.IsWorkingTreeCleanAsync(repo)).Should().BeTrue();
    }

    // ── Remotes ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Remotes_AddThenReadBack()
    {
        var repo = NewRepo("remote-add");

        await _git.AddRemoteAsync(repo, "origin", "https://example.invalid/team/repo.git");

        var remotes = await _git.GetRemotesAsync(repo);
        remotes.Should().ContainSingle();
        remotes[0].Should().Be(new GitRemote("origin", "https://example.invalid/team/repo.git"));
    }

    [Fact]
    public async Task Remotes_SetUrlRepointsAnExistingRemote()
    {
        var repo = NewRepo("remote-set-url");
        await _git.AddRemoteAsync(repo, "origin", "https://example.invalid/old.git");

        await _git.SetRemoteUrlAsync(repo, "origin", "https://example.invalid/new.git");

        (await _git.GetRemoteUrlAsync(repo)).Should().Be("https://example.invalid/new.git");
    }

    [Fact]
    public async Task Remotes_RenameAndRemove()
    {
        var repo = NewRepo("remote-rename");
        await _git.AddRemoteAsync(repo, "origin", "https://example.invalid/repo.git");

        await _git.RenameRemoteAsync(repo, "origin", "upstream");
        (await _git.GetRemotesAsync(repo)).Should().ContainSingle(r => r.Name == "upstream");

        await _git.RemoveRemoteAsync(repo, "upstream");
        (await _git.GetRemotesAsync(repo)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("--upload-pack=calc.exe")]
    [InlineData("ext::sh -c whoami")]
    [InlineData("")]
    public async Task Remotes_RejectAUrlThatIsNotATransportItSupports(string url)
    {
        var repo = NewRepo($"remote-bad-{Math.Abs(url.GetHashCode())}");

        var act = () => _git.AddRemoteAsync(repo, "origin", url);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Remotes_RejectANameThatCouldBeReadAsAFlag()
    {
        var repo = NewRepo("remote-flag-name");

        var act = () => _git.AddRemoteAsync(repo, "--config=core.pager=calc.exe", "https://example.invalid/repo.git");

        await act.Should().ThrowAsync<ArgumentException>();
    }
}
