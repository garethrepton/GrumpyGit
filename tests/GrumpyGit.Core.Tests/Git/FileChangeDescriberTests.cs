using FluentAssertions;
using GrumpyGit.Core.Git;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests.Git;

/// <summary>
/// The file-level description. Every assertion here is about a statement that is true of
/// the diff — the describer is allowed to say less, never to say something it cannot know.
/// </summary>
public class FileChangeDescriberTests
{
    private static ParsedDiff Diff(int hunks = 1, params string[] headerLines)
    {
        var list = Enumerable.Range(0, hunks)
            .Select(i => new DiffHunk { Index = i, HeaderLine = $"@@ hunk {i} @@" })
            .ToList();

        return new ParsedDiff("old", "new", [], [], [], hunks: list, fileHeaderLines: headerLines);
    }

    private static SymbolChange Symbol(string name, SymbolChangeKind kind, int added = 1, int removed = 0) =>
        new(name, added, removed, 1, 1, kind, "description");

    [Fact]
    public void ANewFileIsNamedAsOne()
    {
        var summary = new FileChangeSummary("a.cs", 120, 0, [Symbol("void A()", SymbolChangeKind.Added)]);

        var text = FileChangeDescriber.Describe(summary, Diff(headerLines: "new file mode 100644"));

        text.Should().Be("New file — 120 lines.");
    }

    [Fact]
    public void ADeletedFileIsNamedAsOne()
    {
        var summary = new FileChangeSummary("a.cs", 0, 40, []);

        var text = FileChangeDescriber.Describe(summary, Diff(headerLines: "deleted file mode 100644"));

        text.Should().Be("File deleted — 40 lines gone.");
    }

    [Fact]
    public void ARenameIsStatedBeforeTheContentChange()
    {
        var summary = new FileChangeSummary("b.cs", 2, 1, [Symbol("void A()", SymbolChangeKind.Modified, 2, 1)]);

        var text = FileChangeDescriber.Describe(summary, Diff(1, "rename from a.cs", "rename to b.cs"));

        text.Should().StartWith("Renamed to b.cs.");
        text.Should().Contain("+2 −1");
    }

    [Fact]
    public void APureRenameSaysOnlyThat()
    {
        var summary = new FileChangeSummary("b.cs", 0, 0, []);

        var text = FileChangeDescriber.Describe(summary, Diff(1, "rename from a.cs", "rename to b.cs"));

        text.Should().Be("Renamed to b.cs.");
    }

    [Fact]
    public void WithNoSymbolNamesItCountsRatherThanInventsStructure()
    {
        // No language driver for this file type — there is nothing truthful to name.
        var summary = new FileChangeSummary("notes.txt", 6, 2, []);

        var text = FileChangeDescriber.Describe(summary, Diff(hunks: 3));

        text.Should().Be("+6 −2 across 3 hunks.");
    }

    [Fact]
    public void OneSymbolIsNamedOutright()
    {
        var summary = new FileChangeSummary("a.cs", 4, 2,
            [Symbol("private void Guard(int x)", SymbolChangeKind.Modified, 4, 2)]);

        var text = FileChangeDescriber.Describe(summary, Diff());

        text.Should().Be("Reworks Guard(). +4 −2.");
    }

    [Fact]
    public void TwoSymbolsAreBothNamed()
    {
        var summary = new FileChangeSummary("a.cs", 4, 2,
        [
            Symbol("void Guard()", SymbolChangeKind.Modified, 2, 1),
            Symbol("void Close()", SymbolChangeKind.Modified, 2, 1),
        ]);

        var text = FileChangeDescriber.Describe(summary, Diff());

        text.Should().Be("Reworks Guard() and Close(). +4 −2.");
    }

    [Fact]
    public void ManySymbolsAreSummarisedRatherThanListed()
    {
        var summary = new FileChangeSummary("a.cs", 10, 5,
        [
            Symbol("void A()", SymbolChangeKind.Modified),
            Symbol("void B()", SymbolChangeKind.Modified),
            Symbol("void C()", SymbolChangeKind.Modified),
            Symbol("void D()", SymbolChangeKind.Modified),
        ]);

        var text = FileChangeDescriber.Describe(summary, Diff());

        text.Should().Be("Reworks A() and B() and 2 more. +10 −5.");
    }

    [Fact]
    public void MixedKindsTakeTheNeutralVerb()
    {
        var summary = new FileChangeSummary("a.cs", 5, 3,
        [
            Symbol("void A()", SymbolChangeKind.Added),
            Symbol("void B()", SymbolChangeKind.Removed),
        ]);

        var text = FileChangeDescriber.Describe(summary, Diff());

        text.Should().StartWith("Changes ");
    }

    [Fact]
    public void AddedOnlySymbolsSayAdds()
    {
        var summary = new FileChangeSummary("a.cs", 12, 0, [Symbol("void A()", SymbolChangeKind.Added, 12)]);

        FileChangeDescriber.Describe(summary, Diff()).Should().StartWith("Adds A().");
    }

    [Fact]
    public void RemovedOnlySymbolsSayRemoves()
    {
        var summary = new FileChangeSummary("a.cs", 0, 12,
            [Symbol("void A()", SymbolChangeKind.Removed, 0, 12)]);

        FileChangeDescriber.Describe(summary, Diff()).Should().StartWith("Removes A().");
    }

    [Fact]
    public void AnonymousHunksDoNotBecomeNames()
    {
        var summary = new FileChangeSummary("a.cs", 3, 1,
        [
            new SymbolChange("", 3, 1, 1, 1, SymbolChangeKind.Modified, "reworked"),
        ]);

        var text = FileChangeDescriber.Describe(summary, Diff(hunks: 2));

        text.Should().Be("+3 −1 across 2 hunks.");
    }

    [Fact]
    public void AFileWithNoChangeSaysSo()
    {
        var summary = new FileChangeSummary("a.cs", 0, 0, []);

        FileChangeDescriber.Describe(summary, Diff()).Should().Be("No content change.");
    }

    [Fact]
    public void SingularCountsReadAsSingular()
    {
        var summary = new FileChangeSummary("a.cs", 1, 0, [Symbol("void A()", SymbolChangeKind.Added)]);

        FileChangeDescriber.Describe(summary, Diff(headerLines: "new file mode 100644"))
            .Should().Be("New file — 1 line.");
    }
}
