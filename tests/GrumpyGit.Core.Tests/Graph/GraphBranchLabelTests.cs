using FluentAssertions;
using GrumpyGit.Core.Graph;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests.Graph;

/// <summary>
/// Branch identity propagation through the layout engine — this is what the commit
/// graph's hover tooltip reports, so a wrong label here is a wrong claim to the user.
/// </summary>
public class GraphBranchLabelTests
{
    private static CommitNode Commit(
        string hash, string[] parents, string subject = "work", params string[] refNames) =>
        new(
            Hash: hash,
            ParentHashes: parents,
            AuthorName: "Dev",
            AuthorEmail: "dev@example.com",
            AuthorDate: DateTimeOffset.UnixEpoch,
            Subject: subject,
            RefNames: refNames);

    [Fact]
    public void BranchTipRef_PropagatesDownToItsAncestors()
    {
        // c (tip of 'main') → b → a, all on the same line of development.
        var commits = new[]
        {
            Commit("c", ["b"], "third", "HEAD -> main"),
            Commit("b", ["a"], "second"),
            Commit("a", [], "first"),
        };

        var result = GraphLayoutEngine.Compute(commits);

        result[0].BranchLabel.Should().Be("main");
        result[1].BranchLabel.Should().Be("main", "ancestors inherit the tip's branch");
        result[2].BranchLabel.Should().Be("main");
    }

    [Fact]
    public void SegmentsCarryTheBranchLabel_SoHoverCanReportIt()
    {
        var commits = new[]
        {
            Commit("c", ["b"], "third", "HEAD -> main"),
            Commit("b", ["a"], "second"),
            Commit("a", [], "first"),
        };

        var result = GraphLayoutEngine.Compute(commits);

        result[0].Segments.Should().NotBeEmpty();
        result[0].Segments.Should().OnlyContain(s => s.BranchLabel == "main");
    }

    [Fact]
    public void MergedBranch_IsIdentifiedFromTheMergeSubject_EvenAfterDeletion()
    {
        // 'feature' has been deleted, so no ref survives — the merge subject is the
        // only remaining record of where the merged lane came from.
        var commits = new[]
        {
            Commit("m", ["a", "f"], "Merge branch 'feature'", "HEAD -> main"),
            Commit("f", ["a"], "feature work"),
            Commit("a", [], "base"),
        };

        var result = GraphLayoutEngine.Compute(commits);

        // The merge commit opens a second lane for the merged parent 'f'.
        var mergedLaneSegment = result[0].Segments
            .Should().ContainSingle(s => s.Type == SegmentType.BranchOut).Subject;

        mergedLaneSegment.BranchLabel.Should().Be("feature");
    }

    [Fact]
    public void FirstParentLane_KeepsTheMergeCommitsOwnBranch_NotTheMergedOne()
    {
        var commits = new[]
        {
            Commit("m", ["a", "f"], "Merge branch 'feature'", "HEAD -> main"),
            Commit("f", ["a"], "feature work"),
            Commit("a", [], "base"),
        };

        var result = GraphLayoutEngine.Compute(commits);

        var firstParentSegment = result[0].Segments
            .Should().ContainSingle(s => s.Type == SegmentType.Vertical).Subject;

        firstParentSegment.BranchLabel.Should().Be("main",
            "the first-parent line continues main, not the branch being merged in");
    }

    [Fact]
    public void UnknowableBranch_IsLeftNull_RatherThanGuessed()
    {
        // No refs anywhere and no merge record — the branch genuinely cannot be known.
        var commits = new[]
        {
            Commit("b", ["a"], "second"),
            Commit("a", [], "first"),
        };

        var result = GraphLayoutEngine.Compute(commits);

        result[0].BranchLabel.Should().BeNull();
        result[0].Segments.Should().OnlyContain(s => s.BranchLabel == null);
    }

    [Fact]
    public void TwoIndependentBranches_KeepSeparateLabels()
    {
        // Two tips, each with its own ref, sharing a common ancestor.
        var commits = new[]
        {
            Commit("x", ["a"], "feature work", "feature"),
            Commit("m", ["a"], "main work", "HEAD -> main"),
            Commit("a", [], "base"),
        };

        var result = GraphLayoutEngine.Compute(commits);

        result[0].BranchLabel.Should().Be("feature");
        result[1].BranchLabel.Should().Be("main");
    }

    [Fact]
    public void TagOnlyDecoration_DoesNotBecomeABranchLabel()
    {
        var commits = new[]
        {
            Commit("b", ["a"], "release", "tag: v1.0"),
            Commit("a", [], "first"),
        };

        var result = GraphLayoutEngine.Compute(commits);

        result[0].BranchLabel.Should().BeNull("a tag is not a line of development");
    }
}
