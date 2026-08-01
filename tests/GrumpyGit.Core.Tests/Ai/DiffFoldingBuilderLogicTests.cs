using FluentAssertions;

namespace GrumpyGit.Core.Tests.Ai;

/// <summary>
/// The folding *selection rule* — which line runs are foldable — mirrored here so it
/// can be tested without an Avalonia TextDocument. The production implementation in
/// DiffFoldingBuilder applies the same thresholds against a real document.
/// </summary>
public class DiffFoldingBuilderLogicTests
{
    private const int KeptContext = 3;
    private const int MinimumRun = 6;

    /// <summary>Returns the (first,last) line ranges that would be folded.</summary>
    private static List<(int First, int Last)> FoldableRuns(int lineCount, HashSet<int> changed)
    {
        var runs = new List<(int, int)>();
        var start = -1;

        for (var line = 1; line <= lineCount + 1; line++)
        {
            var nearChange = false;
            for (var d = -KeptContext; d <= KeptContext && !nearChange; d++)
                if (changed.Contains(line + d)) nearChange = true;

            var mustShow = line > lineCount || nearChange;

            if (!mustShow)
            {
                if (start < 0) start = line;
                continue;
            }

            if (start >= 0)
            {
                if (line - start >= MinimumRun) runs.Add((start, line - 1));
                start = -1;
            }
        }

        return runs;
    }

    [Fact]
    public void FileWithNoChanges_FoldsEverythingAsOneRun()
    {
        var runs = FoldableRuns(100, []);

        var run = runs.Should().ContainSingle().Subject;
        run.First.Should().Be(1);
        run.Last.Should().Be(100);
    }

    [Fact]
    public void ChangeInTheMiddle_LeavesContextVisibleOnBothSides()
    {
        // 100 lines, one change at line 50.
        var runs = FoldableRuns(100, [50]);

        runs.Should().HaveCount(2);
        // Context of 3 means lines 47-53 stay visible.
        runs[0].Last.Should().Be(46);
        runs[1].First.Should().Be(54);
    }

    [Fact]
    public void ShortGapBetweenChanges_IsNotFolded()
    {
        // Changes at 20 and 30. Visible: 17-23 and 27-33, leaving only 24-26 between —
        // three lines, below the minimum, so folding it would cost more than it saves.
        var runs = FoldableRuns(100, [20, 30]);

        runs.Should().NotContain(r => r.First == 24);
    }

    [Fact]
    public void LargeGapBetweenChanges_IsFolded()
    {
        var runs = FoldableRuns(200, [20, 120]);

        runs.Should().Contain(r => r.First == 24 && r.Last == 116);
    }

    [Fact]
    public void ChangeAtStartOfFile_DoesNotProduceALeadingFold()
    {
        var runs = FoldableRuns(100, [1]);

        runs.Should().NotContain(r => r.First == 1);
        runs.Should().ContainSingle().Which.First.Should().Be(5);
    }

    [Fact]
    public void EveryLineChanged_ProducesNoFolds()
    {
        var changed = new HashSet<int>(Enumerable.Range(1, 50));

        FoldableRuns(50, changed).Should().BeEmpty();
    }
}
