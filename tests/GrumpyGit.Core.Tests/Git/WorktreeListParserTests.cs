using FluentAssertions;
using GrumpyGit.Core.Git;

namespace GrumpyGit.Core.Tests.Git;

public class WorktreeListParserTests
{
    private const string TwoWorktrees =
        "worktree C:/src/grumpygit\n" +
        "HEAD 62d2e94ad80f8683c6b06e2df4a0b8206d2d6b64\n" +
        "branch refs/heads/master\n" +
        "\n" +
        "worktree C:/src/grumpygit-worktrees/feature-tabs\n" +
        "HEAD 1c9d3f0aa11bb22cc33dd44ee55ff66aa77bb889\n" +
        "branch refs/heads/feature/tabs\n" +
        "\n";

    [Fact]
    public void Parse_ReadsEveryRecord()
    {
        WorktreeListParser.Parse(TwoWorktrees).Should().HaveCount(2);
    }

    [Fact]
    public void Parse_TreatsFirstRecordAsTheMainWorktree()
    {
        var worktrees = WorktreeListParser.Parse(TwoWorktrees);

        worktrees[0].IsMain.Should().BeTrue();
        worktrees[0].IsLinked.Should().BeFalse();
        worktrees[1].IsMain.Should().BeFalse();
        worktrees[1].IsLinked.Should().BeTrue();
    }

    [Fact]
    public void Parse_StripsRefsHeadsButKeepsSlashesInsideTheBranchName()
    {
        var worktrees = WorktreeListParser.Parse(TwoWorktrees);

        worktrees[0].Branch.Should().Be("master");
        worktrees[1].Branch.Should().Be("feature/tabs");
    }

    [Fact]
    public void Parse_ReadsPathAndHead()
    {
        var worktrees = WorktreeListParser.Parse(TwoWorktrees);

        worktrees[1].Path.Should().Be("C:/src/grumpygit-worktrees/feature-tabs");
        worktrees[1].Head.Should().Be("1c9d3f0aa11bb22cc33dd44ee55ff66aa77bb889");
    }

    [Fact]
    public void Parse_ReadsDetachedAsANullBranch()
    {
        const string output =
            "worktree C:/src/repo\n" +
            "HEAD abc1234\n" +
            "detached\n";

        var worktree = WorktreeListParser.Parse(output).Single();

        worktree.IsDetached.Should().BeTrue();
        worktree.Branch.Should().BeNull();
    }

    [Fact]
    public void Parse_ReadsBareMainWorktree()
    {
        const string output =
            "worktree C:/src/repo.git\n" +
            "bare\n";

        var worktree = WorktreeListParser.Parse(output).Single();

        worktree.IsBare.Should().BeTrue();
        worktree.IsMain.Should().BeTrue();
        worktree.Head.Should().BeEmpty();
    }

    [Fact]
    public void Parse_CapturesLockAndPruneReasons()
    {
        const string output =
            "worktree C:/src/repo\n" +
            "HEAD abc1234\n" +
            "branch refs/heads/master\n" +
            "\n" +
            "worktree C:/src/gone\n" +
            "HEAD def5678\n" +
            "branch refs/heads/stale\n" +
            "locked holding an in-progress bisect\n" +
            "prunable gitdir file points to non-existent location\n";

        var stale = WorktreeListParser.Parse(output)[1];

        stale.IsLocked.Should().BeTrue();
        stale.LockReason.Should().Be("holding an in-progress bisect");
        stale.IsPrunable.Should().BeTrue();
        stale.PrunableReason.Should().Be("gitdir file points to non-existent location");
    }

    [Fact]
    public void Parse_TreatsValuelessLockedAsLockedWithNoReason()
    {
        const string output =
            "worktree C:/src/repo\n" +
            "HEAD abc1234\n" +
            "branch refs/heads/master\n" +
            "locked\n";

        var worktree = WorktreeListParser.Parse(output).Single();

        worktree.IsLocked.Should().BeTrue();
        worktree.LockReason.Should().BeNull();
    }

    /// <summary>
    /// git writes LF, but a buffered read through a Windows pipe can surface CRLF. A
    /// stray CR would otherwise end up on the end of the parsed path and every
    /// subsequent path comparison would miss.
    /// </summary>
    [Fact]
    public void Parse_ToleratesCrlf()
    {
        var output = TwoWorktrees.Replace("\n", "\r\n");

        var worktrees = WorktreeListParser.Parse(output);

        worktrees.Should().HaveCount(2);
        worktrees[0].Path.Should().Be("C:/src/grumpygit");
        worktrees[1].Branch.Should().Be("feature/tabs");
    }

    /// <summary>
    /// git terminates the last record with a blank line, but a truncated stream must not
    /// silently drop the entry it was in the middle of.
    /// </summary>
    [Fact]
    public void Parse_KeepsFinalRecordWithoutTrailingBlankLine()
    {
        const string output =
            "worktree C:/src/repo\n" +
            "HEAD abc1234\n" +
            "branch refs/heads/master";

        WorktreeListParser.Parse(output).Should().ContainSingle()
            .Which.Branch.Should().Be("master");
    }

    [Fact]
    public void Parse_StartsANewRecordOnAWorktreeLineWithNoBlankSeparator()
    {
        const string output =
            "worktree C:/src/a\n" +
            "branch refs/heads/one\n" +
            "worktree C:/src/b\n" +
            "branch refs/heads/two\n";

        var worktrees = WorktreeListParser.Parse(output);

        worktrees.Should().HaveCount(2);
        worktrees[0].Branch.Should().Be("one");
        worktrees[1].Branch.Should().Be("two");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    public void Parse_ReturnsEmptyForNoOutput(string output)
    {
        WorktreeListParser.Parse(output).Should().BeEmpty();
    }

    [Fact]
    public void Name_FallsBackToTheLastPathSegment()
    {
        var worktrees = WorktreeListParser.Parse(TwoWorktrees);

        worktrees[1].Name.Should().Be("feature-tabs");
    }
}
