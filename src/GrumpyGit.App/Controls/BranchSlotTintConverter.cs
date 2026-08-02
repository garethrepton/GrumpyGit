using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;

namespace GrumpyGit.App.Controls;

/// <summary>
/// A branch's palette slot as a very low-alpha wash, for tinting commit rows so the list
/// carries the same branch identity as the graph and its key.
///
/// The alpha has to stay low enough that the row's selected and hover states still read
/// through it — the tint is an aid to scanning, not a highlight competing with selection.
/// </summary>
public class BranchSlotTintConverter : IValueConverter
{
    public static readonly BranchSlotTintConverter Instance = new();

    private const double TintAlpha = 0.13;

    // One brush per slot rather than one per row: the commit list evaluates this for
    // every row it realises, and a fresh SolidColorBrush each time would churn.
    private static IBrush[]? _tints;
    private static ThemeVariant? _variant;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int slot || slot < 0)
            return Brushes.Transparent;

        EnsureTints();
        return _tints![((slot % _tints.Length) + _tints.Length) % _tints.Length];
    }

    private static void EnsureTints()
    {
        var variant = Avalonia.Application.Current?.ActualThemeVariant ?? ThemeVariant.Dark;
        if (_tints is not null && Equals(_variant, variant))
            return;

        _variant = variant;
        var tints = new IBrush[BranchPaletteSize];
        for (var i = 0; i < BranchPaletteSize; i++)
        {
            tints[i] = CommitGraphPanel.BrushForSlot(i) is ISolidColorBrush s
                ? new SolidColorBrush(s.Color, TintAlpha)
                : Brushes.Transparent;
        }

        _tints = tints;
    }

    private const int BranchPaletteSize = GrumpyGit.Core.Graph.BranchPalette.Size;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
