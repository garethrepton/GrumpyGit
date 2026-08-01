using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace GrumpyGit.App.Controls;

/// <summary>
/// Resolves design tokens from <c>Themes/Tokens.axaml</c> and
/// <c>Themes/Icons.axaml</c> by key, for the handful of places that must build
/// visuals in code rather than XAML.
/// <para>
/// Look-ups are deliberately performed per call rather than cached in a static
/// field: the token set ships both a Dark and a Light dictionary, so a cached
/// value would freeze whichever variant happened to be active at type-init.
/// </para>
/// </summary>
internal static class ThemeTokens
{
    /// <summary>
    /// Returns the resource registered under <paramref name="key"/> for the
    /// active theme variant, or null when the key is missing or the application
    /// host is unavailable (e.g. the XAML previewer).
    /// </summary>
    private static object? Lookup(string key)
    {
        var app = Application.Current;
        if (app is null)
            return null;

        var variant = app.ActualThemeVariant ?? ThemeVariant.Dark;
        return app.TryFindResource(key, variant, out var value) ? value : null;
    }

    /// <summary>Resolves a themed brush token, e.g. <c>"TextTertiaryBrush"</c>.</summary>
    public static IBrush Brush(string key, IBrush fallback)
        => Lookup(key) as IBrush ?? fallback;

    /// <summary>Resolves a non-themed double token, e.g. <c>"FontSizeCode"</c>.</summary>
    public static double Size(string key, double fallback)
        => Lookup(key) is double d ? d : fallback;

    /// <summary>The application monospace stack, from the <c>MonoFontFamily</c> token.</summary>
    public static FontFamily Mono
        => Lookup("MonoFontFamily") as FontFamily
           ?? new FontFamily("Cascadia Code,JetBrains Mono,Consolas,monospace");

    /// <summary>
    /// Resolves an icon geometry from <c>Themes/Icons.axaml</c>, e.g.
    /// <c>"IconCheckCircle"</c>. Returns null when the key is unknown, which
    /// leaves the host <c>Path</c> simply drawing nothing.
    /// </summary>
    public static Geometry? Icon(string key) => Lookup(key) as Geometry;
}
