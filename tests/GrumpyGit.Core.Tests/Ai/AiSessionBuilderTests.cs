using GrumpyGit.Core.Ai;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests.Ai;

public class AiSessionBuilderTests
{
    private static readonly DateTimeOffset T0 =
        new(2026, 3, 21, 12, 0, 0, TimeSpan.Zero);

    private static CommitNode Commit(
        string hash,
        DateTimeOffset date,
        string? agentCoAuthor = null,
        string? parent = null) =>
        new(
            Hash: hash,
            ParentHashes: parent is null ? [] : [parent],
            AuthorName: "Test Developer",
            AuthorEmail: "dev@example.com",
            AuthorDate: date,
            Subject: $"change {hash}",
            RefNames: [])
        {
            CoAuthors = agentCoAuthor is null ? [] : [agentCoAuthor],
        };

    private const string ClaudeTrailer = "Claude Code <noreply@anthropic.com>";
    private const string CopilotTrailer = "Copilot <copilot@github.com>";

    [Fact]
    public void NoCommits_ProducesNoSessions()
    {
        Assert.Empty(AiSessionBuilder.Build([]));
    }

    [Fact]
    public void OnlyHumanCommits_ProduceNoSessions()
    {
        var commits = new[]
        {
            Commit("c2", T0),
            Commit("c1", T0.AddMinutes(-5)),
        };

        Assert.Empty(AiSessionBuilder.Build(commits));
    }

    [Fact]
    public void ConsecutiveAgentCommits_CollapseIntoOneSession()
    {
        // Newest first, as git log emits.
        var commits = new[]
        {
            Commit("c3", T0,                  ClaudeTrailer, parent: "c2"),
            Commit("c2", T0.AddMinutes(-10),  ClaudeTrailer, parent: "c1"),
            Commit("c1", T0.AddMinutes(-20),  ClaudeTrailer, parent: "base"),
        };

        var sessions = AiSessionBuilder.Build(commits);

        var session = Assert.Single(sessions);
        Assert.Equal(3, session.CommitCount);
        Assert.Equal("Claude Code", session.AgentName);
        Assert.Equal("c1", session.First.Hash);
        Assert.Equal("c3", session.Last.Hash);
    }

    [Fact]
    public void SessionDiffRange_SpansParentOfFirstCommitToHead()
    {
        var commits = new[]
        {
            Commit("c2", T0,                 ClaudeTrailer, parent: "c1"),
            Commit("c1", T0.AddMinutes(-10), ClaudeTrailer, parent: "base"),
        };

        var session = Assert.Single(AiSessionBuilder.Build(commits));

        // Reviewing the whole session means diffing base..c2, not c1..c2.
        Assert.Equal("base", session.BaseHash);
        Assert.Equal("c2", session.HeadHash);
    }

    [Fact]
    public void RootCommitSession_HasNoBaseToDiffAgainst()
    {
        var commits = new[] { Commit("c1", T0, ClaudeTrailer, parent: null) };

        var session = Assert.Single(AiSessionBuilder.Build(commits));

        Assert.Null(session.BaseHash);
    }

    [Fact]
    public void HumanCommitInTheMiddle_SplitsTheSession()
    {
        var commits = new[]
        {
            Commit("c4", T0,                 ClaudeTrailer, parent: "c3"),
            Commit("c3", T0.AddMinutes(-5)),                       // human
            Commit("c2", T0.AddMinutes(-10), ClaudeTrailer, parent: "c1"),
            Commit("c1", T0.AddMinutes(-15), ClaudeTrailer, parent: "base"),
        };

        var sessions = AiSessionBuilder.Build(commits);

        Assert.Equal(2, sessions.Count);
        Assert.Equal(1, sessions[0].CommitCount);
        Assert.Equal(2, sessions[1].CommitCount);
    }

    [Fact]
    public void DifferentAgents_DoNotShareASession()
    {
        var commits = new[]
        {
            Commit("c2", T0,                 CopilotTrailer, parent: "c1"),
            Commit("c1", T0.AddMinutes(-10), ClaudeTrailer,  parent: "base"),
        };

        var sessions = AiSessionBuilder.Build(commits);

        Assert.Equal(2, sessions.Count);
        Assert.Equal("GitHub Copilot", sessions[0].AgentName);
        Assert.Equal("Claude Code", sessions[1].AgentName);
    }

    [Fact]
    public void GapLongerThanThreshold_StartsANewSession()
    {
        var commits = new[]
        {
            Commit("c2", T0,                ClaudeTrailer, parent: "c1"),
            Commit("c1", T0.AddHours(-5),   ClaudeTrailer, parent: "base"),
        };

        var sessions = AiSessionBuilder.Build(commits, sessionGap: TimeSpan.FromHours(2));

        Assert.Equal(2, sessions.Count);
    }

    [Fact]
    public void GapWithinThreshold_StaysOneSession()
    {
        var commits = new[]
        {
            Commit("c2", T0,                    ClaudeTrailer, parent: "c1"),
            Commit("c1", T0.AddMinutes(-90),    ClaudeTrailer, parent: "base"),
        };

        var sessions = AiSessionBuilder.Build(commits, sessionGap: TimeSpan.FromHours(2));

        Assert.Single(sessions);
    }

    [Fact]
    public void SessionTimings_ReadOldestToNewest()
    {
        var commits = new[]
        {
            Commit("c2", T0,                 ClaudeTrailer, parent: "c1"),
            Commit("c1", T0.AddMinutes(-30), ClaudeTrailer, parent: "base"),
        };

        var session = Assert.Single(AiSessionBuilder.Build(commits));

        Assert.Equal(T0.AddMinutes(-30), session.StartedAt);
        Assert.Equal(T0, session.EndedAt);
        Assert.Equal(TimeSpan.FromMinutes(30), session.Duration);
    }
}
