using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace GrumpyGit.App.Controls;

/// <summary>
/// Turns a branch's palette slot into its brush, so a key swatch is drawn from the same
/// source as the graph line and the two cannot drift apart.
/// </summary>
public class BranchSlotBrushConverter : IValueConverter
{
    public static readonly BranchSlotBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int slot ? CommitGraphPanel.BrushForSlot(slot) : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
