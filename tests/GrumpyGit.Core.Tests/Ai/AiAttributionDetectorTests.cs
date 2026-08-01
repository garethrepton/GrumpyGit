using GrumpyGit.Core.Ai;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests.Ai;

public class AiAttributionDetectorTests
{
    private static CommitNode Commit(
        string subject = "some change",
        string authorName = "Test Developer",
        string authorEmail = "dev@example.com",
        string[]? coAuthors = null,
        DateTimeOffset? date = null) =>
        new(
            Hash: "a".PadRight(40, 'b'),
            ParentHashes: ["c".PadRight(40, 'd')],
            AuthorName: authorName,
            AuthorEmail: authorEmail,
            AuthorDate: date ?? DateTimeOffset.UnixEpoch,
            Subject: subject,
            RefNames: [])
        {
            CoAuthors = coAuthors ?? [],
        };

    [Fact]
    public void PlainHumanCommit_IsNotAi()
    {
        var result = AiAttributionDetector.Detect(Commit());

        Assert.False(result.IsAi);
        Assert.Equal(AiEvidence.None, result.Evidence);
    }

    [Fact]
    public void CoAuthorTrailer_FromRealClaudeCodeCommit_IsDetected()
    {
        // This is the exact trailer format on commit 7fb157b of this repository.
        var commit = Commit(coAuthors: ["Claude Sonnet 4.6 <noreply@anthropic.com>"]);

        var result = AiAttributionDetector.Detect(commit);

        Assert.True(result.IsAi);
        Assert.Equal("Claude", result.AgentName);
        Assert.Equal(AiEvidence.CoAuthorTrailer, result.Evidence);
    }

    [Fact]
    public void CoAuthorTrailer_TakesPrecedenceOverHumanAuthor()
    {
        // The human stays the author; the agent is the co-author. Attribution must
        // still report AI, otherwise agent commits are invisible.
        var commit = Commit(
            authorName: "Test Developer",
            authorEmail: "dev@example.com",
            coAuthors: ["Claude Code <noreply@anthropic.com>"]);

        var result = AiAttributionDetector.Detect(commit);

        Assert.Equal("Claude Code", result.AgentName);
        Assert.Equal(AiEvidence.CoAuthorTrailer, result.Evidence);
    }

    [Theory]
    [InlineData("Claude Code <noreply@anthropic.com>", "Claude Code")]
    [InlineData("Copilot <copilot@github.com>", "GitHub Copilot")]
    [InlineData("Cursor Agent <cursoragent@cursor.com>", "Cursor")]
    [InlineData("devin-ai-integration[bot] <devin@devin.ai>", "Devin")]
    [InlineData("aider <aider@aider.chat>", "Aider")]
    [InlineData("google-labs-jules[bot] <jules@google.com>", "Jules")]
    public void KnownAgents_AreIdentifiedByName(string coAuthor, string expectedAgent)
    {
        var result = AiAttributionDetector.Detect(Commit(coAuthors: [coAuthor]));

        Assert.True(result.IsAi);
        Assert.Equal(expectedAgent, result.AgentName);
    }

    [Fact]
    public void AgentAsDirectAuthor_IsDetected()
    {
        var commit = Commit(authorName: "Copilot", authorEmail: "copilot@github.com");

        var result = AiAttributionDetector.Detect(commit);

        Assert.True(result.IsAi);
        Assert.Equal("GitHub Copilot", result.AgentName);
        Assert.Equal(AiEvidence.AuthorIdentity, result.Evidence);
    }

    [Fact]
    public void HumanCoAuthor_IsNotMistakenForAi()
    {
        var commit = Commit(coAuthors: ["Jane Developer <jane@example.com>"]);

        Assert.False(AiAttributionDetector.Detect(commit).IsAi);
    }

    [Fact]
    public void Detail_CarriesTheMatchedText_SoTheUserCanSeeWhy()
    {
        var commit = Commit(coAuthors: ["Claude <noreply@anthropic.com>"]);

        var result = AiAttributionDetector.Detect(commit);

        Assert.Equal("Claude <noreply@anthropic.com>", result.Detail);
    }
}
