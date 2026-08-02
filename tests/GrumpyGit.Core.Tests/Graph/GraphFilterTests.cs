using FluentAssertions;
using GrumpyGit.Core.Graph;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests.Graph;

public class GraphFilterTests
{
    private static CommitNode Commit(string hash, string[] parents, string subject = "x", params string[] refs) =>
        new(hash, parents, "A", "a@a", DateTimeOffset.UnixEpoch, subject, refs);

    /// <summary>
    /// master:  m3 ── m2(merge) ── m1
    ///                    │
    /// feature:          f2 ── f1
    ///
    /// Topological order, children first.
    /// </summary>
    private static List<CommitNode> MergeHistory() =>
    [
        Commit("m3", ["m2"], "later work", "master"),
        Commit("m2", ["m1", "f2"], "Merge branch 'feature'"),
        Commit("f2", ["f1"], "feature tip", "feature"),
        Commit("f1", ["m1"], "feature start"),
        Commit("m1", [], "root"),
    ];

    // ── Branch mode ───────────────────────────────────────────────────────────

    [Fact]
    public void FirstParentChain_KeepsTheMergeButNotWhatWasMergedIn()
    {
        var chain = GraphFilter.FirstParentChain(MergeHistory(), "master");

        chain.Should().BeEquivalentTo(["m3", "m2", "m1"]);
        chain.Should().NotContain("f2", "commits from the merged branch are not on master's own line");
        chain.Should().NotContain("f1");
    }

    [Fact]
    public void FirstParentChain_FollowsTheFeatureBranchWhenAskedForIt()
    {
        var chain = GraphFilter.FirstParentChain(MergeHistory(), "feature");

        chain.Should().BeEquivalentTo(["f2", "f1", "m1"]);
    }

    [Fact]
    public void FirstParentChain_ResolvesABranchDecoratedViaHeadArrow()
    {
        var commits = new List<CommitNode>
        {
            Commit("c2", ["c1"], "tip", "HEAD -> develop"),
            Commit("c1", [], "root"),
        };

        GraphFilter.FirstParentChain(commits, "develop").Should().BeEquivalentTo(["c2", "c1"]);
    }

    [Fact]
    public void FirstParentChain_MatchesAcrossTheRemotePrefix()
    {
        var commits = new List<CommitNode>
        {
            Commit("c2", ["c1"], "tip", "origin/main"),
            Commit("c1", [], "root"),
        };

        GraphFilter.FirstParentChain(commits, "main").Should().BeEquivalentTo(["c2", "c1"]);
        GraphFilter.FirstParentChain(commits, "origin/main").Should().BeEquivalentTo(["c2", "c1"]);
    }

    [Fact]
    public void FirstParentChain_IgnoresTags()
    {
        var commits = new List<CommitNode>
        {
            Commit("c2", ["c1"], "tip", "tag: v1.0"),
            Commit("c1", [], "root"),
        };

        GraphFilter.FirstParentChain(commits, "v1.0").Should().BeEmpty();
    }

    [Fact]
    public void FirstParentChain_IsEmptyForAnUnknownBranch()
    {
        GraphFilter.FirstParentChain(MergeHistory(), "nope").Should().BeEmpty();
    }

    /// <summary>
    /// A corrupt or truncated history could otherwise walk forever.
    /// </summary>
    [Fact]
    public void FirstParentChain_TerminatesOnAParentCycle()
    {
        var commits = new List<CommitNode>
        {
            Commit("a", ["b"], "a", "loop"),
            Commit("b", ["a"], "b"),
        };

        GraphFilter.FirstParentChain(commits, "loop").Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public void Apply_BranchModeKeepsOnlyThatBranchesLine()
    {
        var commits = MergeHistory();
        var options = new GraphFilterOptions { BranchMode = "master" };

        var filtered = GraphFilter.Apply(commits, NoLabels(), options);

        filtered.Select(c => c.Hash).Should().Equal("m3", "m2", "m1");
    }

    [Fact]
    public void Apply_BranchModePreservesTheOriginalOrder()
    {
        var filtered = GraphFilter.Apply(
            MergeHistory(), NoLabels(), new GraphFilterOptions { BranchMode = "feature" });

        filtered.Select(c => c.Hash).Should().Equal("f2", "f1", "m1");
    }

    /// <summary>
    /// Blanking the graph reads as a bug rather than as "that branch is not here".
    /// </summary>
    [Fact]
    public void Apply_LeavesHistoryIntactWhenTheBranchCannotBeResolved()
    {
        var commits = MergeHistory();

        var filtered = GraphFilter.Apply(
            commits, NoLabels(), new GraphFilterOptions { BranchMode = "does-not-exist" });

        filtered.Should().HaveCount(commits.Count);
    }

    // ── Hidden branches ───────────────────────────────────────────────────────

    [Fact]
    public void Apply_DropsCommitsLabelledWithAHiddenBranch()
    {
        var commits = MergeHistory();
        var labels = new Dictionary<string, string?>
        {
            ["m3"] = "master",
            ["m2"] = "master",
            ["f2"] = "feature",
            ["f1"] = "feature",
            ["m1"] = "master",
        };

        var filtered = GraphFilter.Apply(commits, labels, new GraphFilterOptions
        {
            HiddenBranches = new HashSet<string>(["feature"], StringComparer.Ordinal),
        });

        filtered.Select(c => c.Hash).Should().Equal("m3", "m2", "m1");
    }

    /// <summary>
    /// An unlabelled commit has no key entry, so hiding it would make it unreachable.
    /// </summary>
    [Fact]
    public void Apply_NeverHidesCommitsWhoseBranchIsUnknown()
    {
        var commits = MergeHistory();
        var labels = new Dictionary<string, string?> { ["m3"] = null, ["m2"] = "master" };

        var filtered = GraphFilter.Apply(commits, labels, new GraphFilterOptions
        {
            HiddenBranches = new HashSet<string>(["master"], StringComparer.Ordinal),
        });

        filtered.Select(c => c.Hash).Should().Contain("m3");
        filtered.Select(c => c.Hash).Should().NotContain("m2");
    }

    [Fact]
    public void Apply_ReturnsTheInputUntouchedWhenNothingIsFiltered()
    {
        var commits = MergeHistory();

        GraphFilter.Apply(commits, NoLabels(), GraphFilterOptions.Unfiltered)
            .Should().BeSameAs(commits);
    }

    [Fact]
    public void Options_AreInactiveByDefault()
    {
        GraphFilterOptions.Unfiltered.IsActive.Should().BeFalse();
    }

    private static Dictionary<string, string?> NoLabels() => new();
}

public class BranchPaletteTests
{
    [Fact]
    public void Assign_GivesEachBranchItsOwnSlotInOrderOfAppearance()
    {
        var slots = BranchPalette.Assign(["main", "feature", "main", "hotfix"]);

        slots["main"].Should().Be(0);
        slots["feature"].Should().Be(1);
        slots["hotfix"].Should().Be(2);
    }

    [Fact]
    public void Assign_SkipsNullAndEmptyLabels()
    {
        var slots = BranchPalette.Assign([null, "", "main"]);

        slots.Should().ContainSingle();
        slots["main"].Should().Be(0);
    }

    [Fact]
    public void Assign_WrapsAroundOnceThePaletteIsExhausted()
    {
        var many = Enumerable.Range(0, BranchPalette.Size + 2).Select(i => $"b{i}").ToList();

        var slots = BranchPalette.Assign(many);

        slots["b0"].Should().Be(0);
        slots[$"b{BranchPalette.Size}"].Should().Be(0, "the palette wraps");
        slots.Should().HaveCount(BranchPalette.Size + 2);
    }
}
