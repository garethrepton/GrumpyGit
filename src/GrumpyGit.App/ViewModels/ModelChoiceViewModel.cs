using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrumpyGit.Core.LocalModel;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// One row of the model library: a catalogue entry plus what this machine has of it.
///
/// The row holds no logic of its own — downloading, deleting and switching all touch the
/// loaded model and the settings file, which belong to the window. It exists so the list
/// can bind per-model state (installed, active, mid-download) without the view reaching
/// back through the collection to work any of that out.
/// </summary>
public sealed partial class ModelChoiceViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;

    public ModelChoiceViewModel(MainWindowViewModel owner, ModelOption option)
    {
        _owner = owner;
        Option = option;
    }

    public ModelOption Option { get; }

    public string Name => Option.Name;
    public string Summary => Option.Summary;
    public string SizeLabel => Option.SizeLabel;

    /// <summary>
    /// Weights have to be resident, so a model bigger than physical memory will not load at
    /// all. Said before the download rather than after: the alternative is finding out by
    /// spending an hour and forty-five gigabytes on a file that cannot run.
    ///
    /// A deliberately crude test — it ignores the KV cache and everything else already in
    /// memory, so it under-warns rather than over-warns. Passing it is not a promise the
    /// model will be comfortable, only that it is not hopeless.
    /// </summary>
    public bool ExceedsMachineMemory =>
        Option.SizeBytes >= System.GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    public string MemoryWarning =>
        ExceedsMachineMemory
            ? $"Larger than this machine's memory ({System.GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024d / 1024d / 1024d:0.0} GB) — it will not load."
            : string.Empty;

    /// <summary>Every part present. Anything less will not load.</summary>
    [ObservableProperty] private bool _isInstalled;

    /// <summary>Some parts present — an interrupted multi-file download, still taking disk.</summary>
    [ObservableProperty] private bool _isPartial;

    /// <summary>This is the model reviews are currently running on.</summary>
    [ObservableProperty] private bool _isActive;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = string.Empty;

    public bool CanDownload => !IsInstalled && !IsBusy;
    public bool CanDelete => (IsInstalled || IsPartial) && !IsBusy;
    public bool CanUse => IsInstalled && !IsActive && !IsBusy;

    /// <summary>
    /// The right-hand label: what this row costs, or what it is already spending. A partial
    /// download says so rather than reading as "not downloaded", because the disk is gone
    /// either way and the difference is what the Delete button is for.
    /// </summary>
    public string StateLabel => this switch
    {
        { IsBusy: true } => Status,
        { IsActive: true } => $"In use — {SizeLabel}",
        { IsInstalled: true } => $"Downloaded — {SizeLabel}",
        { IsPartial: true } => "Part-downloaded",
        _ => SizeLabel,
    };

    partial void OnIsInstalledChanged(bool value) => RefreshState();
    partial void OnIsPartialChanged(bool value) => RefreshState();
    partial void OnIsActiveChanged(bool value) => RefreshState();
    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(StateLabel));

    partial void OnIsBusyChanged(bool value) => RefreshState();

    private void RefreshState()
    {
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanUse));
        OnPropertyChanged(nameof(StateLabel));

        DownloadCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        UseCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private Task DownloadAsync() => _owner.DownloadModelChoiceAsync(this);

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete() => _owner.DeleteModelChoice(this);

    [RelayCommand(CanExecute = nameof(CanUse))]
    private void Use() => _owner.UseModelChoice(this);
}
