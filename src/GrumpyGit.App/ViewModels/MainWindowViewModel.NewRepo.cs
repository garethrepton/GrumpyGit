using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrumpyGit.App.Controls;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// Partial class — getting a repository that does not exist yet: init and clone.
///
/// Everything else in the app assumes a repository is already on disk. These two are the
/// front door, so both end in the same place as opening one by hand:
/// <see cref="OpenRepositoryAsync"/>, which puts it in the tree and loads it.
/// </summary>
public partial class MainWindowViewModel
{
    // ── New repository ────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task InitRepositoryAsync()
    {
        if (OwnerWindow is null) return;

        var results = await OwnerWindow.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "New Repository — choose or create a folder",
                AllowMultiple = false,
            });
        if (results.Count == 0) return;

        var path = results[0].TryGetLocalPath() ?? results[0].Path.LocalPath;

        StatusMessage = "Creating repository…";
        try
        {
            await _git.InitRepositoryAsync(path);
            await OpenRepositoryAsync(path);
            ShowToast("Repository created", ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"Could not create repository: {ex.Message}", ToastSeverity.Error, 6000);
        }
    }

    // ── Clone ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isCloning;
    [ObservableProperty] private string _cloneUrl = string.Empty;
    [ObservableProperty] private string _cloneFolderName = string.Empty;
    [ObservableProperty] private string _cloneParentDirectory = string.Empty;
    [ObservableProperty] private bool _isCloneRunning;

    partial void OnIsCloningChanged(bool value)
        => OnPropertyChanged(nameof(IsBranchBarVisible));

    [RelayCommand]
    private void StartClone()
    {
        CloseActionBars();
        CloneUrl = string.Empty;
        CloneFolderName = string.Empty;

        // Default beside the repository already open — new checkouts almost always join
        // their siblings rather than landing in the home directory.
        if (string.IsNullOrEmpty(CloneParentDirectory))
        {
            var sibling = string.IsNullOrEmpty(RepoPath) ? null : Path.GetDirectoryName(RepoPath);
            CloneParentDirectory = !string.IsNullOrEmpty(sibling) && Directory.Exists(sibling)
                ? sibling
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        IsCloning = true;
    }

    [RelayCommand]
    private async Task ChooseCloneDirectoryAsync()
    {
        if (OwnerWindow is null) return;

        var results = await OwnerWindow.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Clone into…", AllowMultiple = false });
        if (results.Count == 0) return;

        CloneParentDirectory = results[0].TryGetLocalPath() ?? results[0].Path.LocalPath;
    }

    [RelayCommand]
    private async Task ConfirmCloneAsync()
    {
        if (string.IsNullOrWhiteSpace(CloneUrl) || string.IsNullOrWhiteSpace(CloneParentDirectory)) return;

        IsCloneRunning = true;
        StatusMessage = "Cloning…";
        try
        {
            var path = await _git.CloneAsync(
                CloneParentDirectory.Trim(),
                CloneUrl.Trim(),
                string.IsNullOrWhiteSpace(CloneFolderName) ? null : CloneFolderName.Trim());

            IsCloning = false;
            CloneUrl = string.Empty;
            CloneFolderName = string.Empty;
            await OpenRepositoryAsync(path);
            ShowToast("Clone complete", ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"Clone failed: {ex.Message}", ToastSeverity.Error, 8000);
        }
        finally
        {
            IsCloneRunning = false;
        }
    }

    [RelayCommand]
    private void CancelClone()
    {
        IsCloning = false;
        CloneUrl = string.Empty;
        CloneFolderName = string.Empty;
    }
}
