namespace GrumpyGit.Core.Models;

public record CommitNode(
    string Hash,
    string[] ParentHashes,
    string AuthorName,
    string AuthorEmail,
    DateTimeOffset AuthorDate,
    string Subject,
    string[] RefNames)
{
    /// <summary>
    /// Values of any <c>Co-authored-by</c> trailers on the commit, e.g.
    /// <c>"Claude &lt;noreply@anthropic.com&gt;"</c>.
    ///
    /// This is the primary signal for AI attribution: coding agents overwhelmingly
    /// preserve the human as the commit author and add themselves as a co-author,
    /// so author name/email alone misses almost every agent-written commit.
    /// </summary>
    public string[] CoAuthors { get; init; } = [];

    /// <summary>The committer, which differs from the author on rebases and amends.</summary>
    public string CommitterName { get; init; } = string.Empty;

    public string CommitterEmail { get; init; } = string.Empty;
}
