using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using GrumpyGit.Core.Ai;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// One file inside an AI session, with a reviewed flag and a risk hint.
/// </summary>
public partial class ReviewFileViewModel : ObservableObject
{
    public required string FilePath { get; init; }

    /// <summary>Status word from git, e.g. "Added", "Modified", "Deleted".</summary>
    public required string ChangeType { get; init; }

    public int LinesAdded { get; init; }
    public int LinesRemoved { get; init; }

    [ObservableProperty] private bool _isReviewed;

    public string FileName
    {
        get
        {
            var idx = FilePath.LastIndexOfAny(['/', '\\']);
            return idx >= 0 ? FilePath[(idx + 1)..] : FilePath;
        }
    }

    public string Directory
    {
        get
        {
            var idx = FilePath.LastIndexOfAny(['/', '\\']);
            return idx >= 0 ? FilePath[..idx] : string.Empty;
        }
    }

    public int TotalChurn => LinesAdded + LinesRemoved;

    public string ChurnLabel => $"+{LinesAdded} −{LinesRemoved}";

    /// <summary>Single-letter status glyph, matching git's --name-status vocabulary.</summary>
    public string ChangeTypeGlyph => ChangeType switch
    {
        "Added" => "A",
        "Deleted" => "D",
        "Renamed" => "R",
        "Copied" => "C",
        "Conflicted" => "U",
        "Untracked" => "?",
        _ => "M",
    };

    // ── Risk hint ─────────────────────────────────────────────────────────────

    private RiskAssessment? _assessment;

    private RiskAssessment Assessment =>
        _assessment ??= ReviewRiskAssessor.Assess(FilePath, ChangeType, LinesAdded, LinesRemoved);

    /// <summary>
    /// Heuristic scrutiny hint from <see cref="ReviewRiskAssessor"/>. It orders the
    /// reviewer's attention; a "low" result never means the change is safe.
    /// </summary>
    public ReviewRisk Risk => Assessment.Risk;

    public string RiskLabel => Risk switch
    {
        ReviewRisk.High => "high",
        ReviewRisk.Medium => "medium",
        _ => "low",
    };

    /// <summary>Why the risk hint came out the way it did, shown on hover.</summary>
    public string RiskReason => Assessment.Reason;

    /// <summary>
    /// Brush for the risk dot, resolved from the active theme dictionary rather than a
    /// literal so it tracks the theme like everything else.
    /// </summary>
    public IBrush RiskBrush
    {
        get
        {
            var key = Risk switch
            {
                ReviewRisk.High => "RiskHighBrush",
                ReviewRisk.Medium => "RiskMediumBrush",
                _ => "RiskLowBrush",
            };

            if (Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true
                && value is IBrush brush)
            {
                return brush;
            }

            return Brushes.Gray;
        }
    }
}
