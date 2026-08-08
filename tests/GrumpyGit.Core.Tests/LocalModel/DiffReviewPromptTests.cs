using FluentAssertions;
using GrumpyGit.Core.LocalModel;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests.LocalModel;

/// <summary>
/// The prompt is a pure function of the diff, which is what makes the review cache sound
/// and these tests fast. What is asserted here is the budget behaviour: what gets left
/// out when a file is too big, and whether the model is told it happened.
/// </summary>
public class DiffReviewPromptTests
{
    private static DiffHunk Hunk(int index, string header, params (DiffLineType Type, string Content)[] lines) =>
        new()
        {
            Index = index,
            HeaderLine = header,
            Lines = lines.Select(l => new DiffLine { Type = l.Type, Content = l.Content }).ToList(),
        };

    private static ParsedDiff Diff(params DiffHunk[] hunks) =>
        new("old", "new", [], [], [], hunks: hunks);

    [Fact]
    public void ChangedLinesAreIncludedWithTheirMarkers()
    {
        var diff = Diff(Hunk(0, "@@ -1,2 +1,2 @@ void Guard()",
            (DiffLineType.Removed, "if (x > 0)"),
            (DiffLineType.Added, "if (x >= 0)")));

        var prompt = DiffReviewPrompt.Build("src/Guard.cs", diff);

        prompt.User.Should().Contain("-if (x > 0)");
        prompt.User.Should().Contain("+if (x >= 0)");
        prompt.User.Should().Contain("src/Guard.cs");
    }

    [Fact]
    public void ContextLinesAreLeftOut()
    {
        var diff = Diff(Hunk(0, "@@ -1,3 +1,3 @@",
            (DiffLineType.Context, "// unchanged neighbour"),
            (DiffLineType.Added, "var x = 1;")));

        var prompt = DiffReviewPrompt.Build("a.cs", diff);

        prompt.User.Should().NotContain("unchanged neighbour");
        prompt.User.Should().Contain("+var x = 1;");
    }

    [Fact]
    public void TheSymbolSummaryIsIncludedWhenThereIsOne()
    {
        var diff = Diff(Hunk(0, "@@ -1 +1 @@", (DiffLineType.Added, "x")));
        var summary = new FileChangeSummary("a.cs", 1, 0,
        [
            new SymbolChange("void Guard()", 1, 0, 4, 1, SymbolChangeKind.Added, "guard added"),
        ]);

        var prompt = DiffReviewPrompt.Build("a.cs", diff, summary);

        prompt.User.Should().Contain("void Guard()");
    }

    [Fact]
    public void AnonymousSymbolsAreNotNamedInTheSummaryLine()
    {
        var diff = Diff(Hunk(0, "@@ -1 +1 @@", (DiffLineType.Added, "x")));
        var summary = new FileChangeSummary("a.cs", 1, 0,
        [
            new SymbolChange("", 1, 0, 4, 1, SymbolChangeKind.Added, "1 line added"),
        ]);

        var prompt = DiffReviewPrompt.Build("a.cs", diff, summary);

        prompt.User.Should().NotContain("Touched: (");
    }

    [Fact]
    public void AnOversizedFileIsTruncatedByWholeHunksAndSaysSo()
    {
        var fat = new string('x', 500);
        var hunks = Enumerable.Range(0, 40)
            .Select(i => Hunk(i, $"@@ hunk {i} @@", (DiffLineType.Added, fat)))
            .ToArray();

        var prompt = DiffReviewPrompt.Build("big.cs", Diff(hunks));

        prompt.User.Length.Should().BeLessThan(DiffReviewPrompt.DiffCharacterBudget + 1500);
        prompt.User.Should().Contain("omitted");
        prompt.User.Should().Contain("@@ hunk 0 @@", "truncation drops the tail, not the head");
    }

    [Fact]
    public void ASingleHunkLargerThanTheBudgetIsStillSent()
    {
        // Dropping it would leave the model nothing to review at all, which is a worse
        // answer than a long prompt.
        var fat = new string('y', DiffReviewPrompt.DiffCharacterBudget * 2);
        var prompt = DiffReviewPrompt.Build("huge.cs", Diff(Hunk(0, "@@ one @@", (DiffLineType.Added, fat))));

        prompt.User.Should().Contain(fat);
    }

    [Fact]
    public void TheSameDiffProducesTheSamePrompt()
    {
        var build = () => DiffReviewPrompt.Build("a.cs", Diff(Hunk(0, "@@ -1 +1 @@",
            (DiffLineType.Added, "var x = 1;"))));

        build().Should().BeEquivalentTo(build());
    }

    [Fact]
    public void TheSystemInstructionForbidsInventedLineNumbers()
    {
        var prompt = DiffReviewPrompt.Build("a.cs", Diff(Hunk(0, "@@ -1 +1 @@", (DiffLineType.Added, "x"))));

        prompt.System.Should().Contain("Never invent a line number");
    }

    [Fact]
    public void ChangesAreNumberedSoTheModelCanReferToThem()
    {
        var diff = Diff(
            Hunk(0, "@@ -1 +1 @@ void A()", (DiffLineType.Added, "a")),
            Hunk(1, "@@ -9 +9 @@ void B()", (DiffLineType.Added, "b")));

        var prompt = DiffReviewPrompt.Build("a.cs", diff);

        prompt.User.Should().Contain("CHANGE 1 @@ -1 +1 @@ void A()");
        prompt.User.Should().Contain("CHANGE 2 @@ -9 +9 @@ void B()");
    }

    [Fact]
    public void OneHunkWithSeveralEditsIsNumberedAsSeveralChanges()
    {
        // The unit the model describes is the edit, not git's patch-sized hunk. A hunk
        // holding three runs of changed lines separated by context is three changes, and
        // the view will draw three sections for it.
        var diff = Diff(Hunk(0, "@@ -1,9 +1,9 @@ void A()",
            (DiffLineType.Added, "first"),
            (DiffLineType.Context, "unchanged"),
            (DiffLineType.Context, "unchanged"),
            (DiffLineType.Removed, "second old"),
            (DiffLineType.Added, "second new"),
            (DiffLineType.Context, "unchanged"),
            (DiffLineType.Context, "unchanged"),
            (DiffLineType.Added, "third")));

        var prompt = DiffReviewPrompt.Build("a.cs", diff);

        prompt.User.Should().Contain("CHANGE 1");
        prompt.User.Should().Contain("CHANGE 2");
        prompt.User.Should().Contain("CHANGE 3");
        prompt.User.Should().NotContain("CHANGE 4");

        // A removal and the addition replacing it are one edit, not two.
        prompt.User.Should().Contain("-second old");
        prompt.User.Should().Contain("+second new");
    }

    [Fact]
    public void AddedLinesCarryTheirNewFileLineNumber()
    {
        var hunk = new DiffHunk
        {
            Index = 0,
            HeaderLine = "@@ -41,2 +41,2 @@",
            Lines =
            [
                new DiffLine { Type = DiffLineType.Removed, Content = "old", OldLineNumber = 41 },
                new DiffLine { Type = DiffLineType.Added, Content = "new", NewLineNumber = 41 },
            ],
        };

        var prompt = DiffReviewPrompt.Build("a.cs", Diff(hunk));

        prompt.User.Should().Contain("41 +new");
        prompt.User.Should().Contain("-old");
        prompt.User.Should().NotContain("41 -old", "a removed line has no line in the new file");
    }
}
