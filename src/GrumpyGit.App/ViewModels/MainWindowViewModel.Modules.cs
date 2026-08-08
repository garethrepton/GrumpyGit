using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrumpyGit.App.Services;
using GrumpyGit.Core.Agents;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// One module on the first-run picker and in settings.
///
/// A viewmodel rather than binding the record directly, because two of the four things a
/// user needs in order to choose are properties of <em>this machine</em> rather than of the
/// module: whether the CLI is installed, and whether it is the one currently in use.
/// </summary>
public partial class ReviewModuleViewModel : ObservableObject
{
    public required ReviewModule Module { get; init; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isInstalled = true;

    public string Name => Module.Name;
    public string Tagline => Module.Tagline;
    public string Requires => Module.Requires;
    public string PrivacyLine => Module.PrivacyLine;
    public bool SendsCodeOffMachine => Module.SendsCodeOffMachine;

    /// <summary>
    /// Shown when the module needs something that is not on this machine yet. Not a
    /// blocker — a user may pick Copilot now and install it after — but saying nothing
    /// would let them choose a module that then silently answers nothing.
    /// </summary>
    public string? MissingHint =>
        IsInstalled ? null : Module.InstallHint;

    public bool ShowsMissingHint => !IsInstalled;

    partial void OnIsInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(MissingHint));
        OnPropertyChanged(nameof(ShowsMissingHint));
    }
}

/// <summary>
/// Partial class — choosing which module reviews diffs, and asking once on first run.
///
/// The pivot this file exists for: the review feature is no longer "a local model, on or
/// off". It is a choice between modules with genuinely different trade-offs — one keeps the
/// code on this machine and is slower, two are faster and better and send the diff to a
/// service the user already pays for. That is a decision only the user can make, so it is
/// asked once, plainly, on first run, and changed in settings.
///
/// <strong>"None" is a first-class answer</strong>, not a dismissal. Plenty of people want a
/// git client with no language model anywhere near it, and picking nothing here leaves the
/// client exactly as it was — no panel, no prompt, no process launched.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>The modules on offer, in catalogue order.</summary>
    public ObservableCollection<ReviewModuleViewModel> Modules { get; } = new();

    /// <summary>
    /// True while the first-run picker is up. Shown once: a user who answered — including
    /// one who answered "none" — is never asked again, and settings holds the door open.
    /// </summary>
    [ObservableProperty] private bool _isModulePickerVisible;

    [ObservableProperty] private ReviewModuleViewModel? _selectedModule;

    /// <summary>
    /// Builds the list and decides whether to ask. Called once at startup, after the saved
    /// module has already been applied — so the list opens with the right row marked.
    /// </summary>
    private void InitialiseModulePicker(AppSettings settings)
    {
        Modules.Clear();

        foreach (var module in ReviewModuleCatalogue.All)
        {
            var row = new ReviewModuleViewModel
            {
                Module = module,
                IsSelected = module.Id == ActiveModuleId,

                // A PATH probe per module, once. Cheap — a handful of File.Exists calls —
                // and it is what turns "GitHub Copilot" from a name into a choice the user
                // can judge: installed, or here is the line to run.
                IsInstalled = IsModuleInstalled(module),
            };

            Modules.Add(row);
        }

        SelectedModule = Modules.FirstOrDefault(m => m.IsSelected);
        IsModulePickerVisible = settings.NeedsModuleChoice;
    }

    /// <summary>
    /// Whether this machine has what the module needs. Only the CLI modules can answer —
    /// the local one needs a download rather than an install, which the model library
    /// already handles.
    /// </summary>
    private static bool IsModuleInstalled(ReviewModule module) =>
        module.Kind != ReviewModuleKind.ExternalCli
        || (module.Executable is { } exe && AgentProcess.Resolve(exe) is not null);

    /// <summary>
    /// Takes the user's pick and writes it down. The only path that changes the module, so
    /// the agent, the settings file and the picker cannot drift apart.
    /// </summary>
    [RelayCommand]
    private void ChooseModule(ReviewModuleViewModel? row)
    {
        if (row is null) return;

        SaveModuleChoice(row.Module.Id);
        IsModulePickerVisible = false;

        // The local module is the one that still needs setting up after being chosen: the
        // panel's own offer to fetch a model takes it from here.
        ShowToast(
            row.Module.Id == ReviewModuleId.Local && !HasLocalModelFile
                ? "Local review chosen — pick a model in the panel to finish setting it up."
                : $"Diffs will be reviewed by {row.Module.Name}.",
            Controls.ToastSeverity.Success);
    }

    /// <summary>
    /// Turns the feature off, from the picker. Distinct from never having been asked, so
    /// the picker does not return next session.
    /// </summary>
    [RelayCommand]
    private void ChooseNoModule()
    {
        SaveModuleChoice(ReviewModuleId.None);
        IsModulePickerVisible = false;
    }

    /// <summary>Reopens the picker from settings, for anyone who changes their mind.</summary>
    [RelayCommand]
    private void ShowModulePicker()
    {
        foreach (var row in Modules)
        {
            row.IsSelected = row.Module.Id == ActiveModuleId;
            row.IsInstalled = IsModuleInstalled(row.Module);
        }

        SelectedModule = Modules.FirstOrDefault(m => m.IsSelected);
        IsModulePickerVisible = true;
    }

    private void SaveModuleChoice(ReviewModuleId module)
    {
        var settings = AppSettings.Load();
        settings.ReviewModule = module == ReviewModuleId.None ? string.Empty : module.ToString();
        settings.ReviewModuleChosen = true;
        settings.Save();

        ApplyModuleSetting(module, settings.LocalModelPath);

        foreach (var row in Modules)
            row.IsSelected = row.Module.Id == module;

        SelectedModule = Modules.FirstOrDefault(m => m.IsSelected);
        OnPropertyChanged(nameof(ActiveModuleName));
        OnPropertyChanged(nameof(ActiveModuleSendsCodeOffMachine));
    }
}
