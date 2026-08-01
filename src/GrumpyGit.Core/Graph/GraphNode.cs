namespace GrumpyGit.Core.Graph;

public class GraphNode
{
    public required string Hash { get; init; }
    public required string[] ParentHashes { get; init; }
    public required string AuthorName { get; init; }
    public required string AuthorEmail { get; init; }
    public required DateTimeOffset AuthorDate { get; init; }
    public required string Subject { get; init; }
    public required string[] RefNames { get; init; }

    // Layout properties assigned by GraphLayoutEngine
    public int Lane { get; set; }          // column index (0-based)
    public List<GraphSegment> Segments { get; } = new(); // lines to draw

    /// <summary>
    /// Best-known branch this commit's lane belongs to, or null if undeterminable.
    /// Inferred — see <see cref="BranchLabelResolver"/>.
    /// </summary>
    public string? BranchLabel { get; set; }
}
