namespace GrumpyGit.Core.Ai;

/// <summary>Where the evidence for an AI attribution came from.</summary>
public enum AiEvidence
{
    /// <summary>No AI involvement detected.</summary>
    None = 0,

    /// <summary>A <c>Co-authored-by</c> trailer named a known agent. Strongest signal.</summary>
    CoAuthorTrailer,

    /// <summary>The commit author/committer identity itself is a known agent.</summary>
    AuthorIdentity,

    /// <summary>The subject line carried a known generated-by marker.</summary>
    SubjectMarker,
}

/// <summary>
/// The result of asking "was this commit written by an AI agent, and which one?"
/// </summary>
/// <param name="AgentName">Display name of the agent, e.g. "Claude Code".</param>
/// <param name="Evidence">What the detection was based on.</param>
/// <param name="Detail">The raw matched text, for showing the user why we think so.</param>
public sealed record AiAttribution(string AgentName, AiEvidence Evidence, string Detail)
{
    public static readonly AiAttribution None = new(string.Empty, AiEvidence.None, string.Empty);

    public bool IsAi => Evidence != AiEvidence.None;
}
