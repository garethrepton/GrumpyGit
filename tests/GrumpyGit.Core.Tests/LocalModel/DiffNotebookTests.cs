using FluentAssertions;
using GrumpyGit.Core.LocalModel;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests.LocalModel;

/// <summary>
/// What counts as one change, and which description ends up above which code.
///
/// This is the definition the prompt numbers from, the parser reads back, and the notebook
/// draws — so a disagreement here is a description appearing above the wrong lines, which
/// is worse than no description at all.
/// </summary>
public class DiffNotebookTests
{
    /// <summary>A hunk whose lines are laid out by marker: '+', '-' or ' '.</summary>
    private static DiffHunk Hunk(string markers, int renderedStart = 1, string header = "@@ -1 +1 @@")
    {
        var lines = new List<DiffLine>();
        var rendered = renderedStart;
        var newLine = renderedStart;

        foreach (var marker in markers)
        {
            var type = marker switch
            {
                '+' => DiffLineType.Added,
                '-' => DiffLineType.Removed,
                _ => DiffLineType.Context,
            };

            lines.Add(new DiffLine
            {
                Type = type,
                Content = $"{marker}line",
                NewLineNumber = type == DiffLineType.Removed ? -1 : newLine++,
                RenderedLineNumber = rendered++,
            });
        }

        return new DiffHunk { HeaderLine = header, Lines = lines };
    }

    private static ParsedDiff DiffOf(params DiffHunk[] hunks) =>
        new("old", "new", [], [], [], hunks: hunks);

    [Fact]
    public void ContiguousChangedLinesAreOneChange()
    {
        var blocks = DiffNotebook.Split(DiffOf(Hunk("+++")));

        blocks.Should().ContainSingle();
        blocks[0].Lines.Should().HaveCount(3);
        blocks[0].Number.Should().Be(1);
    }

    [Fact]
    public void ContextSeparatesChanges()
    {
        // Ten separate edits in one file are ten sections, which is the whole point of
        // splitting on the edit rather than on git's hunk.
        var blocks = DiffNotebook.Split(DiffOf(Hunk("+  +  +  +")));

        blocks.Should().HaveCount(4);
        blocks.Select(b => b.Number).Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public void ASingleUnchangedLineDoesNotSeparateTwoEdits()
    {
        // Two edits a line apart are almost always one edit — a rename used either side of
        // a line that happened not to change. Splitting them would double the sections on
        // screen and halve the description budget each one gets.
        var blocks = DiffNotebook.Split(DiffOf(Hunk("+ +")));

        blocks.Should().ContainSingle();

        // The line between them comes with, because a reader needs it to see why they are
        // one change.
        blocks[0].Lines.Should().HaveCount(3);
        blocks[0].Added.Should().Be(2);
    }

    [Fact]
    public void ATrailingGapIsNotCarriedIntoTheBlock()
    {
        var blocks = DiffNotebook.Split(DiffOf(Hunk("++ ")));

        blocks.Should().ContainSingle();
        blocks[0].Lines.Should().HaveCount(2, "the change ends at its last changed line");
    }

    [Fact]
    public void ARemovalAndItsReplacementAreOneChange()
    {
        // A rewritten line is "-old" then "+new". Calling that two changes would be
        // counting the diff format rather than the edit.
        var blocks = DiffNotebook.Split(DiffOf(Hunk("-+")));

        blocks.Should().ContainSingle();
        blocks[0].Added.Should().Be(1);
        blocks[0].Removed.Should().Be(1);
    }

    [Fact]
    public void NumberingRunsAcrossHunksNotWithinThem()
    {
        var blocks = DiffNotebook.Split(DiffOf(
            Hunk("+  +", renderedStart: 1, header: "@@ a @@"),
            Hunk("+", renderedStart: 10, header: "@@ b @@")));

        blocks.Select(b => b.Number).Should().Equal(1, 2, 3);
        blocks[2].HeaderLine.Should().Be("@@ b @@");
    }

    [Fact]
    public void AChangeStartsAtItsFirstChangedLine()
    {
        // Where the editor draws the callout. The @@ header is not it.
        var blocks = DiffNotebook.Split(DiffOf(Hunk("  ++", renderedStart: 5)));

        blocks.Should().ContainSingle();
        blocks[0].StartRenderedLine.Should().Be(7);
    }

    [Fact]
    public void AHunkOfPureContextProducesNoChange()
    {
        DiffNotebook.Split(DiffOf(Hunk("   "))).Should().BeEmpty();
    }

    [Fact]
    public void CellsCarryOnlyChangedLines()
    {
        var notebook = DiffNotebook.Build(DiffOf(Hunk("  ++  ")));

        notebook.Should().ContainSingle();
        notebook[0].Lines.Should().OnlyContain(l => l.Type == DiffLineType.Added);
    }

    [Fact]
    public void EachChangeKeepsItsOwnNote()
    {
        var notebook = DiffNotebook.Build(
            DiffOf(Hunk("+  +  +")),
            [new ChangeNote(1, 1, "widens the guard"), new ChangeNote(3, 5, "renames the field")]);

        notebook.Should().HaveCount(3);
        notebook[0].Note.Should().Be("widens the guard");
        notebook[1].HasNote.Should().BeFalse("the model gave no line for change 2");
        notebook[2].Note.Should().Be("renames the field");
    }

    [Fact]
    public void ANoteForAChangeThatDoesNotExistIsDropped()
    {
        var notebook = DiffNotebook.Build(DiffOf(Hunk("+")), [new ChangeNote(7, 99, "about nothing")]);

        notebook.Should().ContainSingle();
        notebook[0].HasNote.Should().BeFalse();
    }

    [Fact]
    public void AnIssueLandsOnTheChangeContainingItsLine()
    {
        var notebook = DiffNotebook.Build(
            DiffOf(Hunk("+  +", renderedStart: 1)),
            notes: null,
            issues:
            [
                new ReviewIssue(4, 4, "null check dropped"),
                new ReviewIssue(1, 1, "off by one"),
            ]);

        notebook[0].Issues.Should().ContainSingle().Which.Text.Should().Be("off by one");
        notebook[1].Issues.Should().ContainSingle().Which.Text.Should().Be("null check dropped");
    }

    [Fact]
    public void AnUnanchoredIssueBelongsToNoChange()
    {
        // It stays in the file-level panel instead — attributing it to a section it may
        // have nothing to do with would be inventing a location the model never gave.
        var notebook = DiffNotebook.Build(
            DiffOf(Hunk("+++")),
            notes: null,
            issues: [new ReviewIssue(999, 0, "somewhere in this file")]);

        notebook[0].Issues.Should().BeEmpty();
    }

    [Fact]
    public void AReviewThatNeverArrivedStillGivesSections()
    {
        // The layout is worth something without a model, so no notes means plain sections
        // rather than an empty view.
        var notebook = DiffNotebook.Build(DiffOf(Hunk("+  +")));

        notebook.Should().HaveCount(2);
        notebook.Should().OnlyContain(c => !c.HasNote && !c.HasIssues);
    }

    [Fact]
    public void NoDiffIsNoSections()
    {
        DiffNotebook.Split(null).Should().BeEmpty();
        DiffNotebook.Build(null).Should().BeEmpty();
        DiffNotebook.Build(DiffOf()).Should().BeEmpty();
    }
}
