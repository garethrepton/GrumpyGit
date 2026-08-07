using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrumpyGit.App.Controls;
using GrumpyGit.Core.Git;
using GrumpyGit.Core.Models;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// Partial class — rewriting what is already committed: amend, cherry-pick and reset.
///
/// All three rewrite or move history, so each one either asks first or is a toggle the
/// user has to set deliberately. Reset --hard is the only command in the app that can
/// destroy uncommitted work, and says so in as many words.
/// </summary>
public partial class MainWindowViewModel
{
    // ── Amend ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isAmending;

    /// <summary>
    /// The message loaded from HEAD, so that turning the toggle off again can take it
    /// back out — leaving it behind would silently seed the next ordinary commit with the
    /// previous commit's message.
    /// </summary>
    private string _amendLoadedMessage = string.Empty;

    public string CommitButtonLabel => IsAmending ? "Amend Commit" : "Commit";

    partial void OnIsAmendingChanged(bool value)
    {
        OnPropertyChanged(nameof(CommitButtonLabel));
        _ = SyncAmendMessageAsync(value);
    }

    private async Task SyncAmendMessageAsync(bool amending)
    {
        if (string.IsNullOrEmpty(RepoPath)) return;

        if (!amending)
        {
            if (CommitMessage == _amendLoadedMessage)
                CommitMessage = string.Empty;
            _amendLoadedMessage = string.Empty;
            return;
        }

        if (!string.IsNullOrWhiteSpace(CommitMessage)) return;

        try
        {
            _amendLoadedMessage = await _git.GetHeadCommitMessageAsync(RepoPath);
            CommitMessage = _amendLoadedMessage;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not read the last commit message: {ex.Message}";
        }
    }

    private async Task AmendCommitAsync()
    {
        StatusMessage = "Amending…";
        try
        {
            await _git.AmendCommitAsync(RepoPath, CommitMessage);
            CommitMessage = string.Empty;
            _amendLoadedMessage = string.Empty;
            IsAmending = false;
            await LoadRepoAsync(RepoPath);
            ShowToast("Commit amended", ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"Amend failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    // ── Cherry-pick ───────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task CherryPickCommitAsync(string? commitHash)
    {
        if (string.IsNullOrEmpty(RepoPath) || string.IsNullOrEmpty(commitHash)) return;
        if (commitHash == CommitRowViewModel.WorkingTreeHash) return;

        var commit = Commits.FirstOrDefault(c => c.Hash == commitHash);
        if (commit is null)
        {
            StatusMessage = "Commit not found.";
            return;
        }

        var confirmed = await ShowConfirmationAsync(
            "Cherry-pick Commit?",
            $"This copies the changes from:\n\n{commit.ShortHash} {commit.Subject}\n\nonto '{CurrentBranch}' as a new commit. The original commit stays where it is.");
        if (!confirmed) return;

        StatusMessage = "Cherry-picking…";
        try
        {
            await _git.CherryPickAsync(RepoPath, commitHash);
            await LoadRepoAsync(RepoPath);
            ShowToast($"Cherry-picked {commit.ShortHash}", ToastSeverity.Success);
        }
        catch (GitException ex) when (ex.GitOutput.Contains("conflict", StringComparison.OrdinalIgnoreCase))
        {
            await LoadRepoAsync(RepoPath);
            StatusMessage = "Cherry-pick hit conflicts. Resolve them and commit, or run `git cherry-pick --abort`.";
            ShowToast("Cherry-pick produced conflicts", ToastSeverity.Warning, 6000);
        }
        catch (Exception ex)
        {
            ShowToast($"Cherry-pick failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    // ── Reset ─────────────────────────────────────────────────────────────────

    [RelayCommand]
    private Task ResetSoftAsync(string? commitHash) => ResetToCommitAsync(commitHash, ResetMode.Soft);

    [RelayCommand]
    private Task ResetMixedAsync(string? commitHash) => ResetToCommitAsync(commitHash, ResetMode.Mixed);

    [RelayCommand]
    private Task ResetHardAsync(string? commitHash) => ResetToCommitAsync(commitHash, ResetMode.Hard);

    private async Task ResetToCommitAsync(string? commitHash, ResetMode mode)
    {
        if (string.IsNullOrEmpty(RepoPath) || string.IsNullOrEmpty(commitHash)) return;
        if (commitHash == CommitRowViewModel.WorkingTreeHash) return;

        var commit = Commits.FirstOrDefault(c => c.Hash == commitHash);
        if (commit is null)
        {
            StatusMessage = "Commit not found.";
            return;
        }

        var target = $"{commit.ShortHash} {commit.Subject}";
        var (title, message) = mode switch
        {
            ResetMode.Soft => ("Reset (Soft) to Here?",
                $"'{CurrentBranch}' moves to:\n\n{target}\n\nEverything after it is kept, staged and ready to re-commit."),
            ResetMode.Mixed => ("Reset (Mixed) to Here?",
                $"'{CurrentBranch}' moves to:\n\n{target}\n\nEverything after it stays in your files but is unstaged."),
            _ => ("Reset (Hard) to Here — Discards Work?",
                $"'{CurrentBranch}' moves to:\n\n{target}\n\nEvery later commit and every uncommitted change is destroyed. This cannot be undone from the app."),
        };

        var confirmed = await ShowConfirmationAsync(title, message);
        if (!confirmed) return;

        StatusMessage = "Resetting…";
        try
        {
            await _git.ResetToCommitAsync(RepoPath, commitHash, mode);
            await LoadRepoAsync(RepoPath);
            ShowToast($"Reset to {commit.ShortHash}", ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"Reset failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }
}
