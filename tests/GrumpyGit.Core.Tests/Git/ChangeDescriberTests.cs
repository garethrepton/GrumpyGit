using FluentAssertions;
using GrumpyGit.Core.Git;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests.Git;

public class ChangeDescriberTests
{
    private static List<DiffLine> Lines(string[] added, string[] removed)
    {
        var list = new List<DiffLine>();
        foreach (var a in added) list.Add(new DiffLine { Type = DiffLineType.Added, Content = a });
        foreach (var r in removed) list.Add(new DiffLine { Type = DiffLineType.Removed, Content = r });
        return list;
    }

    [Fact]
    public void NewDeclaration_IsCalledNew()
    {
        var description = ChangeDescriber.Describe(
            "private void Helper()",
            Lines(["private void Helper()", "{", "    Run();", "}"], []));

        description.Should().StartWith("new");
    }

    [Fact]
    public void DeletedDeclaration_IsCalledRemoved()
    {
        var description = ChangeDescriber.Describe(
            "private void Helper()",
            Lines([], ["private void Helper()", "{", "}"]));

        description.Should().StartWith("removed");
    }

    [Fact]
    public void PureInsertionIntoAnExistingSymbol_CountsLines()
    {
        ChangeDescriber.Describe("void F()", Lines(["    a();", "    b();"], []))
            .Should().Be("2 lines added");
    }

    [Fact]
    public void SingleLine_IsNotPluralised()
    {
        ChangeDescriber.Describe("void F()", Lines(["    a();"], []))
            .Should().Be("1 line added");
    }

    [Fact]
    public void ReindentedCode_IsWhitespaceOnly()
    {
        ChangeDescriber.Describe("void F()", Lines(["        a();"], ["    a();"]))
            .Should().Be("whitespace only");
    }

    [Fact]
    public void CommentChanges_AreCalledOut()
    {
        ChangeDescriber.Describe("void F()", Lines(["// now explains why", "// second line"], []))
            .Should().Be("comments added");

        ChangeDescriber.Describe("void F()", Lines(["// new wording"], ["// old wording"]))
            .Should().Be("comments reworded");
    }

    [Fact]
    public void EditedLine_IsReportedAsReworkedRatherThanAddPlusRemove()
    {
        // Same statement with one argument changed — one edit, not two.
        var description = ChangeDescriber.Describe(
            "void F()",
            Lines(["    args.Add(\"push\").Add(\"--follow-tags\");"],
                  ["    args.Add(\"push\");"]));

        description.Should().Contain("reworked 1 line");
        // Not counted as a separate insertion — the line was edited, not added.
        description.Should().NotContain("1 added");
        // And the added argument is noticed as growth of the existing call.
        description.Should().Contain("extended in place");
    }

    [Fact]
    public void NotableAdditions_AreCalledOut()
    {
        ChangeDescriber.Describe("void F()", Lines(["    throw new InvalidOperationException();"], []))
            .Should().Contain("throws added");

        ChangeDescriber.Describe("void F()", Lines(["    if (x == null) { }"], []))
            .Should().Contain("null check added");

        ChangeDescriber.Describe("void F()", Lines(["    // TODO: revisit"], ["    // old"]))
            .Should().Contain("comments");
    }

    [Fact]
    public void SignatureEdit_IsTheHeadlineNote()
    {
        var description = ChangeDescriber.Describe(
            "public async Task PushAsync(string repo, string branch)",
            Lines(["public async Task PushAsync(string repo, string branch)"],
                  ["public async Task PushAsync(string repo)"]));

        description.Should().Contain("signature changed");
    }

    [Fact]
    public void SignatureNote_NamesTheParameterThatChanged()
    {
        // Naming the parameter is the point: a changed parameter list is what breaks
        // callers, and knowing which one saves opening the file.
        var description = ChangeDescriber.Describe(
            "public Task PushAsync(string repo, string branch)",
            Lines(["public Task PushAsync(string repo, string branch)"],
                  ["public Task PushAsync(string repo)"]));

        description.Should().Contain("+branch");
    }

    [Fact]
    public void SignatureNote_NamesARemovedParameter()
    {
        var description = ChangeDescriber.Describe(
            "void F(int a)",
            Lines(["void F(int a)"], ["void F(int a, int b)"]));

        description.Should().Contain("−b");
    }

    [Fact]
    public void ParameterNames_SurviveGenericsAndDefaults()
    {
        // The comma inside Dictionary<,> must not split one parameter into two, and the
        // default value must not be mistaken for the name.
        var description = ChangeDescriber.Describe(
            "void F(Dictionary<string, int> map, int count = 3, CancellationToken ct)",
            Lines(["void F(Dictionary<string, int> map, int count = 3, CancellationToken ct)"],
                  ["void F(Dictionary<string, int> map, int count = 3)"]));

        description.Should().Contain("+ct");
        description.Should().NotContain("+3");
        description.Should().NotContain("+int");
    }

    [Fact]
    public void SwappedIdentifier_IsNamedOnBothSides()
    {
        var description = ChangeDescriber.Describe(
            "void F()",
            Lines(["    var cmd = GitProcess.StartForDiff();"],
                  ["    var cmd = GitProcess.Start();"]));

        description.Should().Contain("Start → StartForDiff");
    }

    [Fact]
    public void VisibilityChange_IsReported()
    {
        var description = ChangeDescriber.Describe(
            "private void Helper()",
            Lines(["private void Helper()"], ["public void Helper()"]));

        description.Should().Contain("public → private");
    }

    [Fact]
    public void EditedCondition_IsReported()
    {
        var description = ChangeDescriber.Describe(
            "void F()",
            Lines(["    if (a && b && c)"], ["    if (a && b)"]));

        description.Should().Contain("condition changed");
    }

    [Fact]
    public void AnEditMadeEntirelyOfImports_SaysSo()
    {
        ChangeDescriber.Describe("", Lines(["using System.IO;", "using System.Linq;"], []))
            .Should().Contain("imports added");
    }

    [Fact]
    public void OneImportAmongRealCode_IsNotAnImportChange()
    {
        // Requiring every line to match keeps the note honest.
        ChangeDescriber.Describe("void F()", Lines(["using System;", "    DoWork();"], []))
            .Should().NotContain("imports");
    }

    [Fact]
    public void NotesAreCappedSoTheLineStaysScannable()
    {
        // Five triggers, at most two reported.
        var description = ChangeDescriber.Describe("void F()", Lines([
            "    if (x is null) throw new Exception();",
            "    try {",
            "    catch (Exception e) { }",
            "    // TODO fix",
            "    await Task.Delay(1);",
        ], []));

        description.Split('·').Should().HaveCountLessThanOrEqualTo(3);
    }

    [Fact]
    public void UnrelatedReplacement_IsNotClaimedAsARework()
    {
        // No token overlap: reporting these as one edited line would invent a
        // relationship, so they stay a separate addition and deletion.
        var description = ChangeDescriber.Describe(
            "void F()",
            Lines(["    completelyDifferent(alpha, beta);"], ["    xyz();"]));

        description.Should().NotContain("reworked");
        description.Should().Contain("1 added");
        description.Should().Contain("1 removed");
    }

    [Fact]
    public void AddedEarlyExit_IsFlaggedAsAGuard()
    {
        ChangeDescriber.Describe("void F()", Lines(["    if (x is null) return;"], []))
            .Should().Contain("guard added");
    }

    [Fact]
    public void NoChangedLines_SaysSo()
    {
        ChangeDescriber.Describe("void F()", []).Should().Be("no change");
    }

    [Fact]
    public void MentioningASymbolIsNotDeclaringIt()
    {
        // The call site references Helper but does not declare it, so this must not be
        // reported as a new declaration.
        var description = ChangeDescriber.Describe(
            "private void Caller()",
            Lines(["    Helper();"], []));

        description.Should().Be("1 line added");
    }
}
