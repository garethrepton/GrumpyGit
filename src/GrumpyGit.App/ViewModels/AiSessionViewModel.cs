using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GrumpyGit.Core.Ai;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// One AI working session, presented as a single reviewable unit.
/// </summary>
public partial class AiSessionViewModel : ObservableObject
{
    public required AiSession Session { get; init; }

    /// <summary>
    /// Stable identity for review state. Uses the head commit, so amending or adding
    /// to the session invalidates prior review marks — which is correct, because the
    /// changes being reviewed are no longer the same.
    /// </summary>
    public string SessionKey => Session.HeadHash;

    public string AgentName => Session.AgentName;
    public string BaseHash => Session.BaseHash ?? string.Empty;
    public string HeadHash => Session.HeadHash;
    public bool HasBase => Session.BaseHash is not null;

    public int CommitCount => Session.CommitCount;

    public string TimeRange
    {
        get
        {
            var start = Session.StartedAt.LocalDateTime;
            var end = Session.EndedAt.LocalDateTime;
            return start.Date == end.Date
                ? $"{start:d MMM HH:mm}–{end:HH:mm}"
                : $"{start:d MMM HH:mm} → {end:d MMM HH:mm}";
        }
    }

    public string DurationLabel
    {
        get
        {
            var d = Session.Duration;
            if (d < TimeSpan.FromMinutes(1)) return "under a minute";
            if (d < TimeSpan.FromHours(1)) return $"{(int)d.TotalMinutes} min";
            return $"{d.TotalHours:0.#} hr";
        }
    }

    public string CommitCountLabel => $"{CommitCount} commit{(CommitCount == 1 ? "" : "s")}";

    /// <summary>Files touched across the whole session, populated on demand.</summary>
    public ObservableCollection<ReviewFileViewModel> Files { get; } = new();

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _filesLoaded;
    [ObservableProperty] private int _reviewedCount;
    [ObservableProperty] private int _fileCount;
    [ObservableProperty] private int _linesAdded;
    [ObservableProperty] private int _linesRemoved;

    public IReadOnlyList<CommitRowViewModel> CommitRows { get; init; } = [];

    public string ReviewProgressLabel =>
        FileCount == 0 ? "—" : $"{ReviewedCount}/{FileCount} reviewed";

    public bool IsFullyReviewed => FileCount > 0 && ReviewedCount >= FileCount;

    public double ReviewProgressFraction => FileCount == 0 ? 0 : (double)ReviewedCount / FileCount;

    public string ChurnLabel => $"+{LinesAdded} −{LinesRemoved}";

    partial void OnReviewedCountChanged(int value) => RaiseProgress();
    partial void OnFileCountChanged(int value) => RaiseProgress();
    partial void OnLinesAddedChanged(int value) => OnPropertyChanged(nameof(ChurnLabel));
    partial void OnLinesRemovedChanged(int value) => OnPropertyChanged(nameof(ChurnLabel));

    private void RaiseProgress()
    {
        OnPropertyChanged(nameof(ReviewProgressLabel));
        OnPropertyChanged(nameof(IsFullyReviewed));
        OnPropertyChanged(nameof(ReviewProgressFraction));
    }

    public void RecalculateReviewed()
    {
        var reviewed = 0;
        foreach (var f in Files)
            if (f.IsReviewed) reviewed++;
        ReviewedCount = reviewed;
    }
}
