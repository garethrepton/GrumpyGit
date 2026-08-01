using FluentAssertions;
using GrumpyGit.Core.Git;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests;

public class UnifiedDiffParserHunkTests
{
    [Fact]
    public void Parse_SingleHunk_PopulatesHunkAndLines()
    {
        var diff = """
            diff --git a/file.txt b/file.txt
            index abc1234..def5678 100644
            --- a/file.txt
            +++ b/file.txt
            @@ -1,3 +1,4 @@
             line1
            -line2
            +line2modified
            +line3new
             line4
            """;

        var result = UnifiedDiffParser.Parse(diff);

        result.Hunks.Should().HaveCount(1);
        var hunk = result.Hunks[0];
        hunk.Index.Should().Be(0);
        hunk.OldStart.Should().Be(1);
        hunk.OldCount.Should().Be(3);
        hunk.NewStart.Should().Be(1);
        hunk.NewCount.Should().Be(4);
        hunk.HeaderLine.Should().Be("@@ -1,3 +1,4 @@");

        hunk.Lines.Should().HaveCount(5);
        hunk.Lines[0].Type.Should().Be(DiffLineType.Context);
        hunk.Lines[0].Content.Should().Be("line1");
        hunk.Lines[1].Type.Should().Be(DiffLineType.Removed);
        hunk.Lines[1].Content.Should().Be("line2");
        hunk.Lines[2].Type.Should().Be(DiffLineType.Added);
        hunk.Lines[2].Content.Should().Be("line2modified");
        hunk.Lines[3].Type.Should().Be(DiffLineType.Added);
        hunk.Lines[3].Content.Should().Be("line3new");
        hunk.Lines[4].Type.Should().Be(DiffLineType.Context);
        hunk.Lines[4].Content.Should().Be("line4");
    }

    [Fact]
    public void Parse_SingleHunk_IncludesTrailingContext()
    {
        var diff = """
            diff --git a/file.txt b/file.txt
            index abc..def 100644
            --- a/file.txt
            +++ b/file.txt
            @@ -1,3 +1,3 @@
             context
            -old
            +new
             trailing
            """;

        var result = UnifiedDiffParser.Parse(diff);

        result.Hunks.Should().HaveCount(1);
        var hunk = result.Hunks[0];
        hunk.Lines.Should().HaveCount(4);
        hunk.Lines[0].Type.Should().Be(DiffLineType.Context);
        hunk.Lines[1].Type.Should().Be(DiffLineType.Removed);
        hunk.Lines[2].Type.Should().Be(DiffLineType.Added);
        hunk.Lines[3].Type.Should().Be(DiffLineType.Context);
        hunk.Lines[3].Content.Should().Be("trailing");
    }

    [Fact]
    public void Parse_MultipleHunks_CreatesMultipleHunkObjects()
    {
        var diff = """
            diff --git a/file.txt b/file.txt
            index abc..def 100644
            --- a/file.txt
            +++ b/file.txt
            @@ -1,3 +1,3 @@
             a
            -b
            +B
             c
            @@ -10,3 +10,4 @@
             x
            -y
            +Y
            +Z
             w
            """;

        var result = UnifiedDiffParser.Parse(diff);

        result.Hunks.Should().HaveCount(2);

        result.Hunks[0].Index.Should().Be(0);
        result.Hunks[0].OldStart.Should().Be(1);
        result.Hunks[0].NewStart.Should().Be(1);

        result.Hunks[1].Index.Should().Be(1);
        result.Hunks[1].OldStart.Should().Be(10);
        result.Hunks[1].NewStart.Should().Be(10);
        result.Hunks[1].NewCount.Should().Be(4);
    }

    [Fact]
    public void Parse_AddedFile_HasCorrectHunkCounts()
    {
        var diff = """
            diff --git a/newfile.txt b/newfile.txt
            new file mode 100644
            index 0000000..abc1234
            --- /dev/null
            +++ b/newfile.txt
            @@ -0,0 +1,3 @@
            +line1
            +line2
            +line3
            """;

        var result = UnifiedDiffParser.Parse(diff);

        result.Hunks.Should().HaveCount(1);
        var hunk = result.Hunks[0];
        hunk.OldStart.Should().Be(0);
        hunk.OldCount.Should().Be(0);
        hunk.NewStart.Should().Be(1);
        hunk.NewCount.Should().Be(3);
        hunk.Lines.Should().HaveCount(3);
        hunk.Lines.Should().AllSatisfy(l => l.Type.Should().Be(DiffLineType.Added));
    }

    [Fact]
    public void Parse_DeletedFile_HasCorrectHunkCounts()
    {
        var diff = """
            diff --git a/old.txt b/old.txt
            deleted file mode 100644
            index abc1234..0000000
            --- a/old.txt
            +++ /dev/null
            @@ -1,2 +0,0 @@
            -line1
            -line2
            """;

        var result = UnifiedDiffParser.Parse(diff);

        result.Hunks.Should().HaveCount(1);
        var hunk = result.Hunks[0];
        hunk.OldStart.Should().Be(1);
        hunk.OldCount.Should().Be(2);
        hunk.NewStart.Should().Be(0);
        hunk.NewCount.Should().Be(0);
        hunk.Lines.Should().HaveCount(2);
        hunk.Lines.Should().AllSatisfy(l => l.Type.Should().Be(DiffLineType.Removed));
    }

    [Fact]
    public void Parse_NoNewlineMarker_IsPreserved()
    {
        var diff = """
            diff --git a/file.txt b/file.txt
            index abc..def 100644
            --- a/file.txt
            +++ b/file.txt
            @@ -1,2 +1,2 @@
             line1
            -line2
            \ No newline at end of file
            +line2modified
            \ No newline at end of file
            """;

        var result = UnifiedDiffParser.Parse(diff);

        result.Hunks.Should().HaveCount(1);
        var hunk = result.Hunks[0];
        // Context + Removed + NoNewline + Added + NoNewline
        hunk.Lines.Count(l => l.Type == DiffLineType.NoNewlineMarker).Should().Be(2);
    }

    [Fact]
    public void Parse_FileHeaderLines_AreCaptured()
    {
        var diff = """
            diff --git a/file.txt b/file.txt
            index abc1234..def5678 100644
            --- a/file.txt
            +++ b/file.txt
            @@ -1,1 +1,1 @@
            -old
            +new
            """;

        var result = UnifiedDiffParser.Parse(diff);

        result.FileHeaderLines.Should().HaveCount(4);
        result.FileHeaderLines[0].Should().StartWith("diff --git");
        result.FileHeaderLines[1].Should().StartWith("index");
        result.FileHeaderLines[2].Should().StartWith("--- ");
        result.FileHeaderLines[3].Should().StartWith("+++ ");
    }

    [Fact]
    public void Parse_RenameWithChanges_CapturesRenameHeaders()
    {
        var diff = """
            diff --git a/old.txt b/new.txt
            similarity index 80%
            rename from old.txt
            rename to new.txt
            index abc..def 100644
            --- a/old.txt
            +++ b/new.txt
            @@ -1,1 +1,1 @@
            -old content
            +new content
            """;

        var result = UnifiedDiffParser.Parse(diff);

        result.FileHeaderLines.Should().Contain(h => h.StartsWith("rename from"));
        result.FileHeaderLines.Should().Contain(h => h.StartsWith("rename to"));
        result.Hunks.Should().HaveCount(1);
    }

    [Fact]
    public void Parse_EmptyDiff_ReturnsEmptyHunks()
    {
        var result = UnifiedDiffParser.Parse("");

        result.Hunks.Should().BeEmpty();
        result.FileHeaderLines.Should().BeEmpty();
    }

    [Fact]
    public void Parse_RenderedLineNumber_IsSetOnHunks()
    {
        var diff = """
            diff --git a/file.txt b/file.txt
            index abc..def 100644
            --- a/file.txt
            +++ b/file.txt
            @@ -1,2 +1,2 @@
             context
            -removed
            +added
            """;

        var result = UnifiedDiffParser.Parse(diff);

        result.Hunks[0].RenderedLineNumber.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Parse_OldAndNewLineNumbers_AreTracked()
    {
        var diff = """
            diff --git a/file.txt b/file.txt
            index abc..def 100644
            --- a/file.txt
            +++ b/file.txt
            @@ -5,3 +5,3 @@
             context
            -removed
            +added
             trailing
            """;

        var result = UnifiedDiffParser.Parse(diff);

        var hunk = result.Hunks[0];
        // Context line at old=5, new=5
        hunk.Lines[0].OldLineNumber.Should().Be(5);
        hunk.Lines[0].NewLineNumber.Should().Be(5);

        // Removed line at old=6
        hunk.Lines[1].OldLineNumber.Should().Be(6);

        // Added line at new=6
        hunk.Lines[2].NewLineNumber.Should().Be(6);

        // Trailing context at old=7, new=7
        hunk.Lines[3].OldLineNumber.Should().Be(7);
        hunk.Lines[3].NewLineNumber.Should().Be(7);
    }

    [Fact]
    public void Parse_SideBySideRendering_StillWorksCorrectly()
    {
        // Ensure the existing side-by-side rendering output is not broken
        var diff = """
            diff --git a/file.txt b/file.txt
            index abc..def 100644
            --- a/file.txt
            +++ b/file.txt
            @@ -1,3 +1,3 @@
             same
            -old
            +new
             same2
            """;

        var result = UnifiedDiffParser.Parse(diff);

        // Left text should contain the hunk header, context, and removed lines
        result.LeftText.Should().Contain("old");
        result.RightText.Should().Contain("new");

        result.LeftColoredLines.Should().NotBeEmpty();
        result.RightColoredLines.Should().NotBeEmpty();
        result.HunkHeaderLines.Should().NotBeEmpty();
    }
}
