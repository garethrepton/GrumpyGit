namespace GrumpyGit.Core.Graph;

/// <param name="FromLane">Source column.</param>
/// <param name="ToLane">Destination column.</param>
/// <param name="FromRow">Row index of the source commit.</param>
/// <param name="ToRow">Row index of the destination commit.</param>
/// <param name="Type">How the line is drawn.</param>
/// <param name="BranchLabel">
/// Best-known branch this line belongs to, or null when it cannot be determined.
/// See <see cref="BranchLabelResolver"/> — git does not record the branch a commit was
/// made on, so this is inferred and may legitimately be unknown.
/// </param>
public record GraphSegment(
    int FromLane,
    int ToLane,
    int FromRow,
    int ToRow,
    SegmentType Type,
    string? BranchLabel = null
);

public enum SegmentType
{
    Vertical,       // straight line within same lane
    MergeIn,        // line coming into a merge commit (child to parent)
    BranchOut       // line going from a commit to its continuation in another lane
}
