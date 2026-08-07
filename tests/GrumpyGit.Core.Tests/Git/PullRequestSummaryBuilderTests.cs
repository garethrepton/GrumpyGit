using FluentAssertions;
using GrumpyGit.Core.Git;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests.Git;

public class PullRequestSummaryBuilderTests
{
    private static PullRequestPreview Preview(
        MergePreview? merge = null,
        params CommitNode[] commits) => new()
    {
        SourceBranch = "feature/widget",
        TargetBranch = "develop",
        MergeBaseHash = "1234567890abcdef",
        HeadHash = "fedcba0987654321",
        Commits = commits,
        Files = [],
        Stats = new Dictionary<string, (int Added, int Removed)>(StringComparer.Ordinal),
        Merge = merge ?? new MergePreview(MergeOutcome.Clean, []),
    };

    private static CommitNode Commit(string hash, string subject) =>
        new(hash, [], "Some Author", "author@example.com", DateTimeOffset.UnixEpoch, subject, []);

    [Fact]
    public void HeaderNamesBothBranches()
    {
        var summary = PullRequestSummaryBuilder.Build(Preview(), []);

        summary.Should().StartWith("# feature/widget → develop");
    }

    [Fact]
    public void CleanMergeIsStated()
    {
        var summary = PullRequestSummaryBuilder.Build(Preview(), []);

        summary.Should().Contain("**Merges cleanly.**");
    }

    [Fact]
    public void ConflictsAreListedNotJustCounted()
    {
        var merge = new MergePreview(MergeOutcome.Conflicts, ["src/a.cs", "src/b.cs"]);

        var summary = PullRequestSummaryBuilder.Build(Preview(merge), []);

        summary.Should().Contain("2 conflicting files");
        summary.Should().Contain("`src/a.cs`");
        summary.Should().Contain("`src/b.cs`");
    }

    [Fact]
    public void UnknownMergeOutcomeIsNotPresentedAsClean()
    {
        var summary = PullRequestSummaryBuilder.Build(Preview(MergePreview.Unknown), []);

        summary.Should().Contain("Merge check unavailable");
        summary.Should().NotContain("Merges cleanly");
    }

    [Fact]
    public void ReviewedFilesAreTickedAndUnreviewedAreNot()
    {
        var files = new List<ReviewedFile>
        {
            new("src/done.cs", 10, 2, IsReviewed: true, Note: ""),
            new("src/todo.cs", 3, 0, IsReviewed: false, Note: ""),
        };

        var summary = PullRequestSummaryBuilder.Build(Preview(), files);

        summary.Should().Contain("- [x] `src/done.cs` (+10 −2)");
        summary.Should().Contain("- [ ] `src/todo.cs` (+3 −0)");
        summary.Should().Contain("**Reviewed:** 1/2 files");
    }

    /// <summary>
    /// A note that broke out of its list item would silently reformat everything below
    /// it once pasted into a pull request.
    /// </summary>
    [Fact]
    public void MultiLineNotesStayInsideTheirListItem()
    {
        var files = new List<ReviewedFile>
        {
            new("src/a.cs", 1, 1, IsReviewed: false, Note: "First line\nSecond line"),
        };

        var summary = PullRequestSummaryBuilder.Build(Preview(), files);

        summary.Should().Contain("  > First line");
        summary.Should().Contain("  > Second line");
    }

    [Fact]
    public void CommitsAreListedByShortHashAndSubject()
    {
        var summary = PullRequestSummaryBuilder.Build(
            Preview(null, Commit("abcdef1234567890", "Add the widget")), []);

        summary.Should().Contain("- `abcdef1` Add the widget");
    }

    /// <summary>
    /// Commandment 9: the summary is written to be pasted somewhere else, so it must not
    /// carry contributors' names or email addresses out of the repository with it.
    /// </summary>
    [Fact]
    public void AuthorIdentityIsNeverIncluded()
    {
        var summary = PullRequestSummaryBuilder.Build(
            Preview(null, Commit("abcdef1234567890", "Add the widget")), []);

        summary.Should().NotContain("Some Author");
        summary.Should().NotContain("author@example.com");
    }
}
