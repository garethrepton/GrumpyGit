using FluentAssertions;
using GrumpyGit.Core.Terminal;

namespace GrumpyGit.Core.Tests.Terminal;

public class AnsiSgrParserTests
{
    // ── Emphasis ──────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyParameters_MeanReset()
    {
        // ESC[m is the documented shorthand for ESC[0m and shells emit it constantly.
        var bold = AnsiSgrParser.Apply(TerminalStyle.Default, "1");

        AnsiSgrParser.Apply(bold, "").Should().Be(TerminalStyle.Default);
    }

    [Fact]
    public void ZeroResetsEverything()
    {
        var styled = AnsiSgrParser.Apply(TerminalStyle.Default, "1;4;7;31;44");

        AnsiSgrParser.Apply(styled, "0").Should().Be(TerminalStyle.Default);
    }

    [Fact]
    public void AttributesAccumulateAcrossSequences()
    {
        // SGR is stateful: "bold" says nothing about colour and must not clear it.
        var style = AnsiSgrParser.Apply(TerminalStyle.Default, "31");
        style = AnsiSgrParser.Apply(style, "1");

        style.Bold.Should().BeTrue();
        style.Foreground.Should().Be(TerminalColor.Palette(1));
    }

    [Theory]
    [InlineData("22")]
    [InlineData("21")]   // "double underline" in the spec, "bold off" in every real terminal
    public void BoldIsTurnedOffWithoutDisturbingColour(string offCode)
    {
        var style = AnsiSgrParser.Apply(TerminalStyle.Default, "1;32");

        style = AnsiSgrParser.Apply(style, offCode);

        style.Bold.Should().BeFalse();
        style.Foreground.Should().Be(TerminalColor.Palette(2));
    }

    [Fact]
    public void UnderlineAndInverseToggleIndependently()
    {
        var style = AnsiSgrParser.Apply(TerminalStyle.Default, "4;7");
        style.Underline.Should().BeTrue();
        style.Inverse.Should().BeTrue();

        style = AnsiSgrParser.Apply(style, "24");
        style.Underline.Should().BeFalse();
        style.Inverse.Should().BeTrue();

        style = AnsiSgrParser.Apply(style, "27");
        style.Inverse.Should().BeFalse();
    }

    // ── The 16 base colours ───────────────────────────────────────────────────

    [Theory]
    [InlineData(30, 0)]
    [InlineData(31, 1)]
    [InlineData(32, 2)]
    [InlineData(33, 3)]
    [InlineData(34, 4)]
    [InlineData(35, 5)]
    [InlineData(36, 6)]
    [InlineData(37, 7)]
    public void BaseForegroundCodesMapToTheFirstEightSlots(int code, int slot)
    {
        AnsiSgrParser.Apply(TerminalStyle.Default, code.ToString())
            .Foreground.Should().Be(TerminalColor.Palette(slot));
    }

    [Theory]
    [InlineData(90, 8)]
    [InlineData(97, 15)]
    public void BrightForegroundCodesMapToTheUpperEightSlots(int code, int slot)
    {
        AnsiSgrParser.Apply(TerminalStyle.Default, code.ToString())
            .Foreground.Should().Be(TerminalColor.Palette(slot));
    }

    [Theory]
    [InlineData(40, 0)]
    [InlineData(47, 7)]
    [InlineData(100, 8)]
    [InlineData(107, 15)]
    public void BackgroundCodesMapToTheSameSlots(int code, int slot)
    {
        AnsiSgrParser.Apply(TerminalStyle.Default, code.ToString())
            .Background.Should().Be(TerminalColor.Palette(slot));
    }

    [Fact]
    public void ThirtyNineAndFortyNineReturnToTheDefaultColours()
    {
        var style = AnsiSgrParser.Apply(TerminalStyle.Default, "31;44");

        style = AnsiSgrParser.Apply(style, "39;49");

        style.Foreground.IsDefault.Should().BeTrue();
        style.Background.IsDefault.Should().BeTrue();
    }

    [Fact]
    public void BoldPromotesABaseColourToItsBrightVariant()
    {
        // Every terminal since the VT220 does this, and prompts rely on it for contrast.
        var style = AnsiSgrParser.Apply(TerminalStyle.Default, "1;34");

        style.Foreground.Should().Be(TerminalColor.Palette(4), "the raw code is preserved");
        style.EffectiveForeground.Should().Be(TerminalColor.Palette(12));
    }

    [Fact]
    public void BoldDoesNotPromoteAnAlreadyBrightColour()
    {
        AnsiSgrParser.Apply(TerminalStyle.Default, "1;94")
            .EffectiveForeground.Should().Be(TerminalColor.Palette(12));
    }

    // ── Extended colour ───────────────────────────────────────────────────────

    [Fact]
    public void Xterm256IndexesBelowSixteenStaySymbolic()
    {
        // Keeping them as slots is what lets them follow the theme.
        AnsiSgrParser.Apply(TerminalStyle.Default, "38;5;9")
            .Foreground.Should().Be(TerminalColor.Palette(9));
    }

    [Fact]
    public void Xterm256CubeIndexesBecomeRgb()
    {
        // 208 is the well-known orange: r=5, g=2, b=0 in the 6x6x6 cube.
        AnsiSgrParser.Apply(TerminalStyle.Default, "38;5;208")
            .Foreground.Should().Be(TerminalColor.Rgb(255, 135, 0));
    }

    [Fact]
    public void Xterm256GreyscaleRampBecomesRgb()
    {
        AnsiSgrParser.Apply(TerminalStyle.Default, "38;5;232")
            .Foreground.Should().Be(TerminalColor.Rgb(8, 8, 8));
    }

    [Fact]
    public void TruecolourIsTakenVerbatim()
    {
        AnsiSgrParser.Apply(TerminalStyle.Default, "38;2;18;52;86")
            .Foreground.Should().Be(TerminalColor.Rgb(18, 52, 86));
    }

    [Fact]
    public void ExtendedColourArgumentsAreConsumedSoLaterCodesStillApply()
    {
        // If the 2;18;52;86 run were not skipped, "1" would be read as a colour component
        // and the bold would be lost.
        var style = AnsiSgrParser.Apply(TerminalStyle.Default, "38;2;18;52;86;1");

        style.Bold.Should().BeTrue();
        style.Foreground.Should().Be(TerminalColor.Rgb(18, 52, 86));
    }

    [Fact]
    public void TruncatedExtendedColourLeavesTheColourAlone()
    {
        var style = AnsiSgrParser.Apply(TerminalStyle.Default, "31");

        AnsiSgrParser.Apply(style, "38;2;18")
            .Foreground.Should().Be(TerminalColor.Palette(1));
    }

    // ── Parameter parsing ─────────────────────────────────────────────────────

    [Fact]
    public void OmittedParametersCountAsZero()
    {
        // ECMA-48 says an empty parameter is 0, so "ESC[;31m" is "reset, then red".
        var style = AnsiSgrParser.Apply(TerminalStyle.Default, "1");

        style = AnsiSgrParser.Apply(style, ";31");

        style.Bold.Should().BeFalse();
        style.Foreground.Should().Be(TerminalColor.Palette(1));
    }

    [Fact]
    public void UnknownCodesAreIgnoredRatherThanAbortingTheSequence()
    {
        var style = AnsiSgrParser.Apply(TerminalStyle.Default, "3;5;9;31");

        style.Foreground.Should().Be(TerminalColor.Palette(1));
    }

    // ── Whole-line convenience ────────────────────────────────────────────────

    [Fact]
    public void ParseLineSplitsTextAtEveryStyleChange()
    {
        var spans = AnsiSgrParser.ParseLine("plain\x1B[31mred\x1B[0mplain");

        spans.Should().HaveCount(3);
        spans[0].Text.Should().Be("plain");
        spans[0].Style.Should().Be(TerminalStyle.Default);
        spans[1].Text.Should().Be("red");
        spans[1].Style.Foreground.Should().Be(TerminalColor.Palette(1));
        spans[2].Text.Should().Be("plain");
        spans[2].Style.Should().Be(TerminalStyle.Default);
    }

    [Fact]
    public void ParseLineProducesOneSpanForUniformText()
    {
        var spans = AnsiSgrParser.ParseLine("\x1B[32mall green\x1B[0m");

        spans.Should().ContainSingle();
        spans[0].Text.Should().Be("all green");
    }

    [Fact]
    public void ParseLineDropsEscapesThatCarryNoText()
    {
        // Window-title (OSC) and mode-setting sequences must not leak into the output.
        AnsiSgrParser.ParseLine("\x1B]0;a title\x07visible\x1B[?25l")
            .Should().ContainSingle().Which.Text.Should().Be("visible");
    }

    [Fact]
    public void ParseLineHandlesTextWithNoEscapesAtAll()
    {
        var spans = AnsiSgrParser.ParseLine("nothing special");

        spans.Should().ContainSingle();
        spans[0].Text.Should().Be("nothing special");
        spans[0].Style.Should().Be(TerminalStyle.Default);
    }

    [Fact]
    public void ParseLineOfEmptyTextProducesNoSpans()
    {
        AnsiSgrParser.ParseLine("").Should().BeEmpty();
    }
}
