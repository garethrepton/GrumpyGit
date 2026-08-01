using FluentAssertions;
using GrumpyGit.Core.Graph;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests.Graph;

public class GraphLayoutEngineTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static readonly DateTimeOffset AnyDate = DateTimeOffset.UtcNow;

    private static CommitNode MakeCommit(string hash, params string[] parentHashes) =>
        new(hash, parentHashes, "Author", "author@test.com", AnyDate, $"Commit {hash}", Array.Empty<string>());

    // -----------------------------------------------------------------------
    // Empty input
    // -----------------------------------------------------------------------

    [Fact]
    public void Compute_EmptyInput_ReturnsEmptyList()
    {
        var result = GraphLayoutEngine.Compute(Array.Empty<CommitNode>());

        result.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Linear history — all commits in a single lane
    // -----------------------------------------------------------------------

    /// <summary>
    /// Three commits in a straight line:
    ///   C (parents: [B])
    ///   B (parents: [A])
    ///   A (no parents)
    /// All should be assigned lane 0.
    /// </summary>
    [Fact]
    public void Compute_LinearHistory_AllCommitsInLane0()
    {
        var commits = new[]
        {
            MakeCommit("c", "b"),
            MakeCommit("b", "a"),
            MakeCommit("a")
        };

        var result = GraphLayoutEngine.Compute(commits);

        result.Should().HaveCount(3);
        result[0].Lane.Should().Be(0, because: "commit C is the first and gets lane 0");
        result[1].Lane.Should().Be(0, because: "commit B continues in lane 0 (first parent of C)");
        result[2].Lane.Should().Be(0, because: "commit A continues in lane 0 (first parent of B)");
    }

    [Fact]
    public void Compute_LinearHistory_CorrectHashes()
    {
        var commits = new[]
        {
            MakeCommit("c", "b"),
            MakeCommit("b", "a"),
            MakeCommit("a")
        };

        var result = GraphLayoutEngine.Compute(commits);

        result[0].Hash.Should().Be("c");
        result[1].Hash.Should().Be("b");
        result[2].Hash.Should().Be("a");
    }

    /// <summary>
    /// Linear history produces vertical segments on each row except the last.
    /// Row 0 (C): 1 downward vertical segment to B.
    /// Row 1 (B): 1 downward vertical segment to A.
    /// Row 2 (A): no parents, no downward segments.
    /// </summary>
    [Fact]
    public void Compute_LinearHistory_SegmentTypes()
    {
        var commits = new[]
        {
            MakeCommit("c", "b"),
            MakeCommit("b", "a"),
            MakeCommit("a")
        };

        var result = GraphLayoutEngine.Compute(commits);

        result[0].Segments.Should().ContainSingle()
            .Which.Type.Should().Be(SegmentType.Vertical);

        result[1].Segments.Should().ContainSingle()
            .Which.Type.Should().Be(SegmentType.Vertical);

        result[2].Segments.Should().BeEmpty(because: "A has no parents so no downward edges");
    }

    // -----------------------------------------------------------------------
    // Simple branch and merge
    // -----------------------------------------------------------------------

    /// <summary>
    /// Merge scenario:
    ///   C (hash: c, parents: [a, b])  — merge commit
    ///   B (hash: b, parents: [a])
    ///   A (hash: a, parents: [])
    ///
    /// Expected lane assignments:
    ///   C → lane 0  (first commit, gets free lane 0)
    ///   B → lane 1  (C's second parent, opened in a new lane)
    ///   A → lane 0  (C's first parent, inherits lane 0; B also points to A but
    ///                lane 0 is already tracking A so no duplicate lane is opened)
    /// </summary>
    [Fact]
    public void Compute_BranchAndMerge_LaneAssignments()
    {
        var commits = new[]
        {
            MakeCommit("c", "a", "b"),  // merge commit
            MakeCommit("b", "a"),
            MakeCommit("a")
        };

        var result = GraphLayoutEngine.Compute(commits);

        result[0].Hash.Should().Be("c");
        result[0].Lane.Should().Be(0, because: "merge commit C is first, gets lane 0");

        result[1].Hash.Should().Be("b");
        result[1].Lane.Should().Be(1, because: "B was opened as C's second parent in a new lane");

        result[2].Hash.Should().Be("a");
        result[2].Lane.Should().Be(0, because: "A was C's first parent and inherits lane 0");
    }

    [Fact]
    public void Compute_BranchAndMerge_MergeCommitSegmentCount()
    {
        var commits = new[]
        {
            MakeCommit("c", "a", "b"),
            MakeCommit("b", "a"),
            MakeCommit("a")
        };

        var result = GraphLayoutEngine.Compute(commits);

        // C opens lane 0 for 'a' (Vertical) and lane 1 for 'b' (BranchOut).
        result[0].Segments.Should().HaveCount(2,
            because: "merge commit C produces one vertical segment (first parent) and one branch-out (second parent)");
    }

    [Fact]
    public void Compute_BranchAndMerge_MergeCommitSegmentTypes()
    {
        var commits = new[]
        {
            MakeCommit("c", "a", "b"),
            MakeCommit("b", "a"),
            MakeCommit("a")
        };

        var result = GraphLayoutEngine.Compute(commits);

        var segments = result[0].Segments;
        segments.Should().ContainSingle(s => s.Type == SegmentType.Vertical,
            because: "the first parent (a) continues straight down in lane 0");
        segments.Should().ContainSingle(s => s.Type == SegmentType.BranchOut,
            because: "the second parent (b) opens a new branch-out lane");
    }

    [Fact]
    public void Compute_BranchAndMerge_BranchCommitContinuesBothLanesDownward()
    {
        // Layout:  row 0  C (lane 0), a merge of A and B
        //          row 1  B (lane 1), whose parent is A
        //          row 2  A (lane 0)
        var commits = new[]
        {
            MakeCommit("c", "a", "b"),
            MakeCommit("b", "a"),
            MakeCommit("a")
        };

        var result = GraphLayoutEngine.Compute(commits);

        // Two edges leave B's row heading down to A: lane 0 carries C's first-parent
        // link to A, and lane 1 carries B's own link to A. B's lane cannot simply
        // close here — if it did, B would render as a dangling branch with no edge
        // reaching its parent.
        result[1].Segments.Should().HaveCount(2,
            because: "lane 0 (C→A) and lane 1 (B→A) both continue downward from B's row");

        result[1].Segments.Should().OnlyContain(s => s.Type == SegmentType.Vertical);
        result[1].Segments.Select(s => s.FromLane).Should().BeEquivalentTo([0, 1]);
    }

    [Fact]
    public void Compute_BranchAndMerge_LeafReceivesConvergingEdge()
    {
        var commits = new[]
        {
            MakeCommit("c", "a", "b"),
            MakeCommit("b", "a"),
            MakeCommit("a")
        };

        var result = GraphLayoutEngine.Compute(commits);

        // A has no parents, so it emits nothing downward — but it is the convergence
        // point for the edge coming from B's lane, which arrives as a MergeIn.
        var segment = result[2].Segments.Should().ContainSingle(
            because: "B's lane converges onto A, and that incoming edge is drawn at A's row").Subject;

        segment.Type.Should().Be(SegmentType.MergeIn);
        segment.FromLane.Should().Be(1, "the edge arrives from B's lane");
        segment.ToLane.Should().Be(0, "A occupies lane 0");
    }

    // -----------------------------------------------------------------------
    // Orphan commit
    // -----------------------------------------------------------------------

    /// <summary>
    /// An orphan commit has no parents and is not a child of any previous commit.
    /// When it is the first (or only) commit, it should receive lane 0.
    /// </summary>
    [Fact]
    public void Compute_SingleOrphanCommit_GetsLane0()
    {
        var commits = new[] { MakeCommit("orphan") };

        var result = GraphLayoutEngine.Compute(commits);

        result.Should().ContainSingle();
        result[0].Lane.Should().Be(0);
        result[0].Segments.Should().BeEmpty(because: "orphan has no parents so no outgoing edges");
    }

    /// <summary>
    /// If an orphan commit appears after a linear chain has fully closed all lanes,
    /// it should still receive lane 0.
    /// </summary>
    [Fact]
    public void Compute_OrphanAfterClosedChain_GetsLane0()
    {
        // A normal root commit followed by an unrelated orphan.
        // Topological order allows unrelated roots to appear in any relative order.
        var commits = new[]
        {
            MakeCommit("b", "a"),
            MakeCommit("a"),            // closes lane 0
            MakeCommit("orphan")        // new unrelated root
        };

        var result = GraphLayoutEngine.Compute(commits);

        result[2].Hash.Should().Be("orphan");
        result[2].Lane.Should().Be(0,
            because: "after lane 0 is closed by 'a', the orphan should reclaim lane 0");
    }

    // -----------------------------------------------------------------------
    // Octopus merge (3+ parents)
    // -----------------------------------------------------------------------

    [Fact]
    public void Compute_OctopusMerge_OpensSeparateLaneForEachNonFirstParent()
    {
        // O merges three branches: first parent is A, then B, then C (unrelated base).
        var commits = new[]
        {
            MakeCommit("o", "a", "b", "cc"),
            MakeCommit("b", "a"),
            MakeCommit("cc", "a"),
            MakeCommit("a")
        };

        var result = GraphLayoutEngine.Compute(commits);

        // O gets lane 0; 'a' (first parent) stays in lane 0.
        result[0].Lane.Should().Be(0);

        // 'b' is the second parent of O → opened in a new lane (lane 1).
        result[1].Lane.Should().Be(1);

        // 'cc' is the third parent of O → opened in another new lane (lane 2).
        result[2].Lane.Should().Be(2);

        // 'a' is the first parent of O → inherits lane 0.
        result[3].Lane.Should().Be(0);
    }

    [Fact]
    public void Compute_OctopusMerge_CorrectSegmentCount()
    {
        var commits = new[]
        {
            MakeCommit("o", "a", "b", "cc"),
            MakeCommit("b", "a"),
            MakeCommit("cc", "a"),
            MakeCommit("a")
        };

        var result = GraphLayoutEngine.Compute(commits);

        // O opens 3 lanes (a in lane 0, b in lane 1, cc in lane 2):
        // 1 Vertical (for 'a') + 2 BranchOut (for 'b', 'cc') = 3 total.
        result[0].Segments.Should().HaveCount(3);
    }

    // -----------------------------------------------------------------------
    // Segment coordinates
    // -----------------------------------------------------------------------

    [Fact]
    public void Compute_LinearHistory_VerticalSegmentCoordinates()
    {
        var commits = new[]
        {
            MakeCommit("c", "b"),
            MakeCommit("b", "a"),
            MakeCommit("a")
        };

        var result = GraphLayoutEngine.Compute(commits);

        // Row 0 emits a segment from row 0 to row 1 in lane 0.
        var seg0 = result[0].Segments[0];
        seg0.FromRow.Should().Be(0);
        seg0.ToRow.Should().Be(1);
        seg0.FromLane.Should().Be(0);
        seg0.ToLane.Should().Be(0);

        // Row 1 emits a segment from row 1 to row 2 in lane 0.
        var seg1 = result[1].Segments[0];
        seg1.FromRow.Should().Be(1);
        seg1.ToRow.Should().Be(2);
        seg1.FromLane.Should().Be(0);
        seg1.ToLane.Should().Be(0);
    }
}
