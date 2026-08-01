using FluentAssertions;
using GrumpyGit.Core.Git;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests;

/// <summary>
/// Partial-hunk patches must treat UNSELECTED lines differently depending on which
/// direction they will be applied, because git verifies the patch against the side it
/// starts from. Getting this wrong produces a patch whose context does not match the
/// index — which git would normally reject, but silently mis-applies when the context
/// check is disabled.
/// </summary>
public class PatchBuilderReverseTests
{
    private static readonly string[] Header =
    [
        "diff --git a/a.txt b/a.txt",
        "index 1111111..2222222 100644",
        "--- a/a.txt",
        "+++ b/a.txt",
    ];

    /// <summary>context / -removedA / -removedB / +addedA / +addedB / context</summary>
    private static DiffHunk Hunk() => new()
    {
        OldStart = 1,
        NewStart = 1,
        HeaderLine = "@@ -1,4 +1,4 @@",
        Lines =
        [
            new DiffLine { Type = DiffLineType.Context, Content = "top" },
            new DiffLine { Type = DiffLineType.Removed, Content = "removedA" },
            new DiffLine { Type = DiffLineType.Removed, Content = "removedB" },
            new DiffLine { Type = DiffLineType.Added,   Content = "addedA" },
            new DiffLine { Type = DiffLineType.Added,   Content = "addedB" },
            new DiffLine { Type = DiffLineType.Context, Content = "bottom" },
        ],
    };

    private static string[] BodyLines(string patch) =>
        patch.Split('\n')
             .Where(l => l.Length > 0
                         && !l.StartsWith("diff ") && !l.StartsWith("index ")
                         && !l.StartsWith("--- ") && !l.StartsWith("+++ ")
                         && !l.StartsWith("@@"))
             .ToArray();

    [Fact]
    public void Forward_UnselectedRemoved_BecomesContext_UnselectedAdded_IsOmitted()
    {
        // Select only index 3 (addedA).
        var patch = PatchBuilder.BuildFromSelectedLines(Header, Hunk(), new HashSet<int> { 3 });

        var body = BodyLines(patch);

        // The pre-image is the worktree file: both removed lines are still present.
        body.Should().Contain(" removedA");
        body.Should().Contain(" removedB");
        body.Should().Contain("+addedA");
        // addedB is not in the pre-image and was not selected — it must not appear.
        body.Should().NotContain(l => l.Contains("addedB"));
    }

    [Fact]
    public void Reverse_UnselectedAdded_BecomesContext_UnselectedRemoved_IsOmitted()
    {
        // Unstaging addedA only.
        var patch = PatchBuilder.BuildFromSelectedLines(
            Header, Hunk(), new HashSet<int> { 3 }, forReverseApply: true);

        var body = BodyLines(patch);

        body.Should().Contain("+addedA", "the selected line is the one being unstaged");

        // addedB IS in the index, so it has to be preserved as context.
        body.Should().Contain(" addedB");

        // The removed lines are NOT in the index — including them as context would make
        // git look for text that isn't there.
        body.Should().NotContain(l => l.Contains("removedA"));
        body.Should().NotContain(l => l.Contains("removedB"));
    }

    [Fact]
    public void Reverse_And_Forward_ProduceDifferentPatches_ForTheSameSelection()
    {
        var selection = new HashSet<int> { 3 };

        var forward = PatchBuilder.BuildFromSelectedLines(Header, Hunk(), selection);
        var reverse = PatchBuilder.BuildFromSelectedLines(Header, Hunk(), selection, forReverseApply: true);

        reverse.Should().NotBe(forward,
            "using the forward patch for a reverse apply was the bug being fixed");
    }

    [Fact]
    public void Reverse_RecalculatedCounts_MatchTheEmittedBody()
    {
        var patch = PatchBuilder.BuildFromSelectedLines(
            Header, Hunk(), new HashSet<int> { 3 }, forReverseApply: true);

        var body = BodyLines(patch);
        var expectedOld = body.Count(l => l[0] == ' ' || l[0] == '-');
        var expectedNew = body.Count(l => l[0] == ' ' || l[0] == '+');

        // git rejects a hunk whose declared counts disagree with its body.
        patch.Should().Contain($"@@ -1,{expectedOld} +1,{expectedNew} @@");
    }

    [Fact]
    public void SelectingNothing_ProducesNoPatch_InEitherDirection()
    {
        PatchBuilder.BuildFromSelectedLines(Header, Hunk(), new HashSet<int>())
            .Should().BeEmpty();
        PatchBuilder.BuildFromSelectedLines(Header, Hunk(), new HashSet<int>(), forReverseApply: true)
            .Should().BeEmpty();
    }

    [Fact]
    public void DefaultOverload_KeepsForwardBehaviour()
    {
        var selection = new HashSet<int> { 1 };

        PatchBuilder.BuildFromSelectedLines(Header, Hunk(), selection)
            .Should().Be(PatchBuilder.BuildFromSelectedLines(Header, Hunk(), selection, forReverseApply: false));
    }
}
