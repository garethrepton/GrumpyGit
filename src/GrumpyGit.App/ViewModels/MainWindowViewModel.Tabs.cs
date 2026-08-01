using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrumpyGit.App.Services;

namespace GrumpyGit.App.ViewModels;

// Partial class — multi-repository tabs and the quick switcher.
public partial class MainWindowViewModel
{
    public MainWindowViewModel()
    {
        LoadRecentRepos();

        // HasStagedFiles gates the "Unstage all" affordance, and StagedFiles is
        // rebuilt wholesale on every refresh, so it has to be recomputed from the
        // collection rather than set at any single call site.
        StagedFiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasStagedFiles));

        // Reopen last session's repositories. Deferred to the dispatcher so the
        // constructor returns immediately and the window paints before repo I/O
        // begins — otherwise startup blocks on git for every restored tab.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => _ = RestoreOpenTabsAsync(),
            Avalonia.Threading.DispatcherPriority.Background);
    }

    // ── Quick switcher (Ctrl+P) ───────────────────────────────────────────────

    [ObservableProperty] private bool _isQuickSwitchVisible;
    [ObservableProperty] private string _quickSwitchQuery = string.Empty;
    [ObservableProperty] private QuickSwitchEntryViewModel? _selectedQuickSwitchEntry;

    /// <summary>Filtered results shown in the switcher.</summary>
    public ObservableCollection<QuickSwitchEntryViewModel> QuickSwitchResults { get; } = new();

    partial void OnQuickSwitchQueryChanged(string value) => RefreshQuickSwitchResults();

    [RelayCommand]
    private void ToggleQuickSwitch()
    {
        IsQuickSwitchVisible = !IsQuickSwitchVisible;
        if (IsQuickSwitchVisible)
        {
            QuickSwitchQuery = string.Empty;
            RefreshQuickSwitchResults();
        }
    }

    [RelayCommand]
    private void CloseQuickSwitch() => IsQuickSwitchVisible = false;

    /// <summary>
    /// Rebuilds the switcher list: currently-open tabs first (you switch between open
    /// repos far more often than you reopen a closed one), then recents, deduplicated.
    /// </summary>
    private void RefreshQuickSwitchResults()
    {
        QuickSwitchResults.Clear();

        var query = QuickSwitchQuery?.Trim() ?? string.Empty;
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tab in RepoTabs)
        {
            if (!seen.Add(tab.Path)) continue;
            if (!Matches(tab.Path, query)) continue;
            QuickSwitchResults.Add(new QuickSwitchEntryViewModel
            {
                Path = tab.Path,
                IsOpen = true,
                IsActive = tab.IsActive,
            });
        }

        foreach (var recent in RecentRepositories)
        {
            if (!seen.Add(recent)) continue;
            if (!Matches(recent, query)) continue;
            QuickSwitchResults.Add(new QuickSwitchEntryViewModel { Path = recent, IsOpen = false });
        }

        SelectedQuickSwitchEntry = QuickSwitchResults.FirstOrDefault();
    }

    /// <summary>
    /// Subsequence match on the whole path, so "gg" finds "grumpygit" and "src/gg"
    /// narrows by folder — the behaviour people expect from a Ctrl+P palette.
    /// </summary>
    private static bool Matches(string path, string query)
    {
        if (string.IsNullOrEmpty(query)) return true;

        var haystack = path.ToLowerInvariant();
        var needle = query.ToLowerInvariant();

        var h = 0;
        foreach (var c in needle)
        {
            if (c == ' ') continue;
            h = haystack.IndexOf(c, h);
            if (h < 0) return false;
            h++;
        }
        return true;
    }

    [RelayCommand]
    private async Task ActivateQuickSwitchEntryAsync(QuickSwitchEntryViewModel? entry)
    {
        entry ??= SelectedQuickSwitchEntry;
        if (entry is null) return;

        IsQuickSwitchVisible = false;
        AddRepoTab(entry.Path);
        await LoadRepoAsync(entry.Path);
    }

    /// <summary>Moves the switcher selection without leaving the text box.</summary>
    [RelayCommand]
    private void MoveQuickSwitchSelection(string? direction)
    {
        if (QuickSwitchResults.Count == 0) return;

        var index = SelectedQuickSwitchEntry is null
            ? -1
            : QuickSwitchResults.IndexOf(SelectedQuickSwitchEntry);

        index += string.Equals(direction, "up", StringComparison.OrdinalIgnoreCase) ? -1 : 1;

        if (index < 0) index = QuickSwitchResults.Count - 1;
        if (index >= QuickSwitchResults.Count) index = 0;

        SelectedQuickSwitchEntry = QuickSwitchResults[index];
    }

    // ── Tabs ──────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task OpenRepoInNewTabAsync()
    {
        if (OwnerWindow is null) return;
        var results = await OwnerWindow.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Open Git Repository in New Tab", AllowMultiple = false });
        if (results.Count == 0) return;
        var path = results[0].TryGetLocalPath() ?? results[0].Path.LocalPath;
        AddRepoTab(path);
        await LoadRepoAsync(path);
    }

    private void AddRepoTab(string path)
    {
        foreach (var t in RepoTabs) t.IsActive = false;
        var existing = RepoTabs.FirstOrDefault(t =>
            string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.IsActive = true;
            ActiveTab = existing;
        }
        else
        {
            var tab = new RepoTabViewModel { Path = path, IsActive = true };
            RepoTabs.Add(tab);
            ActiveTab = tab;
        }
        HasMultipleTabs = RepoTabs.Count > 1;
        var settings = AppSettings.Load();
        settings.AddRecentRepo(path);
        LoadRecentRepos();
        PersistOpenTabs();
    }

    [RelayCommand]
    private async Task SwitchTabAsync(RepoTabViewModel? tab)
    {
        if (tab == null || tab == ActiveTab) return;
        foreach (var t in RepoTabs) t.IsActive = false;
        tab.IsActive = true;
        ActiveTab = tab;
        PersistOpenTabs();
        await LoadRepoAsync(tab.Path);
    }

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab — cycle through open repositories.</summary>
    [RelayCommand]
    private async Task CycleTabAsync(string? direction)
    {
        if (RepoTabs.Count < 2) return;

        var index = ActiveTab is null ? 0 : RepoTabs.IndexOf(ActiveTab);
        index += string.Equals(direction, "prev", StringComparison.OrdinalIgnoreCase) ? -1 : 1;

        if (index < 0) index = RepoTabs.Count - 1;
        if (index >= RepoTabs.Count) index = 0;

        await SwitchTabAsync(RepoTabs[index]);
    }

    /// <summary>Ctrl+1..9 — jump straight to the nth open repository.</summary>
    [RelayCommand]
    private async Task SwitchToTabIndexAsync(string? indexText)
    {
        if (!int.TryParse(indexText, out var oneBased)) return;
        var index = oneBased - 1;
        if (index < 0 || index >= RepoTabs.Count) return;
        await SwitchTabAsync(RepoTabs[index]);
    }

    [RelayCommand]
    private void CloseTab(RepoTabViewModel? tab)
    {
        if (tab == null) return;
        var wasActive = tab.IsActive;
        RepoTabs.Remove(tab);
        HasMultipleTabs = RepoTabs.Count > 1;

        if (wasActive && RepoTabs.Count > 0)
        {
            var next = RepoTabs[^1];
            next.IsActive = true;
            ActiveTab = next;
            _ = LoadRepoAsync(next.Path);
        }
        else if (RepoTabs.Count == 0)
        {
            ActiveTab = null;
            RepoPath = string.Empty;
            Commits.Clear();
            ChangedFiles.Clear();
            StagedFiles.Clear();
            AiSessions.Clear();
            HasAiSessions = false;
            CurrentBranch = "No repo";
        }

        PersistOpenTabs();
    }

    [RelayCommand]
    private void CloseActiveTab() => CloseTab(ActiveTab);

    [RelayCommand]
    private async Task OpenRecentRepoAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        AddRepoTab(path);
        await LoadRepoAsync(path);
    }

    private void LoadRecentRepos()
    {
        var settings = AppSettings.Load();
        RecentRepositories.Clear();
        foreach (var r in settings.RecentRepositories)
            RecentRepositories.Add(r);
    }

    private void PersistOpenTabs() =>
        AppSettings.Load().SaveOpenRepos(
            RepoTabs.Select(t => t.Path),
            ActiveTab?.Path ?? string.Empty);

    /// <summary>
    /// Restores the tab set from the previous run. Repos that have since been moved or
    /// deleted are skipped silently rather than erroring — a stale entry should not
    /// block startup.
    /// </summary>
    public async Task RestoreOpenTabsAsync()
    {
        var settings = AppSettings.Load();
        var paths = settings.OpenRepositories
            .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
            .ToList();

        if (paths.Count == 0) return;

        foreach (var path in paths)
        {
            if (RepoTabs.Any(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase)))
                continue;
            RepoTabs.Add(new RepoTabViewModel { Path = path });
        }

        HasMultipleTabs = RepoTabs.Count > 1;

        var active = RepoTabs.FirstOrDefault(t =>
                         string.Equals(t.Path, settings.ActiveRepository, StringComparison.OrdinalIgnoreCase))
                     ?? RepoTabs.FirstOrDefault();

        if (active is null) return;

        foreach (var t in RepoTabs) t.IsActive = false;
        active.IsActive = true;
        ActiveTab = active;

        await LoadRepoAsync(active.Path);
    }
}
