using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Ai;

/// <summary>
/// Decides whether a commit was authored by an AI coding agent.
///
/// Detection order matters. A <c>Co-authored-by</c> trailer is checked first because
/// it is both the most reliable signal and the most common one in practice: agents
/// keep the human as the git author (so the human owns the work) and add themselves
/// as co-author. Matching on the author identity alone would miss the overwhelming
/// majority of agent-written commits.
/// </summary>
public static class AiAttributionDetector
{
    /// <summary>
    /// Known agent signatures. Each needle is matched case-insensitively as a
    /// substring against an identity string ("Name &lt;email&gt;").
    ///
    /// Order is significant — more specific signatures must precede broader ones, so
    /// "claude code" wins over the bare "claude" fallback.
    /// </summary>
    private static readonly (string Needle, string AgentName)[] AgentSignatures =
    [
        ("claude code",             "Claude Code"),
        ("noreply@anthropic.com",   "Claude"),
        ("anthropic",               "Claude"),
        ("claude",                  "Claude"),
        ("github-copilot",          "GitHub Copilot"),
        ("copilot",                 "GitHub Copilot"),
        ("cursoragent",             "Cursor"),
        ("cursor.com",              "Cursor"),
        ("cursor.sh",               "Cursor"),
        ("devin-ai-integration",    "Devin"),
        ("devin.ai",                "Devin"),
        ("openai.com",              "OpenAI Codex"),
        ("chatgpt",                 "OpenAI Codex"),
        ("codex",                   "OpenAI Codex"),
        ("google-labs-jules",       "Jules"),
        ("gemini-code-assist",      "Gemini"),
        ("windsurf",                "Windsurf"),
        ("aider.chat",              "Aider"),
        ("aider",                   "Aider"),
    ];

    /// <summary>
    /// Markers that appear in a commit subject when a tool generated it, used only as
    /// a last resort because subjects are free text and easy to false-positive on.
    /// </summary>
    private static readonly (string Needle, string AgentName)[] SubjectMarkers =
    [
        ("generated with [claude code]", "Claude Code"),
        ("🤖 generated with",            "Claude Code"),
        ("co-authored-by: claude",       "Claude"),
    ];

    public static AiAttribution Detect(CommitNode commit)
    {
        ArgumentNullException.ThrowIfNull(commit);

        // 1. Co-authored-by trailers — the strongest and most common signal.
        foreach (var coAuthor in commit.CoAuthors)
        {
            var match = MatchAgent(coAuthor);
            if (match is not null)
                return new AiAttribution(match, AiEvidence.CoAuthorTrailer, coAuthor.Trim());
        }

        // 2. The commit identity itself is the agent (some agents author directly).
        var authorIdentity = $"{commit.AuthorName} <{commit.AuthorEmail}>";
        var authorMatch = MatchAgent(authorIdentity);
        if (authorMatch is not null)
            return new AiAttribution(authorMatch, AiEvidence.AuthorIdentity, authorIdentity);

        if (!string.IsNullOrEmpty(commit.CommitterEmail))
        {
            var committerIdentity = $"{commit.CommitterName} <{commit.CommitterEmail}>";
            var committerMatch = MatchAgent(committerIdentity);
            if (committerMatch is not null)
                return new AiAttribution(committerMatch, AiEvidence.AuthorIdentity, committerIdentity);
        }

        // 3. Subject markers — weakest, checked last.
        foreach (var (needle, agentName) in SubjectMarkers)
        {
            if (commit.Subject.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return new AiAttribution(agentName, AiEvidence.SubjectMarker, commit.Subject.Trim());
        }

        return AiAttribution.None;
    }

    private static string? MatchAgent(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return null;

        foreach (var (needle, agentName) in AgentSignatures)
        {
            if (identity.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return agentName;
        }

        return null;
    }
}
