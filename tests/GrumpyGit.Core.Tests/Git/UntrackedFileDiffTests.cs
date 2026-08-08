using FluentAssertions;
using GrumpyGit.Core.Git;
using GrumpyGit.Core.LocalModel;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests.Git;

/// <summary>
/// A file git does not track yet has no diff to give, so its contents are presented as one
/// large addition.
///
/// The hunk is the point. Before it existed the editor drew the content correctly and
/// everything behind it — the review, the change numbering, the notebook — saw an empty
/// diff, so the one kind of file that is entirely new was the one kind the model never read.
/// </summary>
public class UntrackedFileDiffTests
{
    private const string File = "using System;\n\nclass A\n{\n}";

    [Fact]
    public void EveryLineIsAdded()
    {
        var parsed = UnifiedDiffParser.ParseRawContent(File);

        parsed.Hunks.Should().ContainSingle();
        parsed.Hunks[0].Lines.Should().HaveCount(5);
        parsed.Hunks[0].Lines.Should().OnlyContain(l => l.Type == DiffLineType.Added);
    }

    [Fact]
    public void LineNumbersRunFromOne()
    {
        var parsed = UnifiedDiffParser.ParseRawContent(File);

        parsed.Hunks[0].Lines.Select(l => l.NewLineNumber).Should().Equal(1, 2, 3, 4, 5);
        parsed.Hunks[0].Lines.Select(l => l.RenderedLineNumber).Should().Equal(1, 2, 3, 4, 5);
        parsed.Hunks[0].Lines.Should().OnlyContain(l => l.OldLineNumber == -1,
            "nothing here existed before, so no line has an old number");
    }

    [Fact]
    public void TheHeaderSaysTheFileIsNew()
    {
        UnifiedDiffParser.ParseRawContent(File).Hunks[0].HeaderLine.Should().Be("@@ -0,0 +1,5 @@");
    }

    [Fact]
    public void ThereIsNoFileHeaderToBuildAPatchFrom()
    {
        // Untracked means no "diff --git" line exists. The viewmodel keys off this to keep
        // hunk staging off for these files, since such a patch would not apply.
        UnifiedDiffParser.ParseRawContent(File).FileHeaderLines.Should().BeEmpty();
    }

    [Fact]
    public void ItIsReviewableAsASingleChange()
    {
        // Contiguous added lines are one change, so a new file is one section — not one per
        // blank line in it.
        var blocks = DiffNotebook.Split(UnifiedDiffParser.ParseRawContent("a\nb\nc"));

        blocks.Should().ContainSingle();
        blocks[0].Added.Should().Be(3);
        blocks[0].Number.Should().Be(1);
    }

    [Fact]
    public void ThePromptShowsItAsAnAddition()
    {
        var prompt = DiffReviewPrompt.Build("New.cs", UnifiedDiffParser.ParseRawContent("var x = 1;"));

        prompt.User.Should().Contain("CHANGE 1");
        prompt.User.Should().Contain("+var x = 1;");
    }

    [Fact]
    public void AnEmptyFileProducesOneEmptyAddedLineRatherThanNothing()
    {
        // Degenerate but real — "touch new.cs" then look at it. One blank added line is a
        // truthful rendering; no hunk at all would send it back down the unreviewable path.
        var parsed = UnifiedDiffParser.ParseRawContent(string.Empty);

        parsed.Hunks.Should().ContainSingle();
        parsed.Hunks[0].Lines.Should().ContainSingle().Which.Content.Should().BeEmpty();
    }
}
