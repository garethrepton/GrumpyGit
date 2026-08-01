using FluentAssertions;
using GrumpyGit.Core.Git;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests.Git;

public class MovedBlockDetectorTests
{
    private static ParsedDiff Aligned(string[] left, string[] right) =>
        new(string.Join('\n', left), string.Join('\n', right), [], [], []);

    [Fact]
    public void BlockRemovedHereAndAddedThere_IsReportedAsAMove()
    {
        var left = new[] { "one", "two", "three", "", "", "", "tail" };
        var right = new[] { "", "", "", "one", "two", "three", "tail" };

        var moves = MovedBlockDetector.Detect(Aligned(left, right));

        var move = moves.Should().ContainSingle().Subject;
        move.FromLine.Should().Be(1);
        move.ToLine.Should().Be(4);
        move.Length.Should().Be(3);
    }

    [Fact]
    public void ShortRun_IsNotAMove()
    {
        // Two lines is below the threshold — pairing filler like "}" would be noise.
        var moves = MovedBlockDetector.Detect(
            Aligned(["}", "x", "", ""], ["", "", "}", "x"]));

        moves.Should().BeEmpty();
    }

    [Fact]
    public void ReindentedBlock_StillCountsAsMoved()
    {
        var left = new[] { "one", "two", "three", "", "", "" };
        var right = new[] { "", "", "", "    one", "    two", "    three" };

        MovedBlockDetector.Detect(Aligned(left, right))
            .Should().ContainSingle().Which.Length.Should().Be(3);
    }

    [Fact]
    public void GenuineRewrite_IsNotReportedAsAMove()
    {
        var moves = MovedBlockDetector.Detect(
            Aligned(["alpha", "beta", "gamma"], ["delta", "epsilon", "zeta"]));

        moves.Should().BeEmpty();
    }

    [Fact]
    public void WhitespaceOnlyLines_AreNeverUsedAsASeed()
    {
        // All-blank runs match each other trivially; treating them as moves would mark
        // every reformatted file as one giant move.
        var moves = MovedBlockDetector.Detect(
            Aligned(["   ", "  ", " ", ""], ["", " ", "  ", "   "]));

        moves.Should().BeEmpty();
    }
}
