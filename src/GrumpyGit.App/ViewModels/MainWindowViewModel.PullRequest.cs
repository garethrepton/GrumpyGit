using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrumpyGit.Core.Git;
using GrumpyGit.Core.Models;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// Pull request preview — review a branch against the one it will merge into, before
/// raising anything.
///
/// The review that matters is the one done before the PR exists, because that is the only
/// point where fixing something is free. Everything here is computed locally: the merge
/// base, the net diff, and a real merge simulation that says whether it would conflict.
/// Nothing is pushed, nothing is checked out, and no hosting provider is contacted —
/// the summary is markdown on the clipboard, for the human to paste.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>
    /// Branch names tried, in order, when the panel opens and nothing has been chosen.
    /// These are the conventional integration branches; the first that exists wins.
    /// </summary>
    private static readonly string[] PreferredTargetBranches = ["develop", "main", "master", "trunk"];

    [ObservableProperty] private bool _isPullRequestVisible;
    [ObservableProperty] private bool _isLoadingPullRequest;
    [ObservableProperty] private string _prSourceBranch = string.Empty;
    [ObservableProperty] private string _prTargetBranch = string.Empty;
    [ObservableProperty] private string _prError = string.Empty;

    public ObservableCollection<ReviewFileViewModel> PrFiles { get; } = new();
    public ObservableCollection<CommitRowViewModel> PrCommits { get; } = new();
    public ObservableCollection<string> PrConflictPaths { get; } = new();

    /// <summary>
    /// The loaded preview. Held whole rather than shredded into properties because the
    /// summary is written from it verbatim, and rebuilding it from the view state would
    /// be a second, drifting copy of the same facts.
    /// </summary>
    private PullRequestPreview? _prPreview;

    private string PrMergeBase => _prPreview?.MergeBaseHash ?? string.Empty;
    private string PrHeadHash => _prPreview?.HeadHash ?? string.Empty;

    [ObservableProperty] private int _prReviewedCount;
    [ObservableProperty] private int _prFileCount;
    [ObservableProperty] private int _prLinesAdded;
    [ObservableProperty] private int _prLinesRemoved;
    [ObservableProperty] private MergeOutcome _prMergeOutcome = MergeOutcome.Unknown;

    public bool HasPrError => !string.IsNullOrEmpty(PrError);

    /// <summary>True once a preview has loaded — the panel is otherwise all empty state.</summary>
    [ObservableProperty] private bool _hasPrPreview;

    public string PrHeaderLabel => $"{PrSourceBranch} → {PrTargetBranch}";

    public string PrChurnLabel => $"+{PrLinesAdded} −{PrLinesRemoved}";

    public string PrCommitCountLabel =>
        PrCommits.Count == 1 ? "1 commit" : $"{PrCommits.Count} commits";

    public string PrFileCountLabel =>
        PrFileCount == 1 ? "1 file" : $"{PrFileCount} files";

    public string PrProgressLabel =>
        PrFileCount == 0 ? "—" : $"{PrReviewedCount}/{PrFileCount} reviewed";

    /// <summary>Base the diff was taken from, shown so the reviewer knows what they are looking at.</summary>
    public string PrMergeBaseLabel =>
        PrMergeBase.Length > 7 ? PrMergeBase[..7] : PrMergeBase;

    public bool PrHasConflicts => PrMergeOutcome == MergeOutcome.Conflicts;

    public bool PrMergesCleanly => HasPrPreview && PrMergeOutcome == MergeOutcome.Clean;

    /// <summary>
    /// Git could not simulate the merge — <c>merge-tree --write-tree</c> needs git 2.38.
    /// Said out loud rather than shown as "clean", which would be the dangerous guess.
    /// </summary>
    public bool PrMergeUnknown => HasPrPreview && PrMergeOutcome == MergeOutcome.Unknown;

    public string PrMergeLabel => PrMergeOutcome switch
    {
        MergeOutcome.Clean => "Merges cleanly",
        MergeOutcome.Conflicts => PrConflictPaths.Count == 1
            ? "1 conflicting file"
            : $"{PrConflictPaths.Count} conflicting files",
        _ => "Merge check unavailable",
    };

    /// <summary>Everything except the source — a branch cannot be reviewed against itself.</summary>
    public IReadOnlyList<string> PrTargetBranches =>
        Branches.Where(b => !string.Equals(b, PrSourceBranch, StringComparison.Ordinal)).ToList();

    partial void OnPrSourceBranchChanged(string value)
    {
        OnPropertyChanged(nameof(PrHeaderLabel));
        OnPropertyChanged(nameof(PrTargetBranches));
        InvalidatePreviewIfBranchesMoved();
    }

    partial void OnPrTargetBranchChanged(string value)
    {
        OnPropertyChanged(nameof(PrHeaderLabel));
        InvalidatePreviewIfBranchesMoved();
    }

    /// <summary>
    /// The header reads from the two pickers live, so a loaded preview whose branches no
    /// longer match them would sit one pair's files under another pair's title. Dropping
    /// it is the only honest option; the reviewer presses Preview again.
    /// </summary>
    private void InvalidatePreviewIfBranchesMoved()
    {
        if (_prPreview is null) return;

        if (string.Equals(_prPreview.SourceBranch, PrSourceBranch, StringComparison.Ordinal)
            && string.Equals(_prPreview.TargetBranch, PrTargetBranch, StringComparison.Ordinal))
        {
            return;
        }

        ClearPreview();
    }

    partial void OnPrErrorChanged(string value) => OnPropertyChanged(nameof(HasPrError));
    partial void OnPrLinesAddedChanged(int value) => OnPropertyChanged(nameof(PrChurnLabel));
    partial void OnPrLinesRemovedChanged(int value) => OnPropertyChanged(nameof(PrChurnLabel));

    partial void OnPrFileCountChanged(int value)
    {
        OnPropertyChanged(nameof(PrFileCountLabel));
        OnPropertyChanged(nameof(PrProgressLabel));
        OnPropertyChanged(nameof(PrIsUpToDate));
    }

    /// <summary>
    /// A preview loaded, and the source branch introduces nothing. Said explicitly rather
    /// than left as an empty file list, which reads as a failure to load.
    /// </summary>
    public bool PrIsUpToDate => HasPrPreview && PrFileCount == 0;

    partial void OnPrReviewedCountChanged(int value) => OnPropertyChanged(nameof(PrProgressLabel));

    partial void OnPrMergeOutcomeChanged(MergeOutcome value) => RaiseMergeVerdict();

    partial void OnHasPrPreviewChanged(bool value) => RaiseMergeVerdict();

    private void RaiseMergeVerdict()
    {
        OnPropertyChanged(nameof(PrIsUpToDate));
        OnPropertyChanged(nameof(PrHasConflicts));
        OnPropertyChanged(nameof(PrMergesCleanly));
        OnPropertyChanged(nameof(PrMergeUnknown));
        OnPropertyChanged(nameof(PrMergeLabel));
    }

    [RelayCommand]
    private async Task TogglePullRequestAsync()
    {
        IsPullRequestVisible = !IsPullRequestVisible;
        if (!IsPullRequestVisible) return;

        if (string.IsNullOrEmpty(RepoPath))
        {
            PrError = "Open a repository first.";
            return;
        }

        // Only pick defaults on first open: reopening the panel must not throw away the
        // pair the reviewer deliberately chose last time.
        if (string.IsNullOrEmpty(PrSourceBranch))
            PrSourceBranch = Branches.Contains(CurrentBranch) ? CurrentBranch : Branches.FirstOrDefault() ?? string.Empty;

        if (string.IsNullOrEmpty(PrTargetBranch))
            PrTargetBranch = DefaultTargetBranch(PrSourceBranch);

        if (!HasPrPreview)
            await LoadPullRequestPreviewAsync();
    }

    [RelayCommand]
    private void ClosePullRequest() => IsPullRequestVisible = false;

    /// <summary>
    /// Drops all pull request state on a repository change. Branch names collide freely
    /// between repositories, so keeping the selection would silently preview a different
    /// repository's "develop".
    /// </summary>
    private void ResetPullRequestForRepo()
    {
        ClearPreview();
        PrSourceBranch = string.Empty;
        PrTargetBranch = string.Empty;
        PrError = string.Empty;
    }

    /// <summary>
    /// First conventional integration branch that exists, else any branch that is not the
    /// source. Guessing beats making the reviewer choose twice to see anything at all.
    /// </summary>
    private string DefaultTargetBranch(string source)
    {
        foreach (var candidate in PreferredTargetBranches)
        {
            if (Branches.Contains(candidate) && !string.Equals(candidate, source, StringComparison.Ordinal))
                return candidate;
        }

        return Branches.FirstOrDefault(b => !string.Equals(b, source, StringComparison.Ordinal)) ?? string.Empty;
    }

    [RelayCommand]
    private async Task LoadPullRequestPreviewAsync()
    {
        if (string.IsNullOrEmpty(RepoPath)) return;

        PrError = string.Empty;

        if (string.IsNullOrEmpty(PrSourceBranch) || string.IsNullOrEmpty(PrTargetBranch))
        {
            // Usually a repository with a single branch, where there is no second branch
            // for the first to be reviewed against.
            PrError = Branches.Count < 2
                ? "A pull request needs two branches, and this repository has fewer."
                : "Pick a source and a target branch.";
            return;
        }

        IsLoadingPullRequest = true;
        try
        {
            var preview = await PullRequestPreviewBuilder.BuildAsync(
                _git, RepoPath, PrSourceBranch, PrTargetBranch);

            ApplyPreview(preview);
            HasPrPreview = true;

            StatusMessage = preview.IsEmpty
                ? $"{PrSourceBranch} has nothing {PrTargetBranch} does not already have"
                : $"Previewing {PrHeaderLabel} — {PrCommitCountLabel}, {PrFileCountLabel}";
        }
        catch (Exception ex)
        {
            // Wipe the previous preview rather than leaving one branch pair's files under
            // another pair's header.
            ClearPreview();
            PrError = ex.Message;
        }
        finally
        {
            IsLoadingPullRequest = false;
        }
    }

    private void ClearPreview()
    {
        PrFiles.Clear();
        PrCommits.Clear();
        PrConflictPaths.Clear();
        PrFileCount = 0;
        PrReviewedCount = 0;
        PrLinesAdded = 0;
        PrLinesRemoved = 0;
        PrMergeOutcome = MergeOutcome.Unknown;
        _prPreview = null;
        HasPrPreview = false;
        OnPropertyChanged(nameof(PrMergeBaseLabel));
        OnPropertyChanged(nameof(PrCommitCountLabel));
    }

    private void ApplyPreview(PullRequestPreview preview)
    {
        ClearPreview();

        _prPreview = preview;

        foreach (var commit in preview.Commits)
        {
            PrCommits.Add(new CommitRowViewModel
            {
                Hash = commit.Hash,
                Subject = commit.Subject,
                AuthorName = commit.AuthorName,
                AuthorDate = commit.AuthorDate,
                RefNames = commit.RefNames,
            });
        }

        var conflicting = preview.Merge.ConflictingPaths.ToHashSet(StringComparer.Ordinal);
        foreach (var path in preview.Merge.ConflictingPaths)
            PrConflictPaths.Add(path);

        var sessionKey = PullRequestSessionKey;

        var files = preview.Files.Select(change =>
        {
            preview.Stats.TryGetValue(change.Path, out var churn);
            return new ReviewFileViewModel
            {
                FilePath = change.Path,
                ChangeType = change.Status.ToString(),
                LinesAdded = churn.Added,
                LinesRemoved = churn.Removed,
                IsReviewed = _reviewState?.IsReviewed(sessionKey, change.Path) ?? false,
                WouldConflict = conflicting.Contains(change.Path),
            };
        });

        // Conflicts first, then risk, then size: a file that will not even merge is the
        // one to look at before anything else.
        foreach (var file in files
                     .OrderByDescending(f => f.WouldConflict)
                     .ThenByDescending(f => f.Risk)
                     .ThenByDescending(f => f.TotalChurn))
        {
            file.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(ReviewFileViewModel.IsReviewed)) return;
                _reviewState?.SetReviewed(PullRequestSessionKey, file.FilePath, file.IsReviewed);
                RecalculatePrReviewed();
            };

            PrFiles.Add(file);
        }

        PrFileCount = PrFiles.Count;
        PrLinesAdded = preview.LinesAdded;
        PrLinesRemoved = preview.LinesRemoved;
        PrMergeOutcome = preview.Merge.Outcome;
        RecalculatePrReviewed();

        MarkNotedFiles();
        OnPropertyChanged(nameof(PrMergeBaseLabel));
        OnPropertyChanged(nameof(PrCommitCountLabel));
    }

    /// <summary>
    /// Identity for the persisted reviewed ticks. Keyed on the target branch and the
    /// source tip together, so adding a commit — or retargeting the same branch at a
    /// different base — correctly starts the review over. The changes under review are
    /// not the same ones at that point.
    /// </summary>
    private string PullRequestSessionKey => $"pr:{PrTargetBranch}:{PrHeadHash}";

    private void RecalculatePrReviewed() => PrReviewedCount = PrFiles.Count(f => f.IsReviewed);

    /// <summary>Net diff for one file across the whole range, as the merge would land it.</summary>
    [RelayCommand]
    private async Task ShowPrFileDiffAsync(ReviewFileViewModel? file)
    {
        if (file is null || string.IsNullOrEmpty(PrMergeBase)) return;

        SelectedReviewFile = file;
        LoadNoteForCurrentFile();

        try
        {
            var diff = await _git.GetCommitRangeFileDiffAsync(
                RepoPath, PrMergeBase, PrHeadHash, file.FilePath, CurrentDiffOptions);

            var parsed = UnifiedDiffParser.Parse(diff);

            // A range diff is not stageable. Leaving the previous file's hunk buttons
            // alive would offer to apply its patch against whatever is on disk now.
            DiffHunks.Clear();
            ClearImageDiff();

            CurrentDiff = parsed;
            DiffFilePath = file.FilePath;
            UpdateDiffStats(parsed);
            UpdateStagingAvailability();
            IsDiffFromStagedFile = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load diff: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task NextUnreviewedPrFileAsync()
    {
        var start = SelectedReviewFile is null ? 0 : PrFiles.IndexOf(SelectedReviewFile) + 1;

        // Wrap around so the last file leads back to anything skipped earlier.
        for (var i = 0; i < PrFiles.Count; i++)
        {
            var file = PrFiles[(start + i) % PrFiles.Count];
            if (file.IsReviewed) continue;

            await ShowPrFileDiffAsync(file);
            return;
        }

        StatusMessage = PrFiles.Count == 0
            ? "Nothing to review"
            : "Every file in this pull request is reviewed";
    }

    [RelayCommand]
    private void MarkPullRequestReviewed()
    {
        foreach (var file in PrFiles)
            file.IsReviewed = true;
    }

    [RelayCommand]
    private void ResetPullRequestReview()
    {
        foreach (var file in PrFiles)
            file.IsReviewed = false;

        _reviewState?.ClearSession(PullRequestSessionKey);
    }

    /// <summary>
    /// The review as markdown, for pasting wherever the pull request is actually raised.
    /// Returns empty when there is nothing loaded, which the caller treats as "no-op"
    /// rather than putting a blank clipboard in front of the user.
    /// </summary>
    public string BuildPullRequestSummary()
    {
        if (_prPreview is null) return string.Empty;

        var reviewed = PrFiles
            .Select(f => new ReviewedFile(
                f.FilePath,
                f.LinesAdded,
                f.LinesRemoved,
                f.IsReviewed,
                _notesStore?.Get(f.FilePath) ?? string.Empty))
            .ToList();

        return PullRequestSummaryBuilder.Build(_prPreview, reviewed);
    }
}
