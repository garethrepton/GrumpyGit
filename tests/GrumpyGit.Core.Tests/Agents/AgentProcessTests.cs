using FluentAssertions;
using GrumpyGit.Core.Agents;

namespace GrumpyGit.Core.Tests.Agents;

/// <summary>
/// The launcher's two rules, both of which exist to keep a diff away from a shell.
///
/// Nothing here starts a process. The behaviour worth pinning is which files this is willing
/// to launch and which names it is willing to resolve — launching one would test the vendor's
/// CLI, which is not ours to test and not present on a build agent.
/// </summary>
public class AgentProcessTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("grumpy-agent-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Touch(string fileName)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    [Fact]
    public void ARealExecutableIsFound()
    {
        var expected = Touch("copilot.exe");

        AgentProcess.ResolveIn([_dir], "copilot").Should().Be(expected);
    }

    [Fact]
    public void ABatchShimIsRefusedRatherThanLaunched()
    {
        // The failure this covers is the whole reason the rule exists: Windows runs a .cmd
        // through cmd.exe, which re-parses the command line after CreateProcess has quoted
        // it. A diff carrying a quote and an ampersand would then be a command, not text.
        Touch("copilot.cmd");
        Touch("copilot.bat");
        Touch("copilot.ps1");

        AgentProcess.ResolveIn([_dir], "copilot").Should().BeNull();
    }

    [Fact]
    public void AShimIsReportedAsInstalledButUnusable()
    {
        // So the user reads "install the standalone build" rather than "not found", which
        // would be a lie they would spend an evening disproving.
        Touch("copilot.cmd");

        AgentProcess.ResolvesToShimOnlyIn([_dir], "copilot").Should().BeTrue();
    }

    [Fact]
    public void ARealExecutableBesideAShimIsNotReportedAsAShim()
    {
        Touch("copilot.cmd");
        Touch("copilot.exe");

        AgentProcess.ResolvesToShimOnlyIn([_dir], "copilot").Should().BeFalse();
    }

    [Theory]
    [InlineData("../../../windows/system32/calc")]
    [InlineData(@"C:\windows\system32\calc")]
    [InlineData("/usr/bin/env")]
    [InlineData("copilot\"")]
    public void AnythingButABareNameIsRefused(string command)
    {
        // Every name reaching Resolve is a constant from the catalogue. This is the guard
        // that keeps it that way if a later caller ever passes a settings value instead.
        var resolve = () => AgentProcess.ResolveIn([_dir], command);

        resolve.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EscapeSequencesAreStrippedFromAReply()
    {
        // A CLI that decides it is on a terminal despite NO_COLOR would otherwise feed
        // cursor movements to a parser looking for "SUMMARY:".
        AgentProcess.Clean("\u001b[1;32mSUMMARY:\u001b[0m fine")
            .Should().Be("SUMMARY: fine");
    }

    [Fact]
    public void EveryCliModuleNamesABareExecutable()
    {
        // The outbound-surface invariant for process launches: a module may name a command,
        // never a path, so no catalogue entry can ever become "run this file".
        var executables = ReviewModuleCatalogue.All
            .Where(m => m.Kind == ReviewModuleKind.ExternalCli)
            .Select(m => m.Executable);

        executables.Should().NotBeEmpty();
        executables.Should().AllSatisfy(exe =>
        {
            exe.Should().NotBeNullOrWhiteSpace();
            exe!.IndexOfAny(['/', '\\', ':', '"', ' ']).Should().Be(-1);
        });
    }
}
