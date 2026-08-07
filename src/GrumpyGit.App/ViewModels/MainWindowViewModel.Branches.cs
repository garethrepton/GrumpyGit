using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrumpyGit.App.Controls;
using GrumpyGit.Core.Git;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// Partial class — renaming and deleting local branches.
///
/// Both share the action bar under the toolbar with create, merge and tag, so each
/// entry point closes the others rather than stacking two editors in one strip.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>
    /// Every inline editor in the action bar. Opening one closes the rest; the bar shows
    /// itself when any is set.
    /// </summary>
    private void CloseActionBars()
    {
        IsCreatingBranch = false;
        IsMerging = false;
        IsCreatingTag = false;
        IsRenamingBranch = false;
        IsDeletingBranch = false;
        IsCheckingOutRemoteBranch = false;
        IsManagingRemotes = false;
        IsCloning = false;
    }

    // ── Rename ────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isRenamingBranch;
    [ObservableProperty] private string _renameBranchNewName = string.Empty;

    partial void OnIsRenamingBranchChanged(bool value)
        => OnPropertyChanged(nameof(IsBranchBarVisible));

    [RelayCommand]
    private void StartRenameBranch()
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        CloseActionBars();
        RenameBranchNewName = CurrentBranch;
        IsRenamingBranch = true;
    }

    [RelayCommand]
    private async Task ConfirmRenameBranchAsync()
    {
        var newName = RenameBranchNewName.Trim();
        if (string.IsNullOrWhiteSpace(newName) || newName == CurrentBranch) return;

        var oldName = CurrentBranch;
        StatusMessage = $"Renaming '{oldName}' → '{newName}'…";
        try
        {
            await _git.RenameBranchAsync(RepoPath, oldName, newName);
            IsRenamingBranch = false;
            RenameBranchNewName = string.Empty;
            await LoadRepoAsync(RepoPath);
            ShowToast($"Renamed to '{newName}'", ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"Rename failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    [RelayCommand]
    private void CancelRenameBranch()
    {
        IsRenamingBranch = false;
        RenameBranchNewName = string.Empty;
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isDeletingBranch;
    [ObservableProperty] private string? _branchToDelete;

    partial void OnIsDeletingBranchChanged(bool value)
        => OnPropertyChanged(nameof(IsBranchBarVisible));

    [RelayCommand]
    private void StartDeleteBranch()
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        CloseActionBars();
        BranchToDelete = null;
        IsDeletingBranch = true;
    }

    [RelayCommand]
    private async Task ConfirmDeleteBranchAsync()
    {
        var branch = BranchToDelete;
        if (string.IsNullOrEmpty(branch)) return;

        var confirmed = await ShowConfirmationAsync(
            "Delete Branch?",
            $"This deletes the local branch '{branch}'.\n\nCommits it shares with another branch are unaffected. A remote branch of the same name is left alone.");
        if (!confirmed) return;

        StatusMessage = $"Deleting '{branch}'…";
        try
        {
            await _git.DeleteBranchAsync(RepoPath, branch);
            await FinishDeleteBranchAsync(branch);
        }
        catch (GitException ex) when (ex.GitOutput.Contains("not fully merged", StringComparison.OrdinalIgnoreCase))
        {
            // git's refusal is the only warning the user gets that work is about to be
            // orphaned, so it is repeated rather than swallowed into a generic failure.
            var force = await ShowConfirmationAsync(
                "Branch Not Merged — Delete Anyway?",
                $"'{branch}' has commits that exist nowhere else. Deleting it leaves them unreachable.\n\nDelete anyway?");
            if (!force) return;

            try
            {
                await _git.DeleteBranchAsync(RepoPath, branch, force: true);
                await FinishDeleteBranchAsync(branch);
            }
            catch (Exception inner)
            {
                ShowToast($"Delete failed: {inner.Message}", ToastSeverity.Error, 6000);
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Delete failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    private async Task FinishDeleteBranchAsync(string branch)
    {
        IsDeletingBranch = false;
        BranchToDelete = null;
        await LoadRepoAsync(RepoPath);
        ShowToast($"Deleted '{branch}'", ToastSeverity.Success);
    }

    [RelayCommand]
    private void CancelDeleteBranch()
    {
        IsDeletingBranch = false;
        BranchToDelete = null;
    }
}
