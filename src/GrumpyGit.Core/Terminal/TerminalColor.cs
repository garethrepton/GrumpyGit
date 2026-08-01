using System;

namespace GrumpyGit.Core.Terminal;

/// <summary>How a <see cref="TerminalColor"/> should be resolved by the renderer.</summary>
public enum TerminalColorKind
{
    /// <summary>No colour was selected — the renderer uses its own foreground/background.</summary>
    Default = 0,

    /// <summary>One of the 16 standard ANSI slots. The renderer maps these to theme tokens.</summary>
    Palette,

    /// <summary>A literal 24-bit colour, from a 38;2 / 48;2 sequence or the xterm-256 cube.</summary>
    Rgb,
}

/// <summary>
/// A colour selected by an SGR sequence.
///
/// Deliberately *not* an Avalonia brush: GrumpyGit.Core has no UI dependency, and the
/// 16 base slots must resolve through <c>Themes/Tokens.axaml</c> so the terminal follows
/// the light/dark variant like every other surface. Only the renderer knows the theme,
/// so the parser hands it an intent ("ANSI red") rather than a pixel value.
/// </summary>
public readonly record struct TerminalColor(
    TerminalColorKind Kind,
    byte PaletteIndex,
    byte R,
    byte G,
    byte B)
{
    /// <summary>Inherit the renderer's own colour. Relies on <see cref="TerminalColorKind.Default"/> being 0.</summary>
    public static readonly TerminalColor Default = default;

    public bool IsDefault => Kind == TerminalColorKind.Default;

    /// <summary>One of the 16 standard slots: 0-7 normal, 8-15 bright.</summary>
    public static TerminalColor Palette(int index) =>
        new(TerminalColorKind.Palette, (byte)Math.Clamp(index, 0, 15), 0, 0, 0);

    public static TerminalColor Rgb(byte r, byte g, byte b) =>
        new(TerminalColorKind.Rgb, 0, r, g, b);

    /// <summary>
    /// Resolves an xterm-256 index. The first 16 stay symbolic so they keep following the
    /// theme; 16-231 are the fixed 6x6x6 cube and 232-255 the greyscale ramp, both of which
    /// are literal by definition and become RGB.
    /// </summary>
    public static TerminalColor FromXterm256(int index)
    {
        if (index < 0 || index > 255) return Default;
        if (index < 16) return Palette(index);

        if (index < 232)
        {
            var value = index - 16;
            var b = value % 6;
            var g = value / 6 % 6;
            var r = value / 36;
            return Rgb(CubeLevel(r), CubeLevel(g), CubeLevel(b));
        }

        var grey = (byte)(8 + (index - 232) * 10);
        return Rgb(grey, grey, grey);
    }

    // xterm's cube is not linear: level 0 is pure black, the rest start at 95 and step by 40.
    private static byte CubeLevel(int level) => (byte)(level == 0 ? 0 : 55 + level * 40);
}
