using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GrumpyGit.App.Services;
using GrumpyGit.Core.Agents;
using GrumpyGit.Core.LocalModel;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// Partial class — the model library: what the catalogue offers, what this machine has of
/// it, and which one reviews run on.
///
/// The download offer that appears beside a first diff is a one-line version of this; this
/// is the full list in settings, where a model can also be deleted or switched. Deletion
/// is the reason it is its own file rather than more of the review partial: it is the only
/// place the application removes anything from disk, and <see cref="ModelStore"/> is the
/// only thing allowed to do it (commandment 2).
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>
    /// Where downloaded models live. Beneath the app's own folder, shared between the two
    /// editions on purpose — switching from Grumpy to Grumpy AI should not re-download
    /// forty gigabytes.
    /// </summary>
    private ModelStore ModelStore => _modelStore ??= new ModelStore(Path.Combine(AppPaths.Root, "models"));

    private ModelStore? _modelStore;

    public ObservableCollection<ModelChoiceViewModel> ModelLibrary { get; } = new();

    /// <summary>
    /// Total disk the downloaded models occupy, for the line above the list. The number is
    /// the point of the Delete buttons, so it is worth showing without being asked.
    /// </summary>
    public string ModelLibraryFootprint
    {
        get
        {
            var bytes = ModelCatalogue.All.Sum(ModelStore.InstalledBytes);
            return bytes == 0
                ? "No models downloaded."
                : $"{bytes / 1024d / 1024d / 1024d:0.0} GB downloaded in {ModelStore.Directory}";
        }
    }

    private void BuildModelLibrary()
    {
        if (ModelLibrary.Count == 0)
            foreach (var option in ModelCatalogue.All)
                ModelLibrary.Add(new ModelChoiceViewModel(this, option));

        RefreshModelLibrary();
    }

    private void RefreshModelLibrary()
    {
        foreach (var row in ModelLibrary)
        {
            row.IsInstalled = ModelStore.IsInstalled(row.Option);
            row.IsPartial = ModelStore.IsPartiallyInstalled(row.Option);
            row.IsActive = row.IsInstalled && string.Equals(
                _localModelPathInUse, ModelStore.PathFor(row.Option), StringComparison.OrdinalIgnoreCase);
        }

        OnPropertyChanged(nameof(ModelLibraryFootprint));
    }

    /// <summary>
    /// Fetches one row's model and makes it the active one. Only one download runs at a
    /// time: they are gigabytes each, and two at once would take twice as long to give the
    /// user one usable model.
    /// </summary>
    internal async Task DownloadModelChoiceAsync(ModelChoiceViewModel row)
    {
        if (IsDownloadingModel) return;

        _downloadCts = new CancellationTokenSource();
        IsDownloadingModel = true;
        row.IsBusy = true;
        row.Progress = 0;
        row.Status = "Starting…";

        var progress = new Progress<DownloadProgress>(p =>
        {
            row.Progress = p.Fraction;
            row.Status = p.Label;
        });

        try
        {
            var path = await new ModelDownloader()
                .DownloadAsync(row.Option, ModelStore.Directory, progress, _downloadCts.Token);

            SaveActiveModelPath(path);
            ShowToast($"{row.Option.Name} is ready.", Controls.ToastSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ShowToast("Download cancelled.", Controls.ToastSeverity.Info);
        }
        catch (Exception ex)
        {
            ShowToast($"Download failed: {ex.Message}", Controls.ToastSeverity.Error, 8000);
        }
        finally
        {
            row.IsBusy = false;
            row.Status = string.Empty;
            IsDownloadingModel = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
            RefreshModelLibrary();
        }
    }

    /// <summary>
    /// Removes a downloaded model from disk.
    ///
    /// The active model is unloaded first, and that is not a nicety: llama.cpp keeps the
    /// GGUF memory-mapped for as long as the weights are alive, so deleting the file
    /// underneath it fails on Windows. Unloading also leaves local review switched off
    /// rather than pointing at a file that is about to stop existing.
    /// </summary>
    internal void DeleteModelChoice(ModelChoiceViewModel row)
    {
        try
        {
            if (row.IsActive)
                SaveActiveModelPath(string.Empty);

            var freed = ModelStore.Delete(row.Option);

            ShowToast(
                freed == 0
                    ? $"{row.Option.Name} was already gone."
                    : $"Deleted {row.Option.Name} — {freed / 1024d / 1024d / 1024d:0.0} GB freed.",
                Controls.ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowToast($"Could not delete: {ex.Message}", Controls.ToastSeverity.Error, 8000);
        }
        finally
        {
            RefreshModelLibrary();
        }
    }

    /// <summary>Switches reviews onto an already-downloaded model.</summary>
    internal void UseModelChoice(ModelChoiceViewModel row)
    {
        SaveActiveModelPath(ModelStore.PathFor(row.Option));
        ShowToast($"Reviews now run on {row.Option.Name}.", Controls.ToastSeverity.Success);
    }

    /// <summary>
    /// Points the app at a model path — or at none — and writes that down. The one place
    /// the active model changes, so loading, the settings file and the list cannot drift
    /// apart.
    /// </summary>
    private void SaveActiveModelPath(string path)
    {
        SettingsLocalModelPath = path;
        ApplyModuleSetting(ReviewModuleId.Local, path);

        var settings = AppSettings.Load();
        settings.LocalModelPath = path;
        settings.Save();

        RefreshModelLibrary();
    }
}
