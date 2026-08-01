using System.Linq;
using FluentAssertions;
using GrumpyGit.Core.Terminal;

namespace GrumpyGit.Core.Tests.Terminal;

public class TerminalScreenTests
{
    private static string[] TextOf(TerminalScreen screen) =>
        screen.Lines.Select(l => l.Text).ToArray();

    // ── Plain text and line breaks ────────────────────────────────────────────

    [Fact]
    public void AFreshScreenHasExactlyOneEmptyRow()
    {
        var screen = new TerminalScreen();

        screen.Lines.Should().ContainSingle();
        screen.Lines[0].Text.Should().BeEmpty();
        screen.CursorColumn.Should().Be(0);
    }

    [Fact]
    public void NewlinesStartRows()
    {
        var screen = new TerminalScreen();

        screen.Write("one\ntwo\n");

        // The trailing row is the one still being written, so it is present but empty.
        TextOf(screen).Should().Equal("one", "two", "");
    }

    [Fact]
    public void CarriageReturnWithoutNewlineOverwritesInPlace()
    {
        // This is how progress output ("Receiving objects: 42%") works.
        var screen = new TerminalScreen();

        screen.Write("Receiving: 42%\rReceiving: 99%");

        TextOf(screen).Should().Equal("Receiving: 99%");
    }

    [Fact]
    public void CarriageReturnLeavesUnoverwrittenTailBehind()
    {
        // Without an explicit erase, a shorter rewrite really does leave the old tail — a
        // real terminal behaves this way and shells emit ESC[K precisely to avoid it.
        var screen = new TerminalScreen();

        screen.Write("longer text\rshort");

        TextOf(screen).Should().Equal("shortr text");
    }

    [Fact]
    public void CrLfIsASingleLineBreak()
    {
        var screen = new TerminalScreen();

        screen.Write("a\r\nb");

        TextOf(screen).Should().Equal("a", "b");
    }

    [Fact]
    public void BackspaceMovesTheCursorWithoutErasing()
    {
        var screen = new TerminalScreen();

        screen.Write("abc\b\bX");

        TextOf(screen).Should().Equal("aXc");
    }

    [Fact]
    public void TabsAreExpandedToTheNextEightColumnStop()
    {
        var screen = new TerminalScreen();

        screen.Write("ab\tc");

        TextOf(screen).Should().Equal("ab      c");
    }

    [Fact]
    public void TrailingUnstyledBlanksAreTrimmed()
    {
        // Cursor padding and erase-to-end must not leave a tail of spaces for the renderer
        // to measure or the clipboard to carry.
        var screen = new TerminalScreen();

        screen.Write("text     ");

        screen.Lines[0].Text.Should().Be("text");
    }

    // ── Chunking ──────────────────────────────────────────────────────────────

    [Fact]
    public void AnEscapeSequenceSplitAcrossWritesIsStillHonoured()
    {
        // Output arrives in whatever sizes the pipe hands over, so this is the normal case,
        // not an edge case.
        var screen = new TerminalScreen();

        screen.Write("plain\x1B[3");
        screen.Write("1mred");

        screen.Lines[0].Spans.Should().HaveCount(2);
        screen.Lines[0].Spans[1].Text.Should().Be("red");
        screen.Lines[0].Spans[1].Style.Foreground.Should().Be(TerminalColor.Palette(1));
    }

    [Fact]
    public void TextSplitAcrossWritesLandsOnOneRow()
    {
        var screen = new TerminalScreen();

        screen.Write("hel");
        screen.Write("lo");

        TextOf(screen).Should().Equal("hello");
    }

    [Fact]
    public void StyleCarriesOverBetweenWrites()
    {
        var screen = new TerminalScreen();

        screen.Write("\x1B[32mgreen");
        screen.Write(" still green");

        screen.Lines[0].Spans.Should().ContainSingle();
        screen.Lines[0].Spans[0].Text.Should().Be("green still green");
    }

    // ── Erasure and cursor movement ───────────────────────────────────────────

    [Fact]
    public void EraseToEndOfLineTruncatesAtTheCursor()
    {
        var screen = new TerminalScreen();

        screen.Write("longer text\rshort\x1B[K");

        TextOf(screen).Should().Equal("short");
    }

    [Fact]
    public void EraseWholeLineEmptiesTheRow()
    {
        var screen = new TerminalScreen();

        screen.Write("discard me\x1B[2K");

        TextOf(screen).Should().Equal("");
    }

    [Fact]
    public void CursorHorizontalAbsoluteIsOneBased()
    {
        var screen = new TerminalScreen();

        screen.Write("abcdef\x1B[3GX");

        TextOf(screen).Should().Equal("abXdef");
    }

    [Fact]
    public void CursorForwardAndBackMoveWithoutErasing()
    {
        var screen = new TerminalScreen();

        screen.Write("abcdef\x1B[6D\x1B[2CX");

        TextOf(screen).Should().Equal("abXdef");
    }

    [Fact]
    public void CursorMovementPastTheEndPadsWithBlanks()
    {
        var screen = new TerminalScreen();

        screen.Write("ab\x1B[6GX");

        TextOf(screen).Should().Equal("ab   X");
    }

    [Fact]
    public void EraseCharactersBlanksWithoutMovingTheCursor()
    {
        var screen = new TerminalScreen();

        screen.Write("abcdef\x1B[1G\x1B[3X");

        TextOf(screen).Should().Equal("   def");
    }

    [Fact]
    public void DeleteCharactersPullsTheRestOfTheRowLeft()
    {
        var screen = new TerminalScreen();

        screen.Write("abcdef\x1B[1G\x1B[2P");

        TextOf(screen).Should().Equal("cdef");
    }

    [Fact]
    public void InsertBlanksPushesTheRestOfTheRowRight()
    {
        var screen = new TerminalScreen();

        screen.Write("abc\x1B[1G\x1B[2@");

        TextOf(screen).Should().Equal("  abc");
    }

    [Fact]
    public void PromptRedrawIsReproducedFaithfully()
    {
        // The exact shape PSReadLine uses to repaint the input line as you type: return to
        // column 0, rewrite, erase whatever is left over.
        var screen = new TerminalScreen();

        screen.Write("PS C:\\repo> git stat");
        screen.Write("\rPS C:\\repo> git status\x1B[K");

        TextOf(screen).Should().Equal("PS C:\\repo> git status");
    }

    // ── Sequences we deliberately drop ────────────────────────────────────────

    [Fact]
    public void OperatingSystemCommandsAreSwallowedWhole()
    {
        // Window titles arrive constantly and must never reach the transcript.
        var screen = new TerminalScreen();

        screen.Write("\x1B]0;C:\\repo\x07ready");

        TextOf(screen).Should().Equal("ready");
    }

    [Fact]
    public void OperatingSystemCommandsTerminatedByStringTerminatorAreAlsoSwallowed()
    {
        var screen = new TerminalScreen();

        screen.Write("\x1B]2;title\x1B\\ready");

        TextOf(screen).Should().Equal("ready");
    }

    [Fact]
    public void PrivateModeSequencesAreIgnored()
    {
        var screen = new TerminalScreen();

        screen.Write("\x1B[?25lhidden cursor\x1B[?25h");

        TextOf(screen).Should().Equal("hidden cursor");
    }

    [Fact]
    public void VerticalCursorMovementReopensTheEarlierRow()
    {
        // Superseded behaviour: this used to ignore row movement and append, on the
        // grounds that there was no grid to move into. That is what made a redrawn prompt
        // duplicate itself, so rows are now reopened and overwritten in place.
        //
        // The cursor keeps its COLUMN across a vertical move, as it does on a real grid.
        // "second" leaves it at column 6, and row 0 is only five wide, so moving up pads
        // the gap and the X lands at column 6 — "first X", not "firstX".
        var screen = new TerminalScreen();

        screen.Write("first\nsecond\x1B[1AX");

        TextOf(screen).Should().Equal("first X", "second");
    }

    [Fact]
    public void CharsetDesignatorsConsumeTheirArgument()
    {
        var screen = new TerminalScreen();

        screen.Write("\x1B(Btext");

        TextOf(screen).Should().Equal("text");
    }

    [Fact]
    public void UnprintableControlBytesAreDropped()
    {
        var screen = new TerminalScreen();

        screen.Write("a\a\0b");

        TextOf(screen).Should().Equal("ab");
    }

    // ── Clearing and scrollback ───────────────────────────────────────────────

    [Fact]
    public void ClearLeavesASingleEmptyRow()
    {
        var screen = new TerminalScreen();
        screen.Write("one\ntwo\nthree");

        screen.Clear();

        screen.Lines.Should().ContainSingle();
        screen.Lines[0].Text.Should().BeEmpty();
        screen.CursorColumn.Should().Be(0);
    }

    [Fact]
    public void EraseInDisplayTwoClearsTheWholeScrollback()
    {
        var screen = new TerminalScreen();
        screen.Write("one\ntwo\n");

        screen.Write("\x1B[2J");

        screen.Lines.Should().ContainSingle();
        screen.Lines[0].Text.Should().BeEmpty();
    }

    [Fact]
    public void ClearCountsTheRowsItDiscarded()
    {
        var screen = new TerminalScreen();
        screen.Write("one\ntwo\n");

        screen.Clear();

        // Three rows existed: the two finished ones and the one being written.
        screen.DroppedLineCount.Should().Be(3);
    }

    [Fact]
    public void ScrollbackIsBoundedAndTheOldestRowsGoFirst()
    {
        var screen = new TerminalScreen(maxScrollbackLines: 10);

        for (var i = 0; i < 2000; i++)
            screen.Write($"line {i}\n");

        screen.Lines.Count.Should().BeLessThan(400, "the buffer must not grow without bound");
        screen.Lines[^2].Text.Should().Be("line 1999");
        screen.DroppedLineCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void DroppedLineCountMatchesTheRowsActuallyRemoved()
    {
        var screen = new TerminalScreen(maxScrollbackLines: 10);

        for (var i = 0; i < 2000; i++)
            screen.Write($"line {i}\n");

        // 2000 newlines produce 2001 rows in total; whatever is no longer held was dropped.
        (screen.DroppedLineCount + screen.Lines.Count).Should().Be(2001);
    }

    // ── Plain-text extraction ─────────────────────────────────────────────────

    [Fact]
    public void GetTextReturnsTheTranscriptWithoutStyling()
    {
        var screen = new TerminalScreen();

        screen.Write("\x1B[31mone\x1B[0m\ntwo");

        screen.GetText().Should().Be("one\ntwo");
    }
}
