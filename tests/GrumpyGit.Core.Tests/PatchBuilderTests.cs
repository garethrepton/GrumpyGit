using FluentAssertions;
using GrumpyGit.Core.Git;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests;

public class PatchBuilderTests
{
    private static readonly IReadOnlyList<string> SampleHeaders = new[]
    {
        "diff --git a/file.txt b/file.txt",
        "index abc1234..def5678 100644",
        "--- a/file.txt",
        "+++ b/file.txt"
    };

    private static DiffHunk CreateHunk(int index, int oldStart, int oldCount, int newStart, int newCount,
        string headerLine, params DiffLine[] lines)
    {
        return new DiffHunk
        {
            Index = index,
            OldStart = oldStart,
            OldCount = oldCount,
            NewStart = newStart,
            NewCount = newCount,
            HeaderLine = headerLine,
            Lines = lines
        };
    }

    [Fact]
    public void BuildFromHunks_EmptyHunks_ReturnsEmpty()
    {
        var result = PatchBuilder.BuildFromHunks(SampleHeaders, Array.Empty<DiffHunk>());
        result.Should().BeEmpty();
    }

    [Fact]
    public void BuildFromHunks_SingleHunk_ProducesValidPatch()
    {
        var hunk = CreateHunk(0, 1, 3, 1, 3, "@@ -1,3 +1,3 @@",
            new DiffLine { Type = DiffLineType.Context, Content = "line1" },
            new DiffLine { Type = DiffLineType.Removed, Content = "old" },
            new DiffLine { Type = DiffLineType.Added, Content = "new" },
            new DiffLine { Type = DiffLineType.Context, Content = "line3" });

        var result = PatchBuilder.BuildFromHunks(SampleHeaders, new[] { hunk });

        result.Should().Contain("diff --git a/file.txt b/file.txt");
        result.Should().Contain("--- a/file.txt");
        result.Should().Contain("+++ b/file.txt");
        result.Should().Contain("@@ -1,3 +1,3 @@");
        result.Should().Contain(" line1");
        result.Should().Contain("-old");
        result.Should().Contain("+new");
        result.Should().Contain(" line3");
    }

    [Fact]
    public void BuildFromHunks_MultipleHunks_IncludesAll()
    {
        var hunk1 = CreateHunk(0, 1, 1, 1, 1, "@@ -1,1 +1,1 @@",
            new DiffLine { Type = DiffLineType.Removed, Content = "a" },
            new DiffLine { Type = DiffLineType.Added, Content = "A" });

        var hunk2 = CreateHunk(1, 10, 1, 10, 1, "@@ -10,1 +10,1 @@",
            new DiffLine { Type = DiffLineType.Removed, Content = "b" },
            new DiffLine { Type = DiffLineType.Added, Content = "B" });

        var result = PatchBuilder.BuildFromHunks(SampleHeaders, new[] { hunk1, hunk2 });

        result.Should().Contain("@@ -1,1 +1,1 @@");
        result.Should().Contain("@@ -10,1 +10,1 @@");
        result.Should().Contain("-a");
        result.Should().Contain("+A");
        result.Should().Contain("-b");
        result.Should().Contain("+B");
    }

    [Fact]
    public void BuildFromHunks_NoNewlineMarker_IsPreserved()
    {
        var hunk = CreateHunk(0, 1, 1, 1, 1, "@@ -1,1 +1,1 @@",
            new DiffLine { Type = DiffLineType.Removed, Content = "old" },
            new DiffLine { Type = DiffLineType.NoNewlineMarker, Content = @"\ No newline at end of file" },
            new DiffLine { Type = DiffLineType.Added, Content = "new" });

        var result = PatchBuilder.BuildFromHunks(SampleHeaders, new[] { hunk });

        result.Should().Contain(@"\ No newline at end of file");
    }

    [Fact]
    public void BuildFromSelectedLines_AllSelected_MatchesFullHunk()
    {
        var hunk = CreateHunk(0, 1, 3, 1, 3, "@@ -1,3 +1,3 @@",
            new DiffLine { Type = DiffLineType.Context, Content = "ctx" },
            new DiffLine { Type = DiffLineType.Removed, Content = "old" },
            new DiffLine { Type = DiffLineType.Added, Content = "new" },
            new DiffLine { Type = DiffLineType.Context, Content = "ctx2" });

        var selected = new HashSet<int> { 1, 2 }; // removed and added

        var result = PatchBuilder.BuildFromSelectedLines(SampleHeaders, hunk, selected);

        result.Should().Contain("-old");
        result.Should().Contain("+new");
        result.Should().Contain(" ctx");
    }

    [Fact]
    public void BuildFromSelectedLines_OnlyAddedSelected_OmitsRemoved()
    {
        var hunk = CreateHunk(0, 1, 3, 1, 3, "@@ -1,3 +1,3 @@",
            new DiffLine { Type = DiffLineType.Context, Content = "ctx" },
            new DiffLine { Type = DiffLineType.Removed, Content = "old" },
            new DiffLine { Type = DiffLineType.Added, Content = "new" },
            new DiffLine { Type = DiffLineType.Context, Content = "ctx2" });

        // Select only the added line (index 2)
        var selected = new HashSet<int> { 2 };

        var result = PatchBuilder.BuildFromSelectedLines(SampleHeaders, hunk, selected);

        // Unselected removed line becomes context
        result.Should().Contain(" old");
        result.Should().Contain("+new");
        result.Should().NotContain("-old");
    }

    [Fact]
    public void BuildFromSelectedLines_OnlyRemovedSelected_OmitsAdded()
    {
        var hunk = CreateHunk(0, 1, 3, 1, 3, "@@ -1,3 +1,3 @@",
            new DiffLine { Type = DiffLineType.Context, Content = "ctx" },
            new DiffLine { Type = DiffLineType.Removed, Content = "old" },
            new DiffLine { Type = DiffLineType.Added, Content = "new" },
            new DiffLine { Type = DiffLineType.Context, Content = "ctx2" });

        // Select only the removed line (index 1)
        var selected = new HashSet<int> { 1 };

        var result = PatchBuilder.BuildFromSelectedLines(SampleHeaders, hunk, selected);

        result.Should().Contain("-old");
        result.Should().NotContain("+new");
    }

    [Fact]
    public void BuildFromSelectedLines_NoneSelected_ReturnsEmpty()
    {
        var hunk = CreateHunk(0, 1, 3, 1, 3, "@@ -1,3 +1,3 @@",
            new DiffLine { Type = DiffLineType.Context, Content = "ctx" },
            new DiffLine { Type = DiffLineType.Removed, Content = "old" },
            new DiffLine { Type = DiffLineType.Added, Content = "new" },
            new DiffLine { Type = DiffLineType.Context, Content = "ctx2" });

        var selected = new HashSet<int>(); // nothing selected

        var result = PatchBuilder.BuildFromSelectedLines(SampleHeaders, hunk, selected);

        result.Should().BeEmpty();
    }

    [Fact]
    public void BuildFromSelectedLines_RecalculatesLineCounts()
    {
        var hunk = CreateHunk(0, 1, 4, 1, 4, "@@ -1,4 +1,4 @@",
            new DiffLine { Type = DiffLineType.Context, Content = "ctx" },
            new DiffLine { Type = DiffLineType.Removed, Content = "r1" },
            new DiffLine { Type = DiffLineType.Removed, Content = "r2" },
            new DiffLine { Type = DiffLineType.Added, Content = "a1" },
            new DiffLine { Type = DiffLineType.Added, Content = "a2" },
            new DiffLine { Type = DiffLineType.Context, Content = "ctx2" });

        // Select only one removed and one added
        var selected = new HashSet<int> { 1, 3 }; // r1, a1

        var result = PatchBuilder.BuildFromSelectedLines(SampleHeaders, hunk, selected);

        // OldCount = context(2) + unselected-removed-as-context(1) + selected-removed(1) = 4
        // NewCount = context(2) + unselected-removed-as-context(1) + selected-added(1) = 4
        result.Should().Contain("@@ -1,4 +1,4 @@");
        result.Should().Contain("-r1");
        result.Should().Contain(" r2"); // unselected removed -> context
        result.Should().Contain("+a1");
        result.Should().NotContain("+a2"); // unselected added -> omitted
    }

    [Fact]
    public void BuildFromSelectedLines_OnlyContextSelected_ReturnsEmpty()
    {
        var hunk = CreateHunk(0, 1, 2, 1, 2, "@@ -1,2 +1,2 @@",
            new DiffLine { Type = DiffLineType.Context, Content = "ctx" },
            new DiffLine { Type = DiffLineType.Removed, Content = "old" },
            new DiffLine { Type = DiffLineType.Added, Content = "new" });

        // Select only context line (index 0) - no actual changes
        var selected = new HashSet<int> { 0 };

        var result = PatchBuilder.BuildFromSelectedLines(SampleHeaders, hunk, selected);

        // Context lines don't count as "changes"
        result.Should().BeEmpty();
    }
}
