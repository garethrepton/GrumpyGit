using FluentAssertions;
using GrumpyGit.Core.LocalModel;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests.LocalModel;

/// <summary>
/// TOUCHES lines: the model's claim that a change reaches the filesystem, the network, a
/// process or a credential.
///
/// The badge these produce is a reason to stop and look, so the bar for accepting one is
/// higher than for the rest of the reply. A category that cannot be read is dropped, not
/// guessed at — a reader who learns the badges are half wrong stops reading all of them.
/// </summary>
public class ChangeConcernTests
{
    private static ParsedDiff Diff()
    {
        var hunk = new DiffHunk
        {
            HeaderLine = "@@ -1,4 +1,4 @@",
            Lines =
            [
                new DiffLine { Type = DiffLineType.Added, Content = "File.Delete(path);", NewLineNumber = 1, RenderedLineNumber = 1 },
                new DiffLine { Type = DiffLineType.Context, Content = "x", RenderedLineNumber = 2 },
                new DiffLine { Type = DiffLineType.Context, Content = "y", RenderedLineNumber = 3 },
                new DiffLine { Type = DiffLineType.Added, Content = "var n = 1;", NewLineNumber = 4, RenderedLineNumber = 4 },
            ],
        };

        return new ParsedDiff("old", "new", [], [], [], hunks: [hunk]);
    }

    private static DiffReviewResult Parse(string reply) => DiffReviewParser.Parse(reply, Diff());

    [Theory]
    [InlineData("files — deletes a file on disk", ConcernKind.Files)]
    [InlineData("network: opens a connection", ConcernKind.Network)]
    [InlineData("Process starts git.exe", ConcernKind.Process)]
    [InlineData("credentials handles a token", ConcernKind.Credentials)]
    [InlineData("data-loss overwrites the branch", ConcernKind.DataLoss)]
    public void EachCategoryIsRecognisedHoweverItIsPunctuated(string text, ConcernKind expected)
    {
        var result = Parse($"SUMMARY: x\nTOUCHES 1: {text}");

        result.Concerns.Should().ContainSingle();
        result.Concerns[0].Kind.Should().Be(expected);
        result.Concerns[0].ChangeNumber.Should().Be(1);
    }

    [Fact]
    public void TheCategoryWordIsStrippedFromTheDetail()
    {
        var result = Parse("SUMMARY: x\nTOUCHES 1: files — deletes a file on disk");

        result.Concerns[0].Text.Should().Be("deletes a file on disk");
    }

    [Fact]
    public void ALineWithNoRecognisedCategoryIsDropped()
    {
        // Prose where a category was asked for. A badge that says "consequential" without
        // saying how is a reason to stop with nothing to look at.
        var result = Parse("SUMMARY: x\nTOUCHES 1: this looks a bit risky to me");

        result.Concerns.Should().BeEmpty();
    }

    [Fact]
    public void AConcernForAChangeThatDoesNotExistIsDropped()
    {
        Parse("SUMMARY: x\nTOUCHES 9: files — writes something").Concerns.Should().BeEmpty();
    }

    [Fact]
    public void TheFirstAnswerForAChangeWins()
    {
        // A repeated label is a model looping, not a correction.
        var result = Parse("SUMMARY: x\nTOUCHES 1: files — writes\nTOUCHES 1: network — connects");

        result.Concerns.Should().ContainSingle();
        result.Concerns[0].Kind.Should().Be(ConcernKind.Files);
    }

    [Fact]
    public void CredentialsAndDataLossAreSevereAndTheOthersAreNot()
    {
        new ChangeConcern(1, ConcernKind.Credentials, "").IsSevere.Should().BeTrue();
        new ChangeConcern(1, ConcernKind.DataLoss, "").IsSevere.Should().BeTrue();
        new ChangeConcern(1, ConcernKind.Files, "").IsSevere.Should().BeFalse();
        new ChangeConcern(1, ConcernKind.Network, "").IsSevere.Should().BeFalse();
        new ChangeConcern(1, ConcernKind.Process, "").IsSevere.Should().BeFalse();
    }

    [Fact]
    public void ConcernsReachTheSectionTheyBelongTo()
    {
        var result = Parse("SUMMARY: x\nTOUCHES 2: network — calls a remote host");
        var notebook = DiffNotebook.Build(Diff(), result.ChangeNotes, result.Issues, result.Concerns);

        notebook.Should().HaveCount(2);
        notebook[0].HasConcern.Should().BeFalse();
        notebook[1].Concern!.Kind.Should().Be(ConcernKind.Network);
    }

    [Fact]
    public void AReplyWithNoTouchesLinesFlagsNothing()
    {
        // Most changes are ordinary, and the badge is worth something only if most sections
        // do not carry one.
        Parse("SUMMARY: x\nRISK: none\nCHANGE 1: renames a local").Concerns.Should().BeEmpty();
    }
}
