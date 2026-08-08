using FluentAssertions;
using GrumpyGit.Core.LocalModel;

namespace GrumpyGit.Core.Tests.LocalModel;

/// <summary>
/// The changeset orientation pass: what goes into the prompt when a commit is bigger than
/// the context, and what comes back out when the model names a file that is not there.
/// </summary>
public class ChangeSetReviewTests
{
    private static ChangeSetFile File(string path, int added, int removed = 0, string? known = null) =>
        new(path, added, removed, [], known);

    // ── Prompt ────────────────────────────────────────────────────────────────

    [Fact]
    public void TheTitleAndTotalsLeadThePrompt()
    {
        var prompt = ChangeSetReviewPrompt.Build("Add rate limiting",
            [File("a.cs", 10, 2), File("b.cs", 5, 1)]);

        prompt.User.Should().Contain("Add rate limiting");
        prompt.User.Should().Contain("2 file(s), +15 −3");
    }

    [Fact]
    public void FilesAreListedLargestFirst()
    {
        var prompt = ChangeSetReviewPrompt.Build("x",
            [File("small.cs", 1), File("huge.cs", 500), File("medium.cs", 50)]);

        var huge = prompt.User.IndexOf("huge.cs", StringComparison.Ordinal);
        var medium = prompt.User.IndexOf("medium.cs", StringComparison.Ordinal);
        var small = prompt.User.IndexOf("small.cs", StringComparison.Ordinal);

        huge.Should().BeLessThan(medium);
        medium.Should().BeLessThan(small);
    }

    [Fact]
    public void AnOversizedChangeIsCutToTheBiggestFilesAndSaysHowMany()
    {
        var files = Enumerable.Range(0, ChangeSetReviewPrompt.MaxFilesListed + 12)
            .Select(i => File($"file{i}.cs", i))
            .ToList();

        var prompt = ChangeSetReviewPrompt.Build("big", files);

        prompt.User.Should().Contain("12 smaller file(s) not listed");
        prompt.User.Should().NotContain("file0.cs", "the smallest are the ones dropped");
    }

    [Fact]
    public void ACachedPerFileReadingIsHandedToTheChangesetPass()
    {
        var prompt = ChangeSetReviewPrompt.Build("x",
            [File("a.cs", 10, 2, known: "Removes the null guard on the request path.")]);

        prompt.User.Should().Contain("already reviewed: Removes the null guard");
    }

    [Fact]
    public void TheInstructionForbidsGuessingAtCodeItHasNotSeen()
    {
        var prompt = ChangeSetReviewPrompt.Build("x", [File("a.cs", 1)]);

        prompt.System.Should().Contain("you have not been shown it");
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    [Fact]
    public void AWellFormedReplyIsReadWhole()
    {
        var files = new[] { File("Auth.cs", 40), File("README.md", 2) };
        var reply = """
            SUMMARY: Replaces the session store and touches authentication.
            RISK: caution
            WATCH Auth.cs: most of the churn, and it is the login path
            """;

        var result = ChangeSetReviewPrompt.Parse(reply, files);

        result.Summary.Should().Be("Replaces the session store and touches authentication.");
        result.Risk.Should().Be(ReviewRisk.Caution);
        result.Watch.Should().ContainSingle();
        result.Watch[0].Path.Should().Be("Auth.cs");
    }

    [Fact]
    public void AWatchLineForAFileNotInTheChangeIsDropped()
    {
        // Otherwise the panel sends the reader looking for a file that is not there.
        var result = ChangeSetReviewPrompt.Parse(
            "WATCH Imaginary.cs: this looks risky", [File("Real.cs", 1)]);

        result.Watch.Should().BeEmpty();
    }

    [Fact]
    public void AtMostThreeWatchLinesSurvive()
    {
        var files = Enumerable.Range(0, 8).Select(i => File($"f{i}.cs", 1)).ToArray();
        var reply = string.Join('\n', Enumerable.Range(0, 8).Select(i => $"WATCH f{i}.cs: reason {i}"));

        var result = ChangeSetReviewPrompt.Parse(reply, files);

        result.Watch.Should().HaveCount(3);
    }

    [Fact]
    public void ProseAroundTheReplyIsIgnored()
    {
        var reply = "Certainly! Here is the overview:\n\nSUMMARY: A refactor.\nRISK: none\n\nHope that helps.";

        var result = ChangeSetReviewPrompt.Parse(reply, [File("a.cs", 1)]);

        result.Summary.Should().Be("A refactor.");
        result.Risk.Should().Be(ReviewRisk.None);
    }

    [Fact]
    public void AnEmptyReplyIsAnEmptyResult()
    {
        ChangeSetReviewPrompt.Parse("", [File("a.cs", 1)]).Should().Be(ChangeSetReviewResult.Empty);
    }

    [Fact]
    public void PathMatchingIgnoresCase()
    {
        var result = ChangeSetReviewPrompt.Parse(
            "WATCH auth.cs: the login path", [File("Auth.cs", 40)]);

        result.Watch.Should().ContainSingle();
    }
}
