using System;
using System.Collections.Generic;

namespace GrumpyGit.Core.Terminal;

/// <summary>
/// Interprets ANSI SGR ("Select Graphic Rendition") parameters — the <c>ESC[…m</c>
/// family that carries colour and emphasis.
///
/// <see cref="Apply"/> is a pure function of (previous style, parameter string). That
/// matters: SGR is *stateful* — <c>ESC[1m</c> says "bold from here on" and says nothing
/// about colour — so the only way to test it honestly is to thread the style through and
/// assert on the result, which a pure function makes trivial. Everything to do with
/// cursor movement, erasure and line breaks lives in <see cref="TerminalScreen"/>.
/// </summary>
public static class AnsiSgrParser
{
    /// <summary>
    /// Applies one SGR parameter string (the text between <c>ESC[</c> and <c>m</c>) to
    /// <paramref name="style"/> and returns the resulting style.
    ///
    /// An empty parameter string means a full reset: <c>ESC[m</c> is the documented
    /// shorthand for <c>ESC[0m</c>, and shells emit it constantly.
    /// </summary>
    public static TerminalStyle Apply(TerminalStyle style, string parameters)
    {
        if (string.IsNullOrEmpty(parameters))
            return TerminalStyle.Default;

        var codes = ParseCodes(parameters);

        for (var i = 0; i < codes.Count; i++)
        {
            switch (codes[i])
            {
                case 0: style = TerminalStyle.Default; break;
                case 1: style = style with { Bold = true }; break;
                case 4: style = style with { Underline = true }; break;
                case 7: style = style with { Inverse = true }; break;

                // 21 is "double underline" in ECMA-48 but "bold off" on virtually every
                // real terminal, and shells emit it meaning the latter.
                case 21:
                case 22: style = style with { Bold = false }; break;
                case 24: style = style with { Underline = false }; break;
                case 27: style = style with { Inverse = false }; break;

                case >= 30 and <= 37:
                    style = style with { Foreground = TerminalColor.Palette(codes[i] - 30) };
                    break;
                case 38:
                    style = style with { Foreground = ReadExtendedColour(codes, ref i, style.Foreground) };
                    break;
                case 39:
                    style = style with { Foreground = TerminalColor.Default };
                    break;

                case >= 40 and <= 47:
                    style = style with { Background = TerminalColor.Palette(codes[i] - 40) };
                    break;
                case 48:
                    style = style with { Background = ReadExtendedColour(codes, ref i, style.Background) };
                    break;
                case 49:
                    style = style with { Background = TerminalColor.Default };
                    break;

                case >= 90 and <= 97:
                    style = style with { Foreground = TerminalColor.Palette(codes[i] - 90 + 8) };
                    break;
                case >= 100 and <= 107:
                    style = style with { Background = TerminalColor.Palette(codes[i] - 100 + 8) };
                    break;

                // Everything else (faint, italic, blink, conceal, framed, …) is parsed so
                // that it cannot desynchronise the parameter walk, then dropped.
            }
        }

        return style;
    }

    /// <summary>
    /// Splits a single line of text containing SGR escapes into styled spans, starting
    /// from <paramref name="initial"/>.
    ///
    /// Convenience wrapper over <see cref="TerminalScreen"/> so that callers who only ever
    /// see one line at a time — log rendering, tests — do not have to drive a screen.
    /// Newlines and cursor movement are not meaningful here; use the screen for those.
    /// </summary>
    public static IReadOnlyList<TerminalSpan> ParseLine(string text, TerminalStyle initial = default)
    {
        var screen = new TerminalScreen();
        screen.SetStyle(initial);
        screen.Write(text);
        return screen.Lines[^1].Spans;
    }

    /// <summary>
    /// Splits "1;38;5;208" into its numeric codes. An omitted parameter is 0 per ECMA-48
    /// ("ESC[;31m" means "reset, then red"), which is why empty entries are kept rather
    /// than skipped — dropping them would shift every subsequent 38/48 argument.
    /// </summary>
    private static List<int> ParseCodes(string parameters)
    {
        var codes = new List<int>(4);
        var start = 0;

        for (var i = 0; i <= parameters.Length; i++)
        {
            if (i != parameters.Length && parameters[i] != ';' && parameters[i] != ':')
                continue;

            var slice = parameters.AsSpan(start, i - start);
            codes.Add(slice.IsEmpty || !int.TryParse(slice, out var code) ? 0 : code);
            start = i + 1;
        }

        return codes;
    }

    /// <summary>
    /// Consumes the arguments of a 38/48 extended-colour selector, advancing
    /// <paramref name="i"/> past them. A malformed selector leaves the colour untouched
    /// rather than guessing, so a truncated sequence cannot repaint the rest of the line.
    /// </summary>
    private static TerminalColor ReadExtendedColour(List<int> codes, ref int i, TerminalColor current)
    {
        if (i + 1 >= codes.Count) return current;

        var selector = codes[i + 1];

        if (selector == 5 && i + 2 < codes.Count)
        {
            var colour = TerminalColor.FromXterm256(codes[i + 2]);
            i += 2;
            return colour;
        }

        if (selector == 2 && i + 4 < codes.Count)
        {
            var colour = TerminalColor.Rgb(
                (byte)Math.Clamp(codes[i + 2], 0, 255),
                (byte)Math.Clamp(codes[i + 3], 0, 255),
                (byte)Math.Clamp(codes[i + 4], 0, 255));
            i += 4;
            return colour;
        }

        i += 1;
        return current;
    }
}
