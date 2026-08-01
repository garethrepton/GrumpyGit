using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia.Media;

namespace GrumpyGit.App.Controls;

/// <summary>
/// Parses text containing ANSI escape codes into a sequence of styled text runs.
/// </summary>
public static class AnsiTextParser
{
    public record TextRun(string Text, IBrush Foreground, bool IsBold);

    // Match CSI sequences: ESC[ params letter
    private static readonly Regex CsiRegex = new(
        @"\x1B\[([0-9;]*)([A-Za-z])",
        RegexOptions.Compiled);

    // Match and strip OSC sequences (ESC]...BEL or ESC]...ST) and other single-char escapes
    private static readonly Regex OtherEscapes = new(
        @"\x1B(?:\].*?(?:\x07|\x1B\\)|\([A-Z0-9]|[>=])",
        RegexOptions.Compiled);

    // Standard ANSI 8-color palette
    private static readonly string[] AnsiColors =
    [
        "#585858", // 0 black (bright enough to see on dark bg)
        "#E05050", // 1 red
        "#50E050", // 2 green
        "#E0E050", // 3 yellow
        "#5080F0", // 4 blue
        "#E050E0", // 5 magenta
        "#50E0E0", // 6 cyan
        "#D0D0E8", // 7 white
    ];

    private static readonly string[] AnsiBrightColors =
    [
        "#808080", // 0 bright black
        "#FF6060", // 1 bright red
        "#60FF60", // 2 bright green
        "#FFFF60", // 3 bright yellow
        "#6090FF", // 4 bright blue
        "#FF60FF", // 5 bright magenta
        "#60FFFF", // 6 bright cyan
        "#FFFFFF", // 7 bright white
    ];

    private static readonly IBrush DefaultFg = new SolidColorBrush(Color.Parse("#D0D0E8"));

    public static List<TextRun> Parse(string input)
    {
        var runs = new List<TextRun>();
        if (string.IsNullOrEmpty(input))
            return runs;

        // First strip non-CSI escapes (OSC, charset, etc.)
        input = OtherEscapes.Replace(input, string.Empty);

        var currentFg = DefaultFg;
        bool isBold = false;
        int lastIndex = 0;

        foreach (Match match in CsiRegex.Matches(input))
        {
            // Emit text before this escape
            if (match.Index > lastIndex)
            {
                var text = input[lastIndex..match.Index];
                if (!string.IsNullOrEmpty(text))
                    runs.Add(new TextRun(text, currentFg, isBold));
            }

            lastIndex = match.Index + match.Length;

            // Only process SGR sequences (ending with 'm')
            if (match.Groups[2].Value != "m")
                continue;

            var paramStr = match.Groups[1].Value;
            if (string.IsNullOrEmpty(paramStr))
            {
                // ESC[m = reset
                currentFg = DefaultFg;
                isBold = false;
                continue;
            }

            var codes = paramStr.Split(';', StringSplitOptions.RemoveEmptyEntries);
            int ci = 0;
            while (ci < codes.Length)
            {
                if (!int.TryParse(codes[ci], out var code))
                {
                    ci++;
                    continue;
                }

                switch (code)
                {
                    case 0:
                        currentFg = DefaultFg;
                        isBold = false;
                        break;
                    case 1:
                        isBold = true;
                        break;
                    case 22:
                        isBold = false;
                        break;
                    case >= 30 and <= 37:
                        currentFg = new SolidColorBrush(Color.Parse(
                            isBold ? AnsiBrightColors[code - 30] : AnsiColors[code - 30]));
                        break;
                    case 39:
                        currentFg = DefaultFg;
                        break;
                    case >= 90 and <= 97:
                        currentFg = new SolidColorBrush(Color.Parse(AnsiBrightColors[code - 90]));
                        break;
                    case 38:
                        // Extended colour: 38;5;N (256-color) or 38;2;R;G;B
                        if (ci + 1 < codes.Length && int.TryParse(codes[ci + 1], out var subCode))
                        {
                            if (subCode == 5 && ci + 2 < codes.Length && int.TryParse(codes[ci + 2], out var colorIndex))
                            {
                                currentFg = new SolidColorBrush(Get256Color(colorIndex));
                                ci += 2;
                            }
                            else if (subCode == 2 && ci + 4 < codes.Length
                                     && int.TryParse(codes[ci + 2], out var r)
                                     && int.TryParse(codes[ci + 3], out var g)
                                     && int.TryParse(codes[ci + 4], out var b))
                            {
                                currentFg = new SolidColorBrush(Color.FromRgb(
                                    (byte)Math.Clamp(r, 0, 255),
                                    (byte)Math.Clamp(g, 0, 255),
                                    (byte)Math.Clamp(b, 0, 255)));
                                ci += 4;
                            }
                            else
                            {
                                ci++;
                            }
                        }
                        break;
                }
                ci++;
            }
        }

        // Remaining text after last escape
        if (lastIndex < input.Length)
        {
            var text = input[lastIndex..];
            if (!string.IsNullOrEmpty(text))
                runs.Add(new TextRun(text, currentFg, isBold));
        }

        return runs;
    }

    private static Color Get256Color(int index)
    {
        if (index < 8)
            return Color.Parse(AnsiColors[index]);
        if (index < 16)
            return Color.Parse(AnsiBrightColors[index - 8]);
        if (index < 232)
        {
            // 6x6x6 color cube
            int val = index - 16;
            int b = val % 6;
            int g = (val / 6) % 6;
            int r = val / 36;
            return Color.FromRgb(
                (byte)(r == 0 ? 0 : 55 + r * 40),
                (byte)(g == 0 ? 0 : 55 + g * 40),
                (byte)(b == 0 ? 0 : 55 + b * 40));
        }
        // Grayscale ramp
        int gray = 8 + (index - 232) * 10;
        return Color.FromRgb((byte)gray, (byte)gray, (byte)gray);
    }
}
