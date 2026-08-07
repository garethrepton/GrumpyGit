using FluentAssertions;
using GrumpyGit.Core.Git;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests.Git;

public class MergeTreeParserTests
{
    [Fact]
    public void ExitZero_IsClean()
    {
        var result = MergeTreeParser.Parse("a855e3115acd8d3047d3bf8e2123c823cfd67f4b\0", 0);

        result.Outcome.Should().Be(MergeOutcome.Clean);
        result.ConflictingPaths.Should().BeEmpty();
    }

    [Fact]
    public void ExitOne_ListsConflictingPaths()
    {
        // <oid> NUL <path> NUL <path> NUL NUL <informational messages...>
        const string output =
            "a7c51228f8e1421fb36fe5081a5cd2911877c0ff\0src/a.cs\0src/b.cs\0\0" +
            "1\0src/a.cs\0Auto-merging\0Auto-merging src/a.cs\n\0";

        var result = MergeTreeParser.Parse(output, 1);

        result.Outcome.Should().Be(MergeOutcome.Conflicts);
        result.ConflictingPaths.Should().Equal("src/a.cs", "src/b.cs");
    }

    /// <summary>
    /// The informational block is NUL-separated too, so a parser that read to the end of
    /// the output would report English sentences as file names.
    /// </summary>
    [Fact]
    public void InformationalMessages_AreNotTreatedAsPaths()
    {
        const string output = "abc\0src/a.cs\0\0CONFLICT (content): Merge conflict in src/a.cs\n\0";

        var result = MergeTreeParser.Parse(output, 1);

        result.ConflictingPaths.Should().ContainSingle().Which.Should().Be("src/a.cs");
    }

    [Fact]
    public void UnexpectedExitCode_IsUnknownRatherThanClean()
    {
        var result = MergeTreeParser.Parse(string.Empty, 128);

        result.Outcome.Should().Be(MergeOutcome.Unknown);
        result.HasConflicts.Should().BeFalse();
    }

    [Fact]
    public void ConflictWithNoNamedPath_StillReportsConflict()
    {
        var result = MergeTreeParser.Parse("abc\0\0", 1);

        result.Outcome.Should().Be(MergeOutcome.Conflicts);
        result.ConflictingPaths.Should().BeEmpty();
    }
}
