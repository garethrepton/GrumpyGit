using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using GrumpyGit.Core.LocalModel;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// One file's entry in the whole-commit review. Carries its own state because the files
/// are reviewed one at a time and each row fills in as its turn comes — a list that
/// appeared all at once at the end would look like nothing was happening for a minute.
/// </summary>
public partial class FileReviewViewModel : ObservableObject
{
    public string Path { get; init; } = string.Empty;

    /// <summary>Churn from git's own count, known before the model has said anything.</summary>
    public int Added { get; init; }

    public int Removed { get; init; }

    [ObservableProperty] private bool _isPending = true;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private ReviewRisk _risk = ReviewRisk.None;

    public List<ReviewIssue> Issues { get; } = new();

    /// <summary>File name alone, for a list where the directory is mostly noise.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    public string ChurnLabel => $"+{Added} −{Removed}";

    /// <summary>Worst first — see <see cref="ScanRanking"/>.</summary>
    public long Score => ScanRanking.Score(Risk, Issues.Count, Added, Removed);

    /// <summary>
    /// Whether this row is one of the ones the view is actually for. The rest are listed
    /// but not expanded: a scan whose output is forty equally-weighted paragraphs has told
    /// you nothing you could not get from the file list.
    /// </summary>
    public bool IsNotable => ScanRanking.IsNotable(Risk, Issues.Count);

    public bool HasRisk => Risk != ReviewRisk.None;

    public bool IsDanger => Risk == ReviewRisk.Danger;

    public string RiskLabel => Risk switch
    {
        ReviewRisk.Danger => "DANGER",
        ReviewRisk.Caution => "CAUTION",
        _ => string.Empty,
    };

    /// <summary>Issue count, for a row that is worth expanding.</summary>
    public string IssueLabel => Issues.Count switch
    {
        0 => string.Empty,
        1 => "1 issue",
        _ => $"{Issues.Count} issues",
    };

    public string IssueDetail => string.Join("\n", Issues.Select(i =>
        i.IsAnchored ? $"line {i.SourceLine}: {i.Text}" : i.Text));

    public bool HasIssues => Issues.Count > 0;

    /// <summary>What the row says before its turn comes round.</summary>
    public string StatusLabel => IsRunning ? "reading…" : IsPending ? "queued" : string.Empty;

    partial void OnIsPendingChanged(bool value) => OnPropertyChanged(nameof(StatusLabel));

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(StatusLabel));

    partial void OnRiskChanged(ReviewRisk value)
    {
        OnPropertyChanged(nameof(HasRisk));
        OnPropertyChanged(nameof(IsDanger));
        OnPropertyChanged(nameof(RiskLabel));
    }

    public void Applied(DiffReviewResult result)
    {
        Summary = result.Summary;
        Risk = result.Risk;
        Issues.Clear();
        Issues.AddRange(result.Issues);
        IsPending = false;
        IsRunning = false;
        OnPropertyChanged(nameof(IssueLabel));
        OnPropertyChanged(nameof(IssueDetail));
        OnPropertyChanged(nameof(HasIssues));
        OnPropertyChanged(nameof(Score));
        OnPropertyChanged(nameof(IsNotable));
    }

    public void Failed(string reason)
    {
        Summary = reason;
        IsPending = false;
        IsRunning = false;
    }
}
