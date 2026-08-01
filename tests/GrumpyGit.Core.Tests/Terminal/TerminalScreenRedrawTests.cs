using FluentAssertions;
using GrumpyGit.Core.Terminal;

namespace GrumpyGit.Core.Tests.Terminal;

/// <summary>
/// The redraw vocabulary PSReadLine actually uses. Every case here produced duplicated or
/// stranded text while row movement was discarded, which is what made the terminal read as
/// flaky rather than broken — plain output was fine, editing a command was not.
/// </summary>
public class TerminalScreenRedrawTests
{
    private static string[] Rows(TerminalScreen screen) =>
        screen.Lines.Select(l => l.Text).ToArray();

    [Fact]
    public void CursorUp_RewritesTheEarlierRowInsteadOfAppending()
    {
        var screen = new TerminalScreen();
        screen.Write("first\r\nsecond\r\n");

        // Up two rows, to column 0, and overwrite.
        screen.Write("\x1B[2A\rFIRST");

        Rows(screen).Should().StartWith(["FIRST", "second"]);
        Rows(screen).Should().NotContain(r => r == "FIRST" && Rows(screen).Count(x => x == "FIRST") > 1);
    }

    [Fact]
    public void CursorDown_ReturnsToAnExistingRowRatherThanCreatingOne()
    {
        var screen = new TerminalScreen();
        screen.Write("one\r\ntwo\r\nthree");

        screen.Write("\x1B[2A");   // up to "one"
        screen.Write("\x1B[1B");   // back down to "two"
        screen.Write("\rTWO");

        Rows(screen).Should().Equal("one", "TWO", "three");
    }

    [Fact]
    public void NewlineAfterMovingUp_StepsOntoTheExistingRow()
    {
        var screen = new TerminalScreen();
        screen.Write("alpha\r\nbeta\r\n");

        screen.Write("\x1B[2A");    // back to "alpha"
        screen.Write("\r\n");       // newline must land on "beta", not push a copy below
        screen.Write("\rBETA");

        Rows(screen).Should().StartWith(["alpha", "BETA"]);
    }

    [Fact]
    public void EraseDown_DropsTheRowsBelowTheCursor()
    {
        // The shrinking-command case: a long multi-line entry replaced by a short one.
        var screen = new TerminalScreen();
        screen.Write("prompt> long\r\ncontinued\r\nmore\r\n");

        screen.Write("\x1B[3A");    // back to the first row
        screen.Write("\r\x1B[0J");  // column 0, erase to end of display

        Rows(screen).Should().Equal("");
    }

    [Fact]
    public void EraseToEndOfLine_LeavesRowsBelowAlone()
    {
        var screen = new TerminalScreen();
        screen.Write("aaa\r\nbbb");

        screen.Write("\x1B[1A\r\x1B[0K");

        Rows(screen).Should().Equal("", "bbb");
    }

    [Fact]
    public void MovingBelowTheLastRow_CreatesIt()
    {
        var screen = new TerminalScreen();
        screen.Write("only");

        screen.Write("\x1B[2B\rdeeper");

        Rows(screen).Should().Equal("only", "", "deeper");
    }

    [Fact]
    public void CursorUpAtTheTop_StopsRatherThanGoingNegative()
    {
        var screen = new TerminalScreen();
        screen.Write("first line");

        screen.Write("\x1B[99A\rX");

        Rows(screen).Should().ContainSingle().Which.Should().StartWith("X");
    }

    [Fact]
    public void StyleSurvivesAReopenedRow()
    {
        // Reopening a row rebuilds its cells from materialised spans; the colour of text
        // the cursor moved back over has to survive that round trip.
        var screen = new TerminalScreen();
        screen.Write("\x1B[31mred\x1B[0m\r\nsecond");

        // Column 4 is one past "red", so the "!" appends rather than overwriting.
        screen.Write("\x1B[1A\x1B[4G!");

        var first = screen.Lines[0];
        first.Text.Should().Be("red!");
        first.Spans.Should().Contain(s => s.Text == "red" && !s.Style.Foreground.IsDefault);
    }

    [Fact]
    public void RowCursorSurvivesScrollbackTrimming()
    {
        // The row index is absolute, so trimming from the front must slide it. If it did
        // not, a redraw after a trim would overwrite unrelated scrollback.
        var screen = new TerminalScreen(maxScrollbackLines: 8);

        for (var i = 0; i < 400; i++)
            screen.Write($"line {i}\r\n");

        screen.Write("\x1B[1A\rEDITED");

        screen.Lines.Should().HaveCountLessThanOrEqualTo(8 + 256);
        screen.Lines.Count(l => l.Text.StartsWith("EDITED")).Should().Be(1);
    }
}
