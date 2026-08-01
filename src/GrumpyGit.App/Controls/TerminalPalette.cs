using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using GrumpyGit.Core.Terminal;

namespace GrumpyGit.App.Controls;

/// <summary>
/// Turns the colour *intents* produced by <see cref="AnsiSgrParser"/> into brushes.
///
/// The 16 ANSI slots resolve through <c>Themes/Tokens.axaml</c> so terminal output follows
/// the light/dark variant like every other surface — a shell that asks for "green" gets the
/// green that is legible on the current background, not a fixed 1980s CRT value.
/// <para>
/// Brushes are cached and keyed on the active theme variant, exactly as
/// <see cref="CommitGraphCell"/> caches its lane palette: resolution happens once per
/// visible span per frame, which is far too hot for a dictionary lookup, and a cache that
/// ignored the variant would freeze whichever theme was live at first paint.
/// </para>
/// </summary>
internal static class TerminalPalette
{
    private static readonly string[] SlotTokens =
    [
        "AnsiBlackBrush", "AnsiRedBrush", "AnsiGreenBrush", "AnsiYellowBrush",
        "AnsiBlueBrush", "AnsiMagentaBrush", "AnsiCyanBrush", "AnsiWhiteBrush",
        "AnsiBrightBlackBrush", "AnsiBrightRedBrush", "AnsiBrightGreenBrush", "AnsiBrightYellowBrush",
        "AnsiBrightBlueBrush", "AnsiBrightMagentaBrush", "AnsiBrightCyanBrush", "AnsiBrightWhiteBrush",
    ];

    private static IBrush[]? _slots;
    private static IBrush? _foreground;
    private static IBrush? _background;
    private static IBrush? _cursor;
    private static ThemeVariant? _variant;

    // 24-bit colours come straight off the wire and cannot be tokens, so they are cached
    // by value instead — otherwise a truecolor prompt would allocate a brush per span per
    // frame.
    private static readonly Dictionary<uint, IBrush> RgbCache = new();

    private static void Ensure()
    {
        var variant = Application.Current?.ActualThemeVariant ?? ThemeVariant.Dark;
        if (_slots is not null && Equals(_variant, variant))
            return;

        _variant = variant;
        _slots = new IBrush[SlotTokens.Length];
        for (var i = 0; i < SlotTokens.Length; i++)
            _slots[i] = ThemeTokens.Brush(SlotTokens[i], Brushes.Gray);

        _foreground = ThemeTokens.Brush("TerminalFgBrush", Brushes.Gainsboro);
        _background = ThemeTokens.Brush("TerminalBgBrush", Brushes.Black);

        // The caret is a filled block, so it is drawn semi-transparent — an opaque block
        // would hide the character underneath it, which is the one you are about to edit.
        var cursorToken = ThemeTokens.Brush("TerminalCursorBrush", Brushes.MediumPurple);
        _cursor = new SolidColorBrush(
            (cursorToken as ISolidColorBrush)?.Color ?? Colors.MediumPurple, 0.55);

        // A theme switch invalidates cached RGB brushes too: nothing about them changes,
        // but leaving the cache to grow across a long session for no reason is worse than
        // rebuilding it on the rare variant change.
        RgbCache.Clear();
    }

    /// <summary>Default text colour, used wherever no SGR colour is in force.</summary>
    public static IBrush Foreground { get { Ensure(); return _foreground!; } }

    /// <summary>Default surface colour behind the scrollback.</summary>
    public static IBrush Background { get { Ensure(); return _background!; } }

    /// <summary>Translucent block drawn at the cursor while the panel has focus.</summary>
    public static IBrush Cursor { get { Ensure(); return _cursor!; } }

    /// <summary>Resolves one parsed colour, falling back for <see cref="TerminalColorKind.Default"/>.</summary>
    public static IBrush Resolve(TerminalColor colour, IBrush fallback)
    {
        Ensure();

        switch (colour.Kind)
        {
            case TerminalColorKind.Palette:
                return _slots![colour.PaletteIndex];

            case TerminalColorKind.Rgb:
                var key = (uint)(colour.R << 16 | colour.G << 8 | colour.B);
                if (!RgbCache.TryGetValue(key, out var brush))
                {
                    brush = new SolidColorBrush(Color.FromRgb(colour.R, colour.G, colour.B));
                    RgbCache[key] = brush;
                }
                return brush;

            default:
                return fallback;
        }
    }
}
