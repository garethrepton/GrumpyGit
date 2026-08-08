using FluentAssertions;
using GrumpyGit.Core.LocalModel;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests.LocalModel;

/// <summary>
/// The parser is the seam where a small model's sloppiness meets the UI, so most of these
/// are about misbehaviour: decorated labels, invented line numbers, hunk numbers that do
/// not exist, prose wrapped around the answer, and the same label twice.
/// </summary>
public class DiffReviewParserTests
{
    /// <summary>
    /// A two-hunk diff whose added lines are new-file lines 41 and 88, rendered at
    /// document lines 4 and 9. Those two mappings are what anchoring is tested against.
    /// </summary>
    private static ParsedDiff SampleDiff()
    {
        var first = new DiffHunk
        {
            Index = 0,
            HeaderLine = "@@ -40,2 +40,2 @@ void Guard()",
            RenderedLineNumber = 3,
            Lines =
            [
                new DiffLine { Type = DiffLineType.Added, Content = "if (x >= 0)", NewLineNumber = 41, RenderedLineNumber = 4 },
            ],
        };

        var second = new DiffHunk
        {
            Index = 1,
            HeaderLine = "@@ -87,2 +87,2 @@ void Close()",
            RenderedLineNumber = 8,
            Lines =
            [
                new DiffLine { Type = DiffLineType.Added, Content = "stream.Flush();", NewLineNumber = 88, RenderedLineNumber = 9 },
            ],
        };

        return new ParsedDiff("old", "new", [], [], [], hunks: [first, second]);
    }

    [Fact]
    public void AWellFormedReplyIsReadWhole()
    {
        var reply = """
            SUMMARY: Widens the bounds check and flushes the stream.
            RISK: caution
            ISSUE 41: the guard now admits zero, which the caller does not expect
            HUNK 1: relaxes the lower bound
            HUNK 2: flushes before closing
            """;

        var result = DiffReviewParser.Parse(reply, SampleDiff());

        result.Summary.Should().Be("Widens the bounds check and flushes the stream.");
        result.Risk.Should().Be(ReviewRisk.Caution);
        result.Issues.Should().ContainSingle();
        result.Issues[0].SourceLine.Should().Be(41);
        result.Issues[0].RenderedLine.Should().Be(4);
        result.Issues[0].IsAnchored.Should().BeTrue();
        result.ChangeNotes.Should().HaveCount(2);
        // Anchored to the first changed line of the block, not to the @@ header above it:
        // the note describes the change, so it is drawn against the change.
        result.ChangeNotes[0].RenderedLine.Should().Be(4);
        result.ChangeNotes[1].RenderedLine.Should().Be(9);
    }

    [Theory]
    [InlineData("RISK: danger", ReviewRisk.Danger)]
    [InlineData("RISK: Danger.", ReviewRisk.Danger)]
    [InlineData("RISK: caution — the guard moved", ReviewRisk.Caution)]
    [InlineData("RISK: none", ReviewRisk.None)]
    [InlineData("risk: DANGER", ReviewRisk.Danger)]
    public void RiskSurvivesTheModelsPhrasing(string line, ReviewRisk expected)
    {
        var result = DiffReviewParser.Parse($"SUMMARY: x\n{line}", SampleDiff());

        result.Risk.Should().Be(expected);
    }

    [Fact]
    public void AnUnrecognisedRiskLeavesThePreviousVerdictAlone()
    {
        var result = DiffReviewParser.Parse("SUMMARY: x\nRISK: medium-ish", SampleDiff());

        result.Risk.Should().Be(ReviewRisk.None);
    }

    [Fact]
    public void DecoratedLabelsAreStillRecognised()
    {
        var reply = "- **SUMMARY:** Adds a flush.\n* RISK: none\n# HUNK 1: flushes";

        var result = DiffReviewParser.Parse(reply, SampleDiff());

        result.Summary.Should().Be("Adds a flush.");
        result.ChangeNotes.Should().ContainSingle();
    }

    [Fact]
    public void ProseAroundTheAnswerIsIgnored()
    {
        var reply = """
            Sure! Here is my review of the diff you sent.

            SUMMARY: Adds a flush before close.
            RISK: none

            Let me know if you would like more detail.
            """;

        var result = DiffReviewParser.Parse(reply, SampleDiff());

        result.Summary.Should().Be("Adds a flush before close.");
    }

    [Fact]
    public void AnInventedLineNumberKeepsTheTextButAnchorsNowhere()
    {
        // 999 is not a line the diff ever showed. Pointing the warning highlight at some
        // other line would be worse than not pointing it anywhere.
        var result = DiffReviewParser.Parse("ISSUE 999: something is wrong here", SampleDiff());

        result.Issues.Should().ContainSingle();
        result.Issues[0].SourceLine.Should().Be(999);
        result.Issues[0].RenderedLine.Should().Be(0);
        result.Issues[0].IsAnchored.Should().BeFalse();
    }

    [Fact]
    public void AChangeNumberThatDoesNotExistIsDropped()
    {
        var result = DiffReviewParser.Parse("HUNK 7: describes a hunk that was never shown", SampleDiff());

        result.ChangeNotes.Should().BeEmpty();
    }

    [Fact]
    public void ARepeatedHunkLabelKeepsTheFirstAnswer()
    {
        var reply = "HUNK 1: relaxes the lower bound\nHUNK 1: relaxes the lower bound again";

        var result = DiffReviewParser.Parse(reply, SampleDiff());

        result.ChangeNotes.Should().ContainSingle();
        result.ChangeNotes[0].Text.Should().Be("relaxes the lower bound");
    }

    [Fact]
    public void ChangeNotesComeBackInHunkOrder()
    {
        var result = DiffReviewParser.Parse("HUNK 2: second\nHUNK 1: first", SampleDiff());

        result.ChangeNotes.Select(n => n.ChangeNumber).Should().Equal(1, 2);
    }

    [Fact]
    public void ARunawayModelCannotFillThePanel()
    {
        var reply = string.Join('\n', Enumerable.Range(0, 200).Select(i => $"ISSUE 41: problem {i}"));

        var result = DiffReviewParser.Parse(reply, SampleDiff());

        result.Issues.Should().HaveCount(20);
    }

    [Fact]
    public void LabelsWithNoTextAreDropped()
    {
        var result = DiffReviewParser.Parse("ISSUE 41:\nHUNK 1:", SampleDiff());

        result.Issues.Should().BeEmpty();
        result.ChangeNotes.Should().BeEmpty();
    }

    [Fact]
    public void AMultiLineSummaryIsJoined()
    {
        var result = DiffReviewParser.Parse("SUMMARY: First part.\nSUMMARY: Second part.", SampleDiff());

        result.Summary.Should().Be("First part. Second part.");
    }

    [Fact]
    public void AnEmptyReplyIsAnEmptyResult()
    {
        DiffReviewParser.Parse("", SampleDiff()).Should().Be(DiffReviewResult.Empty);
        DiffReviewParser.Parse("   \n  ", SampleDiff()).Should().Be(DiffReviewResult.Empty);
    }

    [Fact]
    public void ANonNumericLineReferenceIsDropped()
    {
        var result = DiffReviewParser.Parse("ISSUE somewhere: it is wrong", SampleDiff());

        result.Issues.Should().BeEmpty();
    }
}
