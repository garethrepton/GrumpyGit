using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Ai;

/// <summary>
/// A contiguous run of commits produced by one AI agent in one sitting.
///
/// This is the unit a human actually wants to review. Agents commit far more often
/// than humans do, so reviewing commit-by-commit buries the reviewer; reviewing the
/// session as a whole shows the net effect of what the agent did.
/// </summary>
public sealed class AiSession
{
    /// <summary>Commits in the session, newest first (matching git log order).</summary>
    public required IReadOnlyList<CommitNode> Commits { get; init; }

    /// <summary>Display name of the agent that produced this session.</summary>
    public required string AgentName { get; init; }

    /// <summary>The oldest commit in the session — the session's starting point.</summary>
    public CommitNode First => Commits[^1];

    /// <summary>The newest commit in the session — the session's end state.</summary>
    public CommitNode Last => Commits[0];

    public DateTimeOffset StartedAt => First.AuthorDate;

    public DateTimeOffset EndedAt => Last.AuthorDate;

    public TimeSpan Duration => EndedAt - StartedAt;

    public int CommitCount => Commits.Count;

    /// <summary>
    /// The commit to diff *against* to see the session's whole effect — the parent of
    /// the session's first commit. Null for a root commit, where there is nothing to
    /// diff against and the session's full tree is the change.
    /// </summary>
    public string? BaseHash => First.ParentHashes.Length > 0 ? First.ParentHashes[0] : null;

    public string HeadHash => Last.Hash;

    /// <summary>Short human label, e.g. "Claude Code · 7 commits · 12 Mar 14:03".</summary>
    public string DisplayName =>
        $"{AgentName} · {CommitCount} commit{(CommitCount == 1 ? "" : "s")} · {StartedAt.LocalDateTime:d MMM HH:mm}";
}

/// <summary>
/// Groups AI-authored commits into <see cref="AiSession"/>s.
/// </summary>
public static class AiSessionBuilder
{
    /// <summary>
    /// Commits by the same agent further apart than this are treated as separate
    /// sessions. Agents commit in bursts; a gap this long means the human went away
    /// and came back, which is a natural review boundary.
    /// </summary>
    public static readonly TimeSpan DefaultSessionGap = TimeSpan.FromHours(2);

    /// <summary>
    /// Builds sessions from a commit list ordered newest-first (as
    /// <c>git log</c> returns it).
    ///
    /// A session breaks when the agent changes, when the time gap exceeds
    /// <paramref name="sessionGap"/>, or when a non-AI commit interrupts the run —
    /// a human commit in the middle means the human already took the wheel there.
    /// </summary>
    public static IReadOnlyList<AiSession> Build(
        IReadOnlyList<CommitNode> commitsNewestFirst,
        TimeSpan? sessionGap = null)
    {
        ArgumentNullException.ThrowIfNull(commitsNewestFirst);
        var gap = sessionGap ?? DefaultSessionGap;

        var sessions = new List<AiSession>();
        var current = new List<CommitNode>();
        string? currentAgent = null;

        void Flush()
        {
            if (current.Count > 0 && currentAgent is not null)
                sessions.Add(new AiSession { Commits = [.. current], AgentName = currentAgent });
            current = [];
            currentAgent = null;
        }

        foreach (var commit in commitsNewestFirst)
        {
            var attribution = AiAttributionDetector.Detect(commit);

            if (!attribution.IsAi)
            {
                // A human commit ends any run in progress.
                Flush();
                continue;
            }

            if (currentAgent is not null)
            {
                var differentAgent = !string.Equals(currentAgent, attribution.AgentName, StringComparison.Ordinal);

                // List is newest-first, so the previous entry is the *later* commit.
                var elapsed = current[^1].AuthorDate - commit.AuthorDate;
                var tooFarApart = elapsed > gap;

                if (differentAgent || tooFarApart)
                    Flush();
            }

            currentAgent = attribution.AgentName;
            current.Add(commit);
        }

        Flush();
        return sessions;
    }
}
