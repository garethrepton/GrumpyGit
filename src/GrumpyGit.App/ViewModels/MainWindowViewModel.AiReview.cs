using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrumpyGit.App.Services;
using GrumpyGit.Core.Ai;
using GrumpyGit.Core.Models;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// AI session review — the feature this client is built around.
///
/// Agents produce far more commits than humans do, and reviewing them one commit at a
/// time buries the reviewer in intermediate states that were never meant to be read.
/// This groups agent commits into sessions and lets a human review the *net* diff of a
/// session file-by-file, ticking files off as they go.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>Hash → attribution for every commit in the loaded graph.</summary>
    private Dictionary<string, AiAttribution> _aiAttributions = new(StringComparer.Ordinal);

    private ReviewStateStore? _reviewState;

    public ObservableCollection<AiSessionViewModel> AiSessions { get; } = new();

    [ObservableProperty] private bool _isAiReviewVisible;
    [ObservableProperty] private AiSessionViewModel? _selectedAiSession;
    [ObservableProperty] private ReviewFileViewModel? _selectedReviewFile;
    [ObservableProperty] private bool _hasAiSessions;
    [ObservableProperty] private int _aiCommitCount;
    [ObservableProperty] private string _aiSummaryLabel = string.Empty;
    [ObservableProperty] private bool _isLoadingAiSession;

    /// <summary>
    /// Recomputes attribution and sessions for the currently loaded commit list.
    /// Called from repo load, before the commit rows are materialised so that rows
    /// pick up their AI badge.
    /// </summary>
    private void RebuildAiSessions(IReadOnlyList<CommitNode> commits)
    {
        _aiAttributions = new Dictionary<string, AiAttribution>(StringComparer.Ordinal);
        foreach (var c in commits)
        {
            var attribution = AiAttributionDetector.Detect(c);
            if (attribution.IsAi)
                _aiAttributions[c.Hash] = attribution;
        }

        _reviewState = new ReviewStateStore(RepoPath);

        AiSessions.Clear();
        SelectedAiSession = null;
        SelectedReviewFile = null;

        foreach (var session in AiSessionBuilder.Build(commits))
        {
            AiSessions.Add(new AiSessionViewModel
            {
                Session = session,
                CommitRows = session.Commits
                    .Select(c => new CommitRowViewModel
                    {
                        Hash = c.Hash,
                        Subject = c.Subject,
                        AuthorName = c.AuthorName,
                        AuthorDate = c.AuthorDate,
                        RefNames = c.RefNames,
                        AiAgentName = session.AgentName,
                        AiEvidenceDetail = _aiAttributions.TryGetValue(c.Hash, out var a) ? a.Detail : string.Empty,
                    })
                    .ToList(),
            });
        }

        AiCommitCount = _aiAttributions.Count;
        HasAiSessions = AiSessions.Count > 0;
        AiSummaryLabel = HasAiSessions
            ? $"{AiSessions.Count} AI session{(AiSessions.Count == 1 ? "" : "s")} · {AiCommitCount} commit{(AiCommitCount == 1 ? "" : "s")}"
            : "No AI-authored commits found";
    }

    [RelayCommand]
    private void ToggleAiReview()
    {
        IsAiReviewVisible = !IsAiReviewVisible;
        if (IsAiReviewVisible && SelectedAiSession is null && AiSessions.Count > 0)
            _ = SelectAiSessionAsync(AiSessions[0]);
    }

    [RelayCommand]
    private async Task SelectAiSessionAsync(AiSessionViewModel? session)
    {
        SelectedAiSession = session;
        SelectedReviewFile = null;
        if (session is null) return;

        await LoadSessionFilesAsync(session);
    }

    /// <summary>
    /// Loads the net file list for a session — the diff of the session's base against
    /// its head, NOT the union of per-commit changes. A file the agent created and then
    /// deleted within the session should not appear at all.
    /// </summary>
    private async Task LoadSessionFilesAsync(AiSessionViewModel session)
    {
        if (session.FilesLoaded) return;

        IsLoadingAiSession = true;
        try
        {
            IReadOnlyList<FileChange> changes;
            if (session.HasBase)
            {
                changes = await _git.GetCommitRangeFileListAsync(RepoPath, session.BaseHash, session.HeadHash);
            }
            else
            {
                // Root-commit session: there is no base, so the session's own first
                // commit contents are the change.
                changes = await _git.GetFilesChangedInCommitAsync(RepoPath, session.HeadHash);
            }

            var stats = session.HasBase
                ? await _git.GetCommitRangeStatsAsync(RepoPath, session.BaseHash, session.HeadHash)
                : new Dictionary<string, (int Added, int Removed)>(StringComparer.Ordinal);

            session.Files.Clear();
            var totalAdded = 0;
            var totalRemoved = 0;

            foreach (var change in changes)
            {
                stats.TryGetValue(change.Path, out var churn);
                totalAdded += churn.Added;
                totalRemoved += churn.Removed;

                var file = new ReviewFileViewModel
                {
                    FilePath = change.Path,
                    ChangeType = change.Status.ToString(),
                    LinesAdded = churn.Added,
                    LinesRemoved = churn.Removed,
                    IsReviewed = _reviewState?.IsReviewed(session.SessionKey, change.Path) ?? false,
                };

                file.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(ReviewFileViewModel.IsReviewed)) return;
                    _reviewState?.SetReviewed(session.SessionKey, file.FilePath, file.IsReviewed);
                    session.RecalculateReviewed();
                };

                session.Files.Add(file);
            }

            // Highest-risk first — the reviewer's attention is the scarce resource.
            var ordered = session.Files
                .OrderByDescending(f => f.Risk)
                .ThenByDescending(f => f.TotalChurn)
                .ToList();
            session.Files.Clear();
            foreach (var f in ordered)
                session.Files.Add(f);

            session.FileCount = session.Files.Count;
            session.LinesAdded = totalAdded;
            session.LinesRemoved = totalRemoved;
            session.RecalculateReviewed();
            session.FilesLoaded = true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load AI session: {ex.Message}";
        }
        finally
        {
            IsLoadingAiSession = false;
        }
    }

    /// <summary>
    /// Shows the net diff for one file across the whole session.
    /// </summary>
    [RelayCommand]
    private async Task ShowReviewFileDiffAsync(ReviewFileViewModel? file)
    {
        if (file is null || SelectedAiSession is null) return;

        SelectedReviewFile = file;
        LoadNoteForCurrentFile();
        try
        {
            var session = SelectedAiSession;
            var options = CurrentDiffOptions;
            var diff = session.HasBase
                ? await _git.GetCommitRangeFileDiffAsync(
                    RepoPath, session.BaseHash, session.HeadHash, file.FilePath, options)
                : await _git.GetFileDiffAsync(RepoPath, session.HeadHash, file.FilePath, options);

            var parsed = GrumpyGit.Core.Git.UnifiedDiffParser.Parse(diff);

            // An AI-session diff spans a commit range, so its hunks are NOT stageable.
            // Without clearing these, the hunk buttons from the last working-tree file
            // stay alive and reposition themselves over this diff — and clicking one
            // would apply a patch built from the previous file against whatever is on
            // disk now, staging a change the user cannot even see.
            DiffHunks.Clear();

            CurrentDiff = parsed;
            UpdateDiffStats(parsed);
            UpdateStagingAvailability();
            DiffFilePath = file.FilePath;
            IsDiffFromStagedFile = false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load diff: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ToggleFileReviewed(ReviewFileViewModel? file)
    {
        if (file is null) return;
        file.IsReviewed = !file.IsReviewed;
    }

    /// <summary>Marks every file in the selected session reviewed — the "I've seen enough" escape hatch.</summary>
    [RelayCommand]
    private void MarkSessionReviewed()
    {
        if (SelectedAiSession is null) return;
        foreach (var f in SelectedAiSession.Files)
            f.IsReviewed = true;
    }

    [RelayCommand]
    private void ResetSessionReview()
    {
        if (SelectedAiSession is null) return;
        foreach (var f in SelectedAiSession.Files)
            f.IsReviewed = false;
        _reviewState?.ClearSession(SelectedAiSession.SessionKey);
    }

    /// <summary>Jumps to the next unreviewed file, so the reviewer can work without hunting.</summary>
    [RelayCommand]
    private async Task NextUnreviewedFileAsync()
    {
        if (SelectedAiSession is null) return;

        var files = SelectedAiSession.Files;
        var start = SelectedReviewFile is null ? 0 : files.IndexOf(SelectedReviewFile) + 1;

        for (var i = start; i < files.Count; i++)
        {
            if (!files[i].IsReviewed)
            {
                await ShowReviewFileDiffAsync(files[i]);
                return;
            }
        }

        // Wrap around — anything unreviewed before the cursor.
        for (var i = 0; i < Math.Min(start, files.Count); i++)
        {
            if (!files[i].IsReviewed)
            {
                await ShowReviewFileDiffAsync(files[i]);
                return;
            }
        }

        StatusMessage = "All files in this session are reviewed";
    }
}
