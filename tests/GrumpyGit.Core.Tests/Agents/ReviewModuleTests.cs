using FluentAssertions;
using GrumpyGit.Core.Agents;

namespace GrumpyGit.Core.Tests.Agents;

/// <summary>
/// The module catalogue, and the argument lists the two CLI modules build.
///
/// The arguments are worth pinning precisely because they are invisible: every one of them
/// switches off something an agent would otherwise do — read a project's instruction files,
/// load someone's MCP servers, keep a transcript of the user's source. A flag quietly
/// dropped in a refactor would not fail anything else.
/// </summary>
public class ReviewModuleTests
{
    private static readonly ModelPrompt Prompt = new("Review this diff.", "@@ -1 +1 @@\n-a\n+b");

    [Theory]
    [InlineData(ReviewModuleId.Local)]
    [InlineData(ReviewModuleId.Copilot)]
    [InlineData(ReviewModuleId.ClaudeCode)]
    public void AChosenModuleSurvivesBeingWrittenDownAndReadBack(ReviewModuleId id)
    {
        ReviewModuleCatalogue.Parse(id.ToString()).Should().Be(id);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Gemini")]
    [InlineData("None")]
    public void AnUnknownModuleNameTurnsTheFeatureOffRatherThanFailing(string? name)
    {
        // A settings file written by a newer build, or edited by hand. The right answer is
        // no reviews, not a client that will not start.
        ReviewModuleCatalogue.Parse(name).Should().Be(ReviewModuleId.None);
    }

    [Fact]
    public void EveryModuleSaysWhetherTheDiffLeavesTheMachine()
    {
        // The one thing no module may be silent about. Local is the only false.
        ReviewModuleCatalogue.Local.SendsCodeOffMachine.Should().BeFalse();
        ReviewModuleCatalogue.Copilot.SendsCodeOffMachine.Should().BeTrue();
        ReviewModuleCatalogue.ClaudeCode.SendsCodeOffMachine.Should().BeTrue();

        ReviewModuleCatalogue.All.Should().AllSatisfy(m =>
            m.PrivacyLine.Should().NotBeNullOrWhiteSpace());
    }

    [Fact]
    public void CopilotSendsTheWholePromptAsASingleArgument()
    {
        // Not split, not concatenated into a command line: one element, however much
        // punctuation the diff carries.
        var args = CopilotCliAgent.Arguments(Prompt);

        args.Should().ContainInOrder("--prompt", $"{Prompt.System}\n\n{Prompt.User}");
    }

    [Fact]
    public void CopilotDeniesEveryToolAndNeverWaitsForAnAnswer()
    {
        var args = CopilotCliAgent.Arguments(Prompt);

        // Per kind, because the CLI rejects a wildcard rule at startup rather than treating
        // it as "everything" — a denial it will not parse is no denial at all.
        args.Should().ContainInOrder("--deny-tool", "shell");
        args.Should().ContainInOrder("--deny-tool", "write");
        args.Should().ContainInOrder("--deny-tool", "url");
        args.Should().NotContain("*");

        args.Should().Contain("--disable-builtin-mcps");
        args.Should().Contain("--no-custom-instructions");
        args.Should().Contain("--no-ask-user");

        // Stats on stdout would reach the review parser as if the model had written them.
        args.Should().Contain("--silent");

        // Nothing may widen the deny: allow-all would be the one flag that undoes it.
        args.Should().NotContain("--allow-all");
        args.Should().NotContain("--allow-all-tools");
        args.Should().NotContain("--yolo");
    }

    [Fact]
    public void ClaudeKeepsTheDiffOffTheCommandLineEntirely()
    {
        var args = ClaudeCodeAgent.Arguments(Prompt);

        args.Should().NotContain(Prompt.User);
        args.Should().Contain("--print");
        args.Should().ContainInOrder("--output-format", "text");
    }

    [Fact]
    public void ClaudeWritesNoTranscriptAndReadsNobodysProjectConfig()
    {
        // The transcript would put the user's source in a session file on disk, written by a
        // process this application started (commandment 9). The rest stop a repository — or
        // someone's own MCP setup — becoming an input to the review.
        var args = ClaudeCodeAgent.Arguments(Prompt);

        args.Should().Contain("--no-session-persistence");
        args.Should().Contain("--strict-mcp-config");
        args.Should().Contain("--disable-slash-commands");
        args.Should().ContainInOrder("--system-prompt", Prompt.System);

        args.Should().NotContain("--dangerously-skip-permissions");
        args.Should().NotContain("--allow-dangerously-skip-permissions");
    }

    [Fact]
    public void TheFactoryBuildsExactlyOneAgentPerModuleAndNoneForNone()
    {
        var work = Path.Combine(Path.GetTempPath(), "grumpy-agent-factory-test");

        ReviewAgentFactory.Create(ReviewModuleId.None, null, work).Should().BeNull();
        ReviewAgentFactory.Create(ReviewModuleId.Copilot, null, work)
            .Should().BeOfType<CopilotCliAgent>();
        ReviewAgentFactory.Create(ReviewModuleId.ClaudeCode, null, work)
            .Should().BeOfType<ClaudeCodeAgent>();

        // The local module is constructed even with no model path — it reports itself
        // unconfigured rather than refusing to exist, which is what keeps the panel able to
        // offer a download instead of vanishing.
        ReviewAgentFactory.Create(ReviewModuleId.Local, null, work)!
            .IsConfigured.Should().BeFalse();
    }
}
