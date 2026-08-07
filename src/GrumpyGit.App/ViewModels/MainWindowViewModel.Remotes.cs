using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrumpyGit.App.Controls;
using GrumpyGit.Core.Models;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// Partial class — everything that talks to a remote without merging: fetch, checking
/// out someone else's branch, and editing the remotes themselves.
///
/// Fetch exists separately from pull because they answer different questions. Pull asks
/// "give me their work in my tree"; fetch asks "what has happened?" — and until this was
/// added, the second question could only be answered by doing the first.
/// </summary>
public partial class MainWindowViewModel
{
    // ── Fetch ─────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task FetchAsync()
    {
        if (string.IsNullOrEmpty(RepoPath)) return;

        StatusMessage = "Fetching…";
        try
        {
            var remote = await ResolveDefaultRemoteAsync();
            if (remote.Length == 0)
            {
                ShowToast("No remote configured for this repository.", ToastSeverity.Info);
                StatusMessage = "Nothing to fetch — no remote.";
                return;
            }

            await _git.FetchAsync(RepoPath, remote);
            await LoadRepoAsync(RepoPath);
            ShowToast($"Fetched {remote} — remote branches updated", ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"Fetch failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    /// <summary>
    /// Which remote a fetch means. "origin" when it exists, otherwise the only other
    /// candidate — a clone from a fork often has just "upstream", and refusing to fetch
    /// because it is not called origin would be a nonsense.
    /// </summary>
    private async Task<string> ResolveDefaultRemoteAsync()
    {
        var remotes = await _git.GetRemotesAsync(RepoPath);
        if (remotes.Count == 0) return string.Empty;

        return remotes.Any(r => string.Equals(r.Name, "origin", StringComparison.Ordinal))
            ? "origin"
            : remotes[0].Name;
    }

    // ── Remote branches ───────────────────────────────────────────────────────

    public ObservableCollection<string> RemoteBranches { get; } = new();

    [ObservableProperty] private bool _isCheckingOutRemoteBranch;
    [ObservableProperty] private string? _selectedRemoteBranch;

    partial void OnIsCheckingOutRemoteBranchChanged(bool value)
        => OnPropertyChanged(nameof(IsBranchBarVisible));

    /// <summary>
    /// Remote branches with no local counterpart — the ones checking out actually
    /// produces something new. The rest are already in the branch picker.
    /// </summary>
    public bool HasRemoteBranches => RemoteBranches.Count > 0;

    [RelayCommand]
    private void StartCheckoutRemoteBranch()
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        CloseActionBars();
        SelectedRemoteBranch = null;
        IsCheckingOutRemoteBranch = true;
    }

    [RelayCommand]
    private async Task ConfirmCheckoutRemoteBranchAsync()
    {
        var remoteBranch = SelectedRemoteBranch;
        if (string.IsNullOrEmpty(remoteBranch)) return;

        StatusMessage = $"Checking out '{remoteBranch}'…";
        try
        {
            var local = await _git.CheckoutRemoteBranchAsync(RepoPath, remoteBranch);
            IsCheckingOutRemoteBranch = false;
            SelectedRemoteBranch = null;
            await LoadRepoAsync(RepoPath);
            ShowToast($"On '{local}', tracking {remoteBranch}", ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"Checkout failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    [RelayCommand]
    private void CancelCheckoutRemoteBranch()
    {
        IsCheckingOutRemoteBranch = false;
        SelectedRemoteBranch = null;
    }

    private async Task LoadRemoteBranchesAsync()
    {
        RemoteBranches.Clear();
        try
        {
            foreach (var b in await _git.GetRemoteBranchesAsync(RepoPath))
                RemoteBranches.Add(b);
        }
        catch
        {
            // A repository with no remotes answers with nothing to show, not an error.
        }
        OnPropertyChanged(nameof(HasRemoteBranches));
    }

    // ── Remote management ─────────────────────────────────────────────────────

    public ObservableCollection<GitRemote> Remotes { get; } = new();

    [ObservableProperty] private bool _isManagingRemotes;
    [ObservableProperty] private GitRemote? _selectedRemote;
    [ObservableProperty] private string _remoteNameInput = string.Empty;
    [ObservableProperty] private string _remoteUrlInput = string.Empty;

    partial void OnIsManagingRemotesChanged(bool value)
        => OnPropertyChanged(nameof(IsBranchBarVisible));

    /// <summary>Selecting a remote loads it into the two boxes, which then edit it.</summary>
    partial void OnSelectedRemoteChanged(GitRemote? value)
    {
        OnPropertyChanged(nameof(HasSelectedRemote));
        if (value is null) return;
        RemoteNameInput = value.Name;
        RemoteUrlInput = value.Url;
    }

    public bool HasSelectedRemote => SelectedRemote is not null;

    [RelayCommand]
    private async Task StartManageRemotesAsync()
    {
        if (string.IsNullOrEmpty(RepoPath)) return;
        CloseActionBars();
        SelectedRemote = null;
        RemoteNameInput = string.Empty;
        RemoteUrlInput = string.Empty;
        IsManagingRemotes = true;
        await LoadRemotesAsync();
    }

    private async Task LoadRemotesAsync()
    {
        Remotes.Clear();
        try
        {
            foreach (var r in await _git.GetRemotesAsync(RepoPath))
                Remotes.Add(r);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not read remotes: {ex.Message}";
        }
    }

    /// <summary>
    /// One button covers add, rename and re-point: which one it is follows from whether a
    /// remote is selected and whether the name in the box still matches it.
    /// </summary>
    [RelayCommand]
    private async Task SaveRemoteAsync()
    {
        var name = RemoteNameInput.Trim();
        var url = RemoteUrlInput.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) return;

        try
        {
            var exists = Remotes.Any(r => string.Equals(r.Name, name, StringComparison.Ordinal));

            if (SelectedRemote is { } selected && !string.Equals(selected.Name, name, StringComparison.Ordinal))
            {
                if (exists)
                {
                    ShowToast($"A remote called '{name}' already exists.", ToastSeverity.Error);
                    return;
                }
                await _git.RenameRemoteAsync(RepoPath, selected.Name, name);
                exists = true;
            }

            if (exists)
                await _git.SetRemoteUrlAsync(RepoPath, name, url);
            else
                await _git.AddRemoteAsync(RepoPath, name, url);

            await LoadRemotesAsync();
            SelectedRemote = Remotes.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.Ordinal));
            await LoadRepoAsync(RepoPath);
            ShowToast($"Saved remote '{name}'", ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"Save remote failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    [RelayCommand]
    private async Task DeleteRemoteAsync()
    {
        if (SelectedRemote is not { } remote) return;

        var confirmed = await ShowConfirmationAsync(
            "Remove Remote?",
            $"This removes '{remote.Name}' and its remote-tracking branches from this repository.\n\nNothing is deleted on the server.");
        if (!confirmed) return;

        try
        {
            await _git.RemoveRemoteAsync(RepoPath, remote.Name);
            SelectedRemote = null;
            RemoteNameInput = string.Empty;
            RemoteUrlInput = string.Empty;
            await LoadRemotesAsync();
            await LoadRepoAsync(RepoPath);
            ShowToast($"Removed remote '{remote.Name}'", ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"Remove remote failed: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    [RelayCommand]
    private void CancelManageRemotes()
    {
        IsManagingRemotes = false;
        SelectedRemote = null;
        RemoteNameInput = string.Empty;
        RemoteUrlInput = string.Empty;
    }
}
