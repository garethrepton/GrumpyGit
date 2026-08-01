using FluentAssertions;
using GrumpyGit.Core.Git;

namespace GrumpyGit.Core.Tests.Git;

/// <summary>
/// Integration tests proving the diff toggles actually reach git and change its
/// output — not just that the flags are formatted correctly.
/// </summary>
public class GitServiceDiffOptionsTests : IDisposable
{
    private readonly GitService _git = new();
    private readonly string _repoPath;

    public GitServiceDiffOptionsTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), $"grumpygit-diffopt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoPath);
        RunGit("init -b main");
        RunGit("config user.email test@test.com");
        RunGit("config user.name TestUser");
        RunGit("config core.autocrlf false");
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.GetFiles(_repoPath, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_repoPath, true);
        }
        catch { }
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
        System.Diagnostics.Process.Start(psi)!.WaitForExit(10_000);
    }

    private void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(_repoPath, name), content);

    /// <summary>20 stable lines, one changed line in the middle.</summary>
    private void SeedFileWithDistantChange()
    {
        var original = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"line {i}")) + "\n";
        Write("a.txt", original);
        RunGit("add a.txt");
        RunGit("commit -q -m baseline");

        var modified = original.Replace("line 20\n", "line 20 CHANGED\n");
        Write("a.txt", modified);
    }

    [Fact]
    public async Task FocusedMode_OmitsLinesFarFromTheChange()
    {
        SeedFileWithDistantChange();

        var diff = await _git.GetUnstagedDiffAsync(
            _repoPath, "a.txt", new DiffOptions { ContextLines = 3 });

        diff.Should().Contain("line 20 CHANGED");
        diff.Should().NotContain("line 1\n", "line 1 is far outside a 3-line context window");
    }

    [Fact]
    public async Task FullFileMode_IncludesTheEntireFile()
    {
        SeedFileWithDistantChange();

        var diff = await _git.GetUnstagedDiffAsync(
            _repoPath, "a.txt", new DiffOptions { ContextLines = DiffOptions.FullFileContext });

        diff.Should().Contain("line 20 CHANGED");
        diff.Should().Contain("line 1\n", "full-file mode must show untouched lines too");
        diff.Should().Contain("line 40");
    }

    [Fact]
    public async Task IgnoreWhitespace_SuppressesAWhitespaceOnlyChange()
    {
        Write("b.txt", "alpha\nbeta\n");
        RunGit("add b.txt");
        RunGit("commit -q -m baseline");

        // Re-indent only — no semantic change.
        Write("b.txt", "alpha\n    beta\n");

        var normal = await _git.GetUnstagedDiffAsync(_repoPath, "b.txt", DiffOptions.Default);
        var ignoring = await _git.GetUnstagedDiffAsync(
            _repoPath, "b.txt", new DiffOptions { IgnoreWhitespace = true });

        normal.Should().Contain("beta", "the whitespace change is a real diff by default");
        ignoring.Should().NotContain("+    beta", "-w must suppress a whitespace-only change");
    }

    [Fact]
    public async Task IgnoreWhitespace_StillReportsRealChanges()
    {
        Write("c.txt", "alpha\nbeta\n");
        RunGit("add c.txt");
        RunGit("commit -q -m baseline");

        // Indentation AND a genuine edit.
        Write("c.txt", "alpha\n    gamma\n");

        var ignoring = await _git.GetUnstagedDiffAsync(
            _repoPath, "c.txt", new DiffOptions { IgnoreWhitespace = true });

        ignoring.Should().Contain("gamma", "-w must not hide a real content change");
    }

    [Fact]
    public async Task ContextLines_ControlHowMuchSurroundingCodeIsShown()
    {
        SeedFileWithDistantChange();

        var tight = await _git.GetUnstagedDiffAsync(
            _repoPath, "a.txt", new DiffOptions { ContextLines = 1 });
        var loose = await _git.GetUnstagedDiffAsync(
            _repoPath, "a.txt", new DiffOptions { ContextLines = 10 });

        tight.Split('\n').Length.Should().BeLessThan(loose.Split('\n').Length);
    }

    [Fact]
    public async Task WorkingTreeStats_ReportPerFileChurn()
    {
        Write("d.txt", "one\ntwo\n");
        RunGit("add d.txt");
        RunGit("commit -q -m baseline");

        Write("d.txt", "one\ntwo\nthree\nfour\n");

        var stats = await _git.GetWorkingTreeStatsAsync(_repoPath, staged: false);

        stats.Should().ContainKey("d.txt");
        stats["d.txt"].Added.Should().Be(2);
        stats["d.txt"].Removed.Should().Be(0);
    }

    [Fact]
    public async Task StagedAndUnstagedStats_AreReportedSeparately()
    {
        Write("e.txt", "one\n");
        RunGit("add e.txt");
        RunGit("commit -q -m baseline");

        // Stage one added line, then add another that stays unstaged.
        Write("e.txt", "one\ntwo\n");
        RunGit("add e.txt");
        Write("e.txt", "one\ntwo\nthree\n");

        var staged = await _git.GetWorkingTreeStatsAsync(_repoPath, staged: true);
        var unstaged = await _git.GetWorkingTreeStatsAsync(_repoPath, staged: false);

        staged["e.txt"].Added.Should().Be(1);
        unstaged["e.txt"].Added.Should().Be(1);
    }

    [Fact]
    public async Task BinaryFiles_AreOmittedFromStats_RatherThanReportedAsZero()
    {
        // git reports "-\t-\t<path>" for binary; recording that as 0/0 would be a lie.
        File.WriteAllBytes(Path.Combine(_repoPath, "blob.bin"), [0x00, 0x01, 0x02, 0xFF]);
        RunGit("add blob.bin");
        RunGit("commit -q -m baseline");

        File.WriteAllBytes(Path.Combine(_repoPath, "blob.bin"), [0x00, 0x01, 0x02, 0xFF, 0xAA, 0xBB]);

        var stats = await _git.GetWorkingTreeStatsAsync(_repoPath, staged: false);

        stats.Should().NotContainKey("blob.bin");
    }

    [Fact]
    public async Task CommitStats_ReportChurnForACommit()
    {
        Write("f.txt", "one\ntwo\n");
        RunGit("add f.txt");
        RunGit("commit -q -m baseline");
        Write("f.txt", "one\ntwo\nthree\n");
        RunGit("add f.txt");
        RunGit("commit -q -m \"add a line\"");

        var head = (await _git.GetCommitGraphAsync(_repoPath))[0].Hash;
        var stats = await _git.GetCommitStatsAsync(_repoPath, head);

        stats["f.txt"].Added.Should().Be(1);
        stats["f.txt"].Removed.Should().Be(0);
    }
}
