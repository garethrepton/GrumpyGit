using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Graph;

/// <summary>
/// Implements the pvigier lane-assignment algorithm to compute a visual graph
/// layout for a list of commits supplied in topological order (children first).
/// </summary>
public static class GraphLayoutEngine
{
    /// <summary>
    /// Computes layout for a list of commits in topological order (children before parents).
    /// Returns a <see cref="GraphNode"/> for each commit with <see cref="GraphNode.Lane"/>
    /// and <see cref="GraphNode.Segments"/> populated.
    /// </summary>
    public static IReadOnlyList<GraphNode> Compute(IReadOnlyList<CommitNode> commits)
    {
        if (commits.Count == 0)
            return Array.Empty<GraphNode>();

        var nodes = new List<GraphNode>(commits.Count);

        // openLanes: each entry is (expectedHash, laneIndex, branchLabel).
        // A lane is "open" while we are still waiting for the commit with that hash
        // to appear. When a commit appears it claims the lane(s) waiting for it.
        //
        // Label propagates newest → oldest, which is the direction we walk: a branch tip
        // carries the ref, and its ancestors inherit it until something better is known.
        var openLanes = new List<(string Hash, int Lane, string? Label)>();

        for (int row = 0; row < commits.Count; row++)
        {
            var commit = commits[row];
            var node = FromCommit(commit);

            // ---------------------------------------------------------------
            // Step 1: Determine which lane this commit is assigned to.
            // ---------------------------------------------------------------
            // Find all lanes currently waiting for this commit's hash.
            var claimedLanes = openLanes
                .Where(l => l.Hash == commit.Hash)
                .OrderBy(l => l.Lane)
                .ToList();

            int assignedLane;
            if (claimedLanes.Count > 0)
            {
                // Claim the leftmost lane that was waiting for us.
                assignedLane = claimedLanes[0].Lane;
            }
            else
            {
                // Nothing was expecting this commit — it is a new head or orphan.
                // Assign the smallest free lane index.
                var usedNow = new HashSet<int>(openLanes.Select(l => l.Lane));
                assignedLane = FindFreeLane(usedNow);
            }

            node.Lane = assignedLane;

            // ---------------------------------------------------------------
            // Determine this commit's branch label. A ref on the commit itself is
            // definitive; otherwise inherit whatever the claimed lane carried down.
            // ---------------------------------------------------------------
            var ownLabel = BranchLabelResolver.FromRefNames(commit.RefNames)
                           ?? claimedLanes.Select(l => l.Label).FirstOrDefault(l => l is not null);

            node.BranchLabel = ownLabel;

            // ---------------------------------------------------------------
            // Step 2: Emit MergeIn segments for any secondary lanes that were
            // converging on this commit (all claimed lanes except the one we
            // kept). These are incoming edges from other lanes to assignedLane.
            // We record them on the current node, originating from the previous
            // row so the renderer can draw them arriving at this row.
            // ---------------------------------------------------------------
            // MergeIn: lanes other than assignedLane that were pointing at us.
            // (This covers the case where multiple prior commits had this hash
            // as a parent and each opened a lane for it.)
            foreach (var (_, mergeLane, mergeLabel) in claimedLanes.Skip(1))
            {
                node.Segments.Add(new GraphSegment(
                    FromLane: mergeLane,
                    ToLane: assignedLane,
                    FromRow: row - 1,
                    ToRow: row,
                    Type: SegmentType.MergeIn,
                    BranchLabel: mergeLabel
                ));
            }

            // ---------------------------------------------------------------
            // Step 3: Remove ALL lanes that were waiting for this commit.
            // ---------------------------------------------------------------
            openLanes.RemoveAll(l => l.Hash == commit.Hash);

            // ---------------------------------------------------------------
            // Step 4: Re-open lanes for each parent of this commit.
            //   - First parent: continues in assignedLane (straight down).
            //   - Additional parents: get a new free lane (branch-out).
            //   - Skip parents already tracked by an existing open lane.
            // ---------------------------------------------------------------
            var usedAfterRemoval = new HashSet<int>(openLanes.Select(l => l.Lane));

            // For a merge, the subject is often the only surviving record of the
            // merged branch's name once that branch has been deleted.
            var mergedBranchLabel = commit.ParentHashes.Length > 1
                ? BranchLabelResolver.FromMergeSubject(commit.Subject)
                : null;

            for (int pi = 0; pi < commit.ParentHashes.Length; pi++)
            {
                string parentHash = commit.ParentHashes[pi];

                // The first parent continues this commit's own line of development;
                // any additional parent is the branch that was merged in.
                var parentLabel = pi == 0 ? ownLabel : mergedBranchLabel;

                // If this parent is already tracked in a lane, check whether we are
                // on a different lane.  If so, register our lane as a second tracker
                // so that when the parent commit appears, claimedLanes contains both
                // lanes and a MergeIn segment is emitted to draw the converging line.
                if (openLanes.Any(l => l.Hash == parentHash))
                {
                    if (!openLanes.Any(l => l.Hash == parentHash && l.Lane == assignedLane))
                    {
                        openLanes.Add((parentHash, assignedLane, parentLabel));
                        usedAfterRemoval.Add(assignedLane);
                    }
                    continue;
                }

                int newLane;
                if (pi == 0)
                {
                    // First parent inherits the commit's lane.
                    newLane = assignedLane;
                }
                else
                {
                    // Non-first parent opens a new lane.
                    newLane = FindFreeLane(usedAfterRemoval);
                }

                openLanes.Add((parentHash, newLane, parentLabel));
                usedAfterRemoval.Add(newLane);
            }

            // ---------------------------------------------------------------
            // Step 5: Emit downward segments for all open lanes.
            // Each open lane represents an edge that will be drawn from this
            // row down to the next row.
            // ---------------------------------------------------------------
            foreach (var (hash, lane, label) in openLanes)
            {
                SegmentType segType;

                // Determine whether this segment is a BranchOut (starts at this
                // commit going to a non-first parent), Vertical (continuation),
                // or the downward leg of the first parent.
                int parentIndex = Array.IndexOf(commit.ParentHashes, hash);

                if (parentIndex >= 1)
                {
                    // This lane was just opened for a non-first parent of this commit.
                    segType = SegmentType.BranchOut;
                }
                else
                {
                    // Either the first-parent continuation or a pass-through lane.
                    segType = SegmentType.Vertical;
                }

                node.Segments.Add(new GraphSegment(
                    FromLane: lane,
                    ToLane: lane,
                    FromRow: row,
                    ToRow: row + 1,
                    Type: segType,
                    BranchLabel: label
                ));
            }

            nodes.Add(node);
        }

        return nodes;
    }

    /// <summary>
    /// Returns the smallest non-negative integer not present in <paramref name="usedLanes"/>.
    /// </summary>
    private static int FindFreeLane(HashSet<int> usedLanes)
    {
        int candidate = 0;
        while (usedLanes.Contains(candidate))
            candidate++;
        return candidate;
    }

    /// <summary>
    /// Maps a <see cref="CommitNode"/> to a <see cref="GraphNode"/>, leaving
    /// layout properties at their defaults for the engine to fill in.
    /// </summary>
    private static GraphNode FromCommit(CommitNode c) => new()
    {
        Hash = c.Hash,
        ParentHashes = c.ParentHashes,
        AuthorName = c.AuthorName,
        AuthorEmail = c.AuthorEmail,
        AuthorDate = c.AuthorDate,
        Subject = c.Subject,
        RefNames = c.RefNames
    };
}
