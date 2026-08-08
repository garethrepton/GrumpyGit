using System;
using System.Globalization;
using Avalonia.Data.Converters;
using GrumpyGit.Core.LocalModel;

namespace GrumpyGit.App.Controls;

/// <summary>
/// True only for <see cref="ReviewRisk.Danger"/>, so the risk badge can switch to the red
/// ramp while caution stays on the amber one. A converter rather than a second viewmodel
/// property because the badge is styled by class, and classes take booleans.
/// </summary>
public sealed class RiskIsDangerConverter : IValueConverter
{
    public static readonly RiskIsDangerConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ReviewRisk.Danger;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
