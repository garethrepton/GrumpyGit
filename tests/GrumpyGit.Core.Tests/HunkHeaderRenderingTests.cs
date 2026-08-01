using FluentAssertions;
using GrumpyGit.Core.Git;

namespace GrumpyGit.Core.Tests;

/// <summary>
/// The "@@ -a,b +c,d @@" text is suppressed from the rendered side-by-side view, but
/// the header must survive on the hunk model because patches are rebuilt from it.
/// </summary>
public class HunkHeaderRenderingTests
{
    private const string Diff =
        "diff --git a/a.txt b/a.txt\n" +
        "index 1111111..2222222 100644\n" +
        "--- a/a.txt\n" +
        "+++ b/a.txt\n" +
        "@@ -3,19 +3,14 @@\n" +
        " context\n" +
        "-removed\n" +
        "+added\n" +
        " tail\n";

    [Fact]
    public void RenderedText_DoesNotContainTheRawHunkHeader()
    {
        var parsed = UnifiedDiffParser.Parse(Diff);

        parsed.LeftText.Should().NotContain("@@");
        parsed.RightText.Should().NotContain("@@");
    }

    [Fact]
    public void HunkHeaderRow_IsStillEmitted_AsAnAnchorAndSeparator()
    {
        var parsed = UnifiedDiffParser.Parse(Diff);

        // The row survives so the hunk staging button has somewhere to sit and the
        // tinted separator band still renders — it is just blank.
        parsed.HunkHeaderLines.Should().ContainSingle();

        var headerLineIndex = parsed.HunkHeaderLines[0] - 1;
        var lines = parsed.LeftText.Split('\n');
        lines[headerLineIndex].Should().BeEmpty();
    }

    [Fact]
    public void HunkModel_StillCarriesTheRealHeader_ForPatchConstruction()
    {
        var parsed = UnifiedDiffParser.Parse(Diff);

        var hunk = parsed.Hunks.Should().ContainSingle().Subject;
        hunk.HeaderLine.Should().StartWith("@@ -3,19 +3,14 @@");
        hunk.OldStart.Should().Be(3);
        hunk.NewStart.Should().Be(3);
    }

    [Fact]
    public void ContentLines_AreUnaffected()
    {
        var parsed = UnifiedDiffParser.Parse(Diff);

        parsed.LeftText.Should().Contain("removed");
        parsed.RightText.Should().Contain("added");
        parsed.LeftText.Should().Contain("context");
    }

    [Fact]
    public void BuiltPatch_StillContainsAHunkHeader()
    {
        var parsed = UnifiedDiffParser.Parse(Diff);

        var patch = PatchBuilder.BuildFromHunks(parsed.FileHeaderLines, [parsed.Hunks[0]]);

        patch.Should().Contain("@@ -3,19 +3,14 @@",
            "hiding the header in the viewer must not strip it from the patch");
    }
}
