using FluentAssertions;
using GrumpyGit.Core.LocalModel;

namespace GrumpyGit.Core.Tests.LocalModel;

/// <summary>
/// The order the AI view puts files in. This is the editorial claim the view makes — "read
/// these first" — so the tiers are asserted rather than left to a sort expression.
/// </summary>
public class ScanRankingTests
{
    private static long Score(ReviewRisk risk, int issues, int added = 0, int removed = 0) =>
        ScanRanking.Score(risk, issues, added, removed);

    [Fact]
    public void RiskOutranksEverything()
    {
        // A one-line dangerous change beats a thousand-line clean one. That is the whole
        // point of the view: churn is not risk.
        Score(ReviewRisk.Danger, 0, 1, 0)
            .Should().BeGreaterThan(Score(ReviewRisk.None, 0, 900, 900));

        Score(ReviewRisk.Caution, 0)
            .Should().BeGreaterThan(Score(ReviewRisk.None, 99, 9999, 9999));
    }

    [Fact]
    public void IssuesOutrankChurnWithinARiskTier()
    {
        Score(ReviewRisk.None, 1)
            .Should().BeGreaterThan(Score(ReviewRisk.None, 0, 5000, 5000));
    }

    [Fact]
    public void ChurnOnlyBreaksTiesAmongFilesNothingWasSaidAbout()
    {
        Score(ReviewRisk.None, 0, 200, 0)
            .Should().BeGreaterThan(Score(ReviewRisk.None, 0, 10, 0));
    }

    [Fact]
    public void AnEnormousDiffCannotClimbIntoTheTierAbove()
    {
        // The ceiling is what stops a generated file with 400,000 changed lines from
        // outscoring a file the model flagged.
        Score(ReviewRisk.None, 0, 400_000, 400_000)
            .Should().BeLessThan(Score(ReviewRisk.None, 1));

        Score(ReviewRisk.None, 5_000)
            .Should().BeLessThan(Score(ReviewRisk.Caution, 0));
    }

    [Theory]
    [InlineData(ReviewRisk.Danger, 0, true)]
    [InlineData(ReviewRisk.Caution, 0, true)]
    [InlineData(ReviewRisk.None, 1, true)]
    [InlineData(ReviewRisk.None, 0, false)]
    public void OnlyAVerdictOrAnIssueMakesARowNotable(ReviewRisk risk, int issues, bool expected)
    {
        ScanRanking.IsNotable(risk, issues).Should().Be(expected);
    }

    [Fact]
    public void TheWorstFileSortsFirst()
    {
        var files = new[]
        {
            ("clean-but-huge.cs", ReviewRisk.None, 0, 4000),
            ("flagged.cs", ReviewRisk.Danger, 2, 12),
            ("one-issue.cs", ReviewRisk.None, 1, 3),
            ("cautious.cs", ReviewRisk.Caution, 0, 40),
        };

        var order = files
            .OrderByDescending(f => ScanRanking.Score(f.Item2, f.Item3, f.Item4, 0))
            .Select(f => f.Item1);

        order.Should().Equal("flagged.cs", "cautious.cs", "one-issue.cs", "clean-but-huge.cs");
    }
}
