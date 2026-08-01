using FluentAssertions;
using GrumpyGit.Core.Git;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests.Git;

public class ChangeSummaryBuilderTests
{
    private static DiffHunk Hunk(string header, int index, int added, int removed, int renderedLine = 1)
    {
        var lines = new List<DiffLine>();
        for (var i = 0; i < added; i++) lines.Add(new DiffLine { Type = DiffLineType.Added });
        for (var i = 0; i < removed; i++) lines.Add(new DiffLine { Type = DiffLineType.Removed });
        lines.Add(new DiffLine { Type = DiffLineType.Context });

        return new DiffHunk
        {
            Index = index,
            HeaderLine = header,
            Lines = lines,
            RenderedLineNumber = renderedLine,
        };
    }

    private static ParsedDiff DiffOf(params DiffHunk[] hunks) =>
        new("", "", [], [], [], hunks: hunks);

    [Fact]
    public void ExtractSymbol_TakesTheContextAfterTheSecondMarker()
    {
        ChangeSummaryBuilder.ExtractSymbol("@@ -154,6 +154,10 @@ private void OnLoaded(object? sender)")
            .Should().Be("private void OnLoaded(object? sender)");
    }

    [Fact]
    public void ExtractSymbol_KeepsAMarkerThatOccursInsideTheDeclaration()
    {
        // Taking the LAST @@ instead of the second would truncate this to "b".
        ChangeSummaryBuilder.ExtractSymbol("@@ -1,2 +1,2 @@ void F(string a = \"@@\", int b)")
            .Should().Be("void F(string a = \"@@\", int b)");
    }

    [Fact]
    public void ExtractSymbol_IsEmptyWhenNoDriverSuppliedContext()
    {
        ChangeSummaryBuilder.ExtractSymbol("@@ -1,4 +1,6 @@").Should().BeEmpty();
    }

    [Fact]
    public void ExtractSymbol_StripsTrailingBraceAndCollapsesWhitespace()
    {
        ChangeSummaryBuilder.ExtractSymbol("@@ -1,2 +1,2 @@   public   void   F()   {")
            .Should().Be("public void F()");
    }

    [Fact]
    public void Build_MergesEveryHunkInsideOneSymbol()
    {
        var summary = ChangeSummaryBuilder.Build("A.cs",
            DiffOf(
                Hunk("@@ -1,2 +1,4 @@ void Alpha()", 0, added: 3, removed: 1, renderedLine: 5),
                Hunk("@@ -9,2 +11,4 @@ void Alpha()", 1, added: 2, removed: 0, renderedLine: 20)));

        var symbol = summary.Symbols.Should().ContainSingle().Subject;
        symbol.Symbol.Should().Be("void Alpha()");
        symbol.Added.Should().Be(5);
        symbol.Removed.Should().Be(1);
        symbol.HunkCount.Should().Be(2);

        // Jump target is the FIRST hunk, so clicking lands on the earliest edit.
        symbol.RenderedLineNumber.Should().Be(5);
    }

    [Fact]
    public void Build_KeepsSeparateSymbolsApartAndInDiffOrder()
    {
        var summary = ChangeSummaryBuilder.Build("A.cs",
            DiffOf(
                Hunk("@@ -1,2 +1,2 @@ void Beta()", 0, added: 1, removed: 1),
                Hunk("@@ -9,2 +9,2 @@ void Alpha()", 1, added: 2, removed: 0)));

        summary.Symbols.Select(s => s.Symbol).Should().ContainInOrder("void Beta()", "void Alpha()");
    }

    [Fact]
    public void Build_DoesNotMergeUnlabelledHunksTogether()
    {
        // Two anonymous hunks are unrelated edits that merely share a lack of label;
        // folding them into one entry would claim a relationship that does not exist.
        var summary = ChangeSummaryBuilder.Build("A.txt",
            DiffOf(
                Hunk("@@ -1,2 +1,2 @@", 0, added: 1, removed: 1),
                Hunk("@@ -9,2 +9,2 @@", 1, added: 1, removed: 1)));

        summary.Symbols.Should().HaveCount(2);
        summary.Symbols.Should().OnlyContain(s => s.IsAnonymous);
    }

    [Theory]
    [InlineData(3, 0, SymbolChangeKind.Added)]
    [InlineData(0, 3, SymbolChangeKind.Removed)]
    [InlineData(2, 2, SymbolChangeKind.Modified)]
    public void Build_ClassifiesBySideOfTheChange(int added, int removed, SymbolChangeKind expected)
    {
        var summary = ChangeSummaryBuilder.Build("A.cs",
            DiffOf(Hunk("@@ -1,1 +1,1 @@ void F()", 0, added, removed)));

        summary.Symbols.Single().Kind.Should().Be(expected);
    }

    [Fact]
    public void Build_SkipsHunksThatChangedNothing()
    {
        var summary = ChangeSummaryBuilder.Build("A.cs",
            DiffOf(Hunk("@@ -1,1 +1,1 @@ void F()", 0, added: 0, removed: 0)));

        summary.Symbols.Should().BeEmpty();
        summary.Added.Should().Be(0);
        summary.Removed.Should().Be(0);
    }

    [Fact]
    public void Build_TotalsTheFileFromItsSymbols()
    {
        var summary = ChangeSummaryBuilder.Build("A.cs",
            DiffOf(
                Hunk("@@ -1,1 +1,1 @@ void A()", 0, added: 4, removed: 2),
                Hunk("@@ -9,1 +9,1 @@ void B()", 1, added: 1, removed: 3)));

        summary.Added.Should().Be(5);
        summary.Removed.Should().Be(5);
        summary.Path.Should().Be("A.cs");
    }
}
