using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrumpyGit.App.Controls;
using GrumpyGit.Core.Git;
using GrumpyGit.Core.LocalModel;

namespace GrumpyGit.App.ViewModels;

/// <summary>What the scan is reading. Decided by what is selected, not by a mode switch.</summary>
public enum AiScanScope
{
    None,
    WorkingTree,
    Commit,
    PullRequest,
}

/// <summary>
/// Partial class — the AI view: every file in the selected change read by the model, worst
/// first, each in a sentence.
///
/// This replaces the old "Review all" modal, which read the staged set and listed the
/// results in commit order. Two things were wrong with that. It only ever answered the
/// pre-commit question, so the model was useless while browsing history or a branch
/// comparison. And an unordered list of forty summaries is not a review — the work of
/// deciding what matters was left with the reader, which is the work they wanted done.
///
/// So: it follows the selection, it ranks (<see cref="ScanRanking"/>), and it says its
/// overview last rather than first. The overview is generated from what the per-file passes
/// actually concluded, not from the shape of the change — the orientation prompt already
/// covers the shape, and repeating it here in bigger type would be the same guess twice.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>Rows, in the order the view should read them. Rebuilt when a scan finishes.</summary>
    public ObservableCollection<FileReviewViewModel> AiScanFiles { get; } = new();

    [ObservableProperty] private bool _isAiScanVisible;
    [ObservableProperty] private bool _isAiScanRunning;
    [ObservableProperty] private string _aiScanStatus = string.Empty;
    [ObservableProperty] private string _aiScanOverview = string.Empty;
    [ObservableProperty] private ReviewRisk _aiScanRisk = ReviewRisk.None;
    [ObservableProperty] private string _aiScanScopeLabel = string.Empty;

    private CancellationTokenSource? _aiScanCts;

    /// <summary>
    /// Which change the scan would read if it ran now. Ordered so the pull request preview
    /// wins while it is open: it is a modal view over a specific comparison, and scanning
    /// the working tree underneath it would answer a question nobody asked.
    /// </summary>
    private AiScanScope CurrentScanScope =>
        IsPullRequestVisible && PrFiles.Count > 0 ? AiScanScope.PullRequest
        : IsWorkingTreeSelected ? AiScanScope.WorkingTree
        : SelectedCommit is not null && ChangedFiles.Count > 0 ? AiScanScope.Commit
        : AiScanScope.None;

    public bool CanRunAiScan => HasLocalModel && CurrentScanScope != AiScanScope.None;

    public bool HasAiScanOverview => AiScanOverview.Length > 0;

    public bool AiScanHasRisk => AiScanRisk != ReviewRisk.None;

    public string AiScanRiskLabel => AiScanRisk switch
    {
        ReviewRisk.Danger => "DANGER",
        ReviewRisk.Caution => "CAUTION",
        _ => string.Empty,
    };

    /// <summary>The counts under the title: what was read, and how much of it was flagged.</summary>
    public string AiScanTally
    {
        get
        {
            if (AiScanFiles.Count == 0) return string.Empty;

            var flagged = AiScanFiles.Count(f => f.HasRisk);
            var issues = AiScanFiles.Sum(f => f.Issues.Count);

            return flagged == 0 && issues == 0
                ? $"{AiScanFiles.Count} file(s) · nothing flagged"
                : $"{AiScanFiles.Count} file(s) · {flagged} flagged · {issues} issue(s)";
        }
    }

    partial void OnAiScanOverviewChanged(string value) => OnPropertyChanged(nameof(HasAiScanOverview));

    partial void OnAiScanRiskChanged(ReviewRisk value)
    {
        OnPropertyChanged(nameof(AiScanHasRisk));
        OnPropertyChanged(nameof(AiScanRiskLabel));
    }

    [RelayCommand]
    private void CloseAiScan()
    {
        CancelAiScan();
        IsAiScanVisible = false;
    }

    [RelayCommand]
    private void CancelAiScan()
    {
        try { _aiScanCts?.Cancel(); }
        catch (ObjectDisposedException) { /* already finished */ }
    }

    /// <summary>
    /// Reads every file in the selected change, then writes the overview.
    ///
    /// Rows appear up front and fill in one at a time. That is not a progress affectation:
    /// a small model on a CPU takes tens of seconds a file, and a list that materialised
    /// complete after four minutes would look like a hang for the first three.
    /// </summary>
    [RelayCommand]
    private async Task RunAiScanAsync()
    {
        if (IsAiScanRunning || _reviewService is null) return;

        var scope = CurrentScanScope;
        var targets = ScanTargets(scope);

        if (targets.Count == 0)
        {
            ShowToast("Nothing selected to scan.", ToastSeverity.Info);
            return;
        }

        // One model, one gate. The open file's own review would otherwise hold the first
        // row up for no reason.
        CancelPendingReview();

        AiScanScopeLabel = ScopeLabel(scope);
        AiScanOverview = string.Empty;
        AiScanRisk = ReviewRisk.None;

        AiScanFiles.Clear();
        foreach (var target in targets)
            AiScanFiles.Add(new FileReviewViewModel
            {
                Path = target.Path,
                Added = target.Added,
                Removed = target.Removed,
            });

        IsAiScanVisible = true;
        IsAiScanRunning = true;

        _aiScanCts = new CancellationTokenSource();
        var ct = _aiScanCts.Token;

        try
        {
            for (var i = 0; i < AiScanFiles.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var row = AiScanFiles[i];
                row.IsRunning = true;
                AiScanStatus = $"Reading {i + 1} of {AiScanFiles.Count} — {row.Path}";

                await ScanOneFileAsync(scope, row, ct);
                OnPropertyChanged(nameof(AiScanTally));
            }

            RankAiScan();

            AiScanStatus = "Writing the overview…";
            await BuildAiScanOverviewAsync(ct);
            AiScanStatus = ScanOutcome();
        }
        catch (OperationCanceledException)
        {
            foreach (var row in AiScanFiles.Where(r => r.IsPending || r.IsRunning))
                row.Failed("not read");

            RankAiScan();
            AiScanStatus = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            AiScanStatus = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsAiScanRunning = false;
            _aiScanCts?.Dispose();
            _aiScanCts = null;
            OnPropertyChanged(nameof(AiScanTally));
        }
    }

    /// <summary>Path and churn for everything in scope, before any of it is read.</summary>
    private List<(string Path, int Added, int Removed)> ScanTargets(AiScanScope scope) => scope switch
    {
        AiScanScope.PullRequest => PrFiles
            .Select(f => (f.FilePath, f.LinesAdded, f.LinesRemoved))
            .ToList(),

        // Staged and unstaged together. "What am I about to commit" is the staged set, but
        // this view answers "what have I done", and a change half-staged is still a change.
        AiScanScope.WorkingTree => StagedFiles
            .Concat(ChangedFiles)
            .GroupBy(f => f.Path, StringComparer.Ordinal)
            .Select(g => (g.Key, g.Max(f => f.LinesAdded), g.Max(f => f.LinesRemoved)))
            .ToList(),

        AiScanScope.Commit => ChangedFiles
            .Select(f => (f.Path, f.LinesAdded, f.LinesRemoved))
            .ToList(),

        _ => [],
    };

    private string ScopeLabel(AiScanScope scope) => scope switch
    {
        AiScanScope.PullRequest => PrHeaderLabel,
        AiScanScope.WorkingTree => "working changes",
        AiScanScope.Commit => SelectedCommit is { } c ? $"{c.ShortHash} {c.Subject}" : "commit",
        _ => string.Empty,
    };

    private async Task ScanOneFileAsync(AiScanScope scope, FileReviewViewModel row, CancellationToken ct)
    {
        try
        {
            var raw = await RawDiffForScanAsync(scope, row.Path, ct);
            var parsed = UnifiedDiffParser.Parse(raw);

            // A file git does not track yet has no diff to give. Its contents are entirely
            // new, which makes it more worth reading than most, so it is reviewed as one
            // large addition rather than skipped.
            if (parsed.Hunks.Count == 0 && scope == AiScanScope.WorkingTree)
                parsed = UnifiedDiffParser.ParseRawContent(await UntrackedContentAsync(row.Path));

            if (parsed.Hunks.Count == 0)
            {
                row.Failed("no textual diff");
                return;
            }

            var summary = ChangeSummaryBuilder.Build(row.Path, parsed);
            var review = await _reviewService!.ReviewAsync(row.Path, parsed, summary, null, ct);

            switch (review.State)
            {
                case DiffReviewState.Complete:
                    row.Applied(review.Result);
                    break;
                case DiffReviewState.TooLarge:
                    row.Failed($"too large to read (over {DiffReviewService.MaxChangedLines} changed lines)");
                    break;
                default:
                    row.Failed(review.Text.Length > 0 ? review.Text : "not read");
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One unreadable file must not end the run — the other forty still matter.
            row.Failed(ex.Message);
        }
    }

    /// <summary>
    /// The same diff the user would see if they clicked the file, from the same wrappers.
    /// Anything else and the review would describe a change the reader cannot find.
    /// </summary>
    private Task<string> RawDiffForScanAsync(AiScanScope scope, string path, CancellationToken ct) => scope switch
    {
        AiScanScope.PullRequest =>
            _git.GetCommitRangeFileDiffAsync(RepoPath, PrMergeBase, PrHeadHash, path, CurrentDiffOptions, ct),

        AiScanScope.Commit when SelectedCommit is { } commit =>
            _git.GetFileDiffAsync(RepoPath, commit.Hash, path, CurrentDiffOptions, ct),

        // Staged wins when a file is both: it is the version closest to being permanent.
        AiScanScope.WorkingTree when StagedFiles.Any(f => f.Path == path) =>
            _git.GetStagedDiffAsync(RepoPath, path, CurrentDiffOptions, ct),

        AiScanScope.WorkingTree =>
            _git.GetUnstagedDiffAsync(RepoPath, path, CurrentDiffOptions, ct),

        _ => Task.FromResult(string.Empty),
    };

    /// <summary>
    /// The contents of an untracked file, or empty if it cannot be read.
    ///
    /// The path comes from <c>git status</c>, so it is repository content and untrusted
    /// (commandment 5). It is the one place in the scan that becomes a filesystem path
    /// rather than staying an argument to git, so it is resolved and checked to be inside
    /// the repository before anything is opened — a status entry naming <c>..\..\secrets</c>
    /// must not be read just because git listed it.
    /// </summary>
    private async Task<string> UntrackedContentAsync(string path)
    {
        try
        {
            var root = Path.GetFullPath(RepoPath) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(RepoPath, path));

            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                return string.Empty;

            return await File.ReadAllTextAsync(full);
        }
        catch
        {
            // Binary, locked, or gone since git listed it. The row says so either way.
            return string.Empty;
        }
    }

    /// <summary>Worst first. Done once, at the end — rows reordering as they finish is unreadable.</summary>
    private void RankAiScan()
    {
        var ordered = AiScanFiles
            .OrderByDescending(f => f.Score)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AiScanFiles.Clear();
        foreach (var row in ordered)
            AiScanFiles.Add(row);

        AiScanRisk = ordered.Count == 0 ? ReviewRisk.None : ordered.Max(f => f.Risk);
    }

    /// <summary>
    /// The paragraph at the top, written after everything else and from the real readings.
    ///
    /// <see cref="ChangeSetReviewPrompt"/> is reused rather than replaced: it already takes
    /// a per-file summary alongside the churn, and by this point every file has one. What
    /// was a guess from the shape of the change when it runs beside the graph becomes a
    /// synthesis of forty actual readings here, from the same prompt.
    /// </summary>
    private async Task BuildAiScanOverviewAsync(CancellationToken ct)
    {
        var input = AiScanFiles
            .Select(f => new ChangeSetFile(f.Path, f.Added, f.Removed, [], f.Summary))
            .ToList();

        var result = await _reviewService!.ReviewChangeSetAsync(AiScanScopeLabel, input, ct);
        if (result is null) return;

        AiScanOverview = result.Summary;

        // The file readings saw actual code and this pass did not, so they outrank it. It
        // may raise the verdict — a set can be worse than its worst file — never lower it.
        if (ScanRanking.RiskWeight(result.Risk) > ScanRanking.RiskWeight(AiScanRisk))
            AiScanRisk = result.Risk;
    }

    private string ScanOutcome()
    {
        var dangers = AiScanFiles.Count(f => f.Risk == ReviewRisk.Danger);
        var cautions = AiScanFiles.Count(f => f.Risk == ReviewRisk.Caution);
        var issues = AiScanFiles.Sum(f => f.Issues.Count);

        if (dangers > 0)
            return $"{dangers} file(s) flagged as dangerous, {issues} issue(s) in total.";

        if (cautions > 0 || issues > 0)
            return $"{cautions} file(s) worth a second look, {issues} issue(s) in total.";

        return $"Read {AiScanFiles.Count} file(s) — nothing flagged.";
    }

    /// <summary>
    /// Opens the diff behind a row. The view closes: the point of a scan is to send you to
    /// a file, and leaving it covering the diff would make that a two-click job.
    /// </summary>
    [RelayCommand]
    private void OpenScannedFile(FileReviewViewModel? row)
    {
        if (row is null) return;

        IsAiScanVisible = false;

        if (IsPullRequestVisible)
        {
            var prFile = PrFiles.FirstOrDefault(f => f.FilePath == row.Path);
            if (prFile is not null)
                _ = ShowPrFileDiffAsync(prFile);
            return;
        }

        var file = ChangedFiles.FirstOrDefault(f => f.Path == row.Path)
                   ?? StagedFiles.FirstOrDefault(f => f.Path == row.Path);
        if (file is not null)
            SelectedFile = file;
    }
}
