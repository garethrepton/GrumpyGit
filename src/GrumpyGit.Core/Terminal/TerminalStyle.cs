namespace GrumpyGit.Core.Terminal;

/// <summary>
/// The SGR attributes in force for a run of terminal text.
///
/// Only the attributes a git workflow actually produces are modelled — colour, bold,
/// underline and inverse cover `git status`, `git diff --color`, PSReadLine syntax
/// highlighting and every prompt theme worth the name. Blink, conceal and the rest are
/// parsed and discarded rather than rendered.
/// </summary>
public readonly record struct TerminalStyle(
    TerminalColor Foreground,
    TerminalColor Background,
    bool Bold,
    bool Underline,
    bool Inverse)
{
    /// <summary>Everything off. Relies on every member's default being the "off" value.</summary>
    public static readonly TerminalStyle Default = default;

    /// <summary>
    /// The colour actually painted, after the two rules a renderer must apply itself.
    ///
    /// Bold plus one of the 8 base slots means the bright variant — that is how every
    /// terminal since the VT220 has behaved, and prompts rely on it for contrast. Keeping
    /// the promotion here rather than in the parser means the raw SGR code survives
    /// round-tripping and stays easy to assert on in tests.
    /// </summary>
    public TerminalColor EffectiveForeground =>
        Bold && Foreground.Kind == TerminalColorKind.Palette && Foreground.PaletteIndex < 8
            ? TerminalColor.Palette(Foreground.PaletteIndex + 8)
            : Foreground;
}
