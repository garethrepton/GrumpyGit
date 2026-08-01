# UX Improvements: Features 14, 15, 16

Implementation plan for Repository Tabs, Settings Panel, and Notification Toasts.

---

## Feature 16: Notification Toasts

**Implement this first.** Features 14 and 15 will use toasts for user feedback (e.g. "Settings saved", "Repository opened"), so the toast infrastructure must exist before the other two features.

### 16.1 Create the ToastNotification model

**File:** `src/GrumpyGit.App/ViewModels/ToastNotification.cs` (new)

```csharp
using CommunityToolkit.Mvvm.ComponentModel;

namespace GrumpyGit.App.ViewModels;

public enum ToastSeverity { Info, Success, Warning, Error }

public partial class ToastNotification : ObservableObject
{
    public required string Message { get; init; }
    public required ToastSeverity Severity { get; init; }
    public required string Id { get; init; }

    [ObservableProperty] private double _opacity = 1.0;
}
```

### 16.2 Create the ToastService

**File:** `src/GrumpyGit.App/Services/ToastService.cs` (new)

This is a singleton service that manages the toast collection and auto-dismiss timers.

- Expose `ObservableCollection<ToastNotification> ActiveToasts`.
- `Show(string message, ToastSeverity severity, int durationMs = 4000)`:
  - Creates a `ToastNotification` with a unique `Id` (use `Guid.NewGuid().ToString()`).
  - Adds it to `ActiveToasts`.
  - Starts a `DispatcherTimer` (or `Task.Delay` + `Dispatcher.UIThread.Post`) that after `durationMs` begins a fade-out, then removes the toast.
- `Dismiss(string id)`: removes the toast immediately (for click-to-dismiss).
- Fade-out: animate `Opacity` from 1.0 to 0.0 over ~300ms using a `DispatcherTimer` ticking every 16ms (frame rate), decrementing opacity by ~0.05 per tick. Remove the toast from the collection when opacity hits 0.
- Fade-in: set initial opacity to 0.0 on creation, then animate to 1.0 over ~200ms using the same timer approach.
- Cap `ActiveToasts` at 5 items max. If a 6th toast arrives, remove the oldest.

### 16.3 Create the ToastOverlay control

**File:** `src/GrumpyGit.App/Controls/ToastOverlay.axaml` (new)
**File:** `src/GrumpyGit.App/Controls/ToastOverlay.axaml.cs` (new)

AXAML: A `UserControl` containing an `ItemsControl` bound to `ActiveToasts`:

```
- Panel (HitTestVisible=False on the outer panel so it doesn't block clicks on the app beneath)
  - ItemsControl
    - ItemsPanel: StackPanel, VerticalAlignment=Top, HorizontalAlignment=Right, Margin="0,8,16,0", Spacing=6
    - ItemTemplate: a Border with CornerRadius=6, Padding=12,8, MinWidth=280, MaxWidth=420
      - Bind Background to Severity via a converter or inline logic:
        - Success: #2D4F2D (green-tinted dark)
        - Error:   #5C2A2A (red-tinted dark)
        - Warning: #5C4A2A (amber-tinted dark)
        - Info:    #2A2A5C (blue-tinted dark, matching existing app palette)
      - Bind Opacity to the ToastNotification.Opacity property
      - Content: a Grid with [TextBlock for message, Button "x" for dismiss]
      - On PointerPressed on the Border, call Dismiss
```

The code-behind:
- Has a `ToastService` dependency property (or just accesses it from a static/DI location).
- Wires the dismiss click to `ToastService.Dismiss(notification.Id)`.

### 16.4 Wire ToastService into MainWindowViewModel

**File:** `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs` (modify)

- Add a `public ToastService Toasts { get; } = new();` property.
- Add convenience methods:
  ```csharp
  private void ShowToast(string message, ToastSeverity severity = ToastSeverity.Info)
      => Toasts.Show(message, severity);
  ```
- Replace key `StatusMessage = ...` assignments with toast calls where the message represents a completed operation result:
  - `"Push complete"` -> `ShowToast("Push complete", ToastSeverity.Success)`
  - `"Pull failed: ..."` -> `ShowToast("Pull failed: ...", ToastSeverity.Error)`
  - `"Committed"` -> `ShowToast("Committed successfully", ToastSeverity.Success)`
  - `"Changes discarded."` -> `ShowToast("Changes discarded", ToastSeverity.Success)`
  - `"Stash pop failed: ..."` -> `ShowToast(..., ToastSeverity.Error)`
  - And so on for all command completion messages.
- Keep `StatusMessage` for transient progress states like "Pulling...", "Loading repository..." since those are status, not results.

### 16.5 Add ToastOverlay to MainWindow.axaml

**File:** `src/GrumpyGit.App/Views/MainWindow.axaml` (modify)

Add the overlay inside the root Grid, spanning all rows, at a high ZIndex (same pattern as the confirmation dialog):

```xml
<!-- Toast notifications overlay -->
<controls:ToastOverlay Grid.Row="0" Grid.RowSpan="5"
                       ZIndex="200"
                       IsHitTestVisible="False"
                       DataContext="{Binding Toasts}"/>
```

Place this after the confirmation dialog border (line ~658). The `IsHitTestVisible="False"` on the outer control means it won't block mouse events on the app. The individual toast Borders inside need `IsHitTestVisible="True"` so clicking them works for dismiss.

### 16.6 Add toast styles to MainWindow.axaml

**File:** `src/GrumpyGit.App/Views/MainWindow.axaml` (modify)

Add within `<Window.Styles>`:

```xml
<Style Selector="Border.toast">
    <Setter Property="CornerRadius" Value="6"/>
    <Setter Property="Padding" Value="12,8"/>
    <Setter Property="MinWidth" Value="280"/>
    <Setter Property="MaxWidth" Value="420"/>
    <Setter Property="Margin" Value="0,0,0,4"/>
    <Setter Property="Cursor" Value="Hand"/>
</Style>
```

### Implementation order for Feature 16

1. `ToastNotification.cs` -- model
2. `ToastService.cs` -- logic
3. `ToastOverlay.axaml` + `.cs` -- UI control
4. Add toast styles to `MainWindow.axaml`
5. Add `ToastOverlay` element to `MainWindow.axaml`
6. Add `Toasts` property and `ShowToast` helper to `MainWindowViewModel`
7. Replace `StatusMessage` assignments with `ShowToast` calls for operation results
8. Test: trigger push, pull, commit, discard -- verify toasts appear top-right, stack, auto-dismiss, and are click-dismissable

---

## Feature 15: Settings Panel

### 15.1 Create the AppSettings model

**File:** `src/GrumpyGit.App/Models/AppSettings.cs` (new)

```csharp
namespace GrumpyGit.App.Models;

public class AppSettings
{
    public string DefaultRemote { get; set; } = "origin";
    public string Theme { get; set; } = "dark";          // "dark" or "light"
    public string AccentColor { get; set; } = "#7070C8";  // hex colour
    public double TerminalFontSize { get; set; } = 13;
    public int DiffContextLines { get; set; } = 3;
    public int AutoFetchIntervalSeconds { get; set; } = 0; // 0 = disabled
}
```

### 15.2 Create the SettingsService

**File:** `src/GrumpyGit.App/Services/SettingsService.cs` (new)

Responsibilities:
- Determine the settings file path: `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GrumpyGit", "settings.json")`
- `Load()`: reads and deserialises the JSON file. If missing/corrupt, returns default `AppSettings`.
- `Save(AppSettings settings)`: serialises to JSON and writes to disk. Creates the directory if it doesn't exist.
- Use `System.Text.Json.JsonSerializer` (built-in, no extra package needed).
- Validate values on load: clamp `TerminalFontSize` to [8, 32], `DiffContextLines` to [0, 50], `AutoFetchIntervalSeconds` to [0, 3600].

**Git identity read/write** (also in this service or as helper methods):
- `GetGitIdentityAsync()`: runs `git config --global user.name` and `git config --global user.email` via CliWrap.
- `SetGitIdentityAsync(string name, string email)`: runs `git config --global user.name "..."` and `git config --global user.email "..."`.
- Input validation: reject empty name/email, reject values containing shell metacharacters or newlines.

### 15.3 Create the SettingsViewModel

**File:** `src/GrumpyGit.App/ViewModels/SettingsViewModel.cs` (new)

Properties (all `[ObservableProperty]`):
- `string GitUserName`
- `string GitUserEmail`
- `string DefaultRemote`
- `string SelectedTheme` (bound to a ComboBox with items "Dark", "Light")
- `string AccentColor`
- `double TerminalFontSize`
- `int DiffContextLines`
- `int AutoFetchIntervalSeconds`

Commands:
- `SaveCommand` (`[RelayCommand]`): validates inputs, calls `SettingsService.Save()`, calls `SetGitIdentityAsync()`, fires a toast "Settings saved", closes the panel.
- `CancelCommand`: discards changes, closes the panel.

On construction / `Load()`:
- Reads `AppSettings` from `SettingsService.Load()`.
- Reads git identity from `SettingsService.GetGitIdentityAsync()`.
- Populates all properties.

### 15.4 Add settings overlay to MainWindow.axaml

**File:** `src/GrumpyGit.App/Views/MainWindow.axaml` (modify)

Add a settings panel overlay, using the same visual pattern as the existing confirmation dialog (semi-transparent backdrop, centered card). Place it in the root Grid spanning all rows at ZIndex=150 (above content, below toasts).

```xml
<!-- Settings panel overlay -->
<Border Grid.Row="0" Grid.RowSpan="5"
        ZIndex="150"
        Background="#88000000"
        IsVisible="{Binding IsSettingsVisible}">
    <Border Background="#2A2A3E"
            BorderBrush="#404070"
            BorderThickness="1"
            CornerRadius="8"
            Padding="24,20"
            MaxWidth="520"
            MaxHeight="600"
            HorizontalAlignment="Center"
            VerticalAlignment="Center">
        <ScrollViewer VerticalScrollBarVisibility="Auto">
            <StackPanel Spacing="14" DataContext="{Binding Settings}">
                <TextBlock Text="Settings"
                           Foreground="#E0E0F0"
                           FontSize="18" FontWeight="SemiBold"/>

                <!-- Git Identity section -->
                <TextBlock Text="GIT IDENTITY" Classes="panel-header"/>
                <StackPanel Spacing="6">
                    <TextBlock Text="User Name" Foreground="#A0A0C0" FontSize="11"/>
                    <TextBox Classes="commit-msg" Text="{Binding GitUserName, Mode=TwoWay}"/>
                    <TextBlock Text="Email" Foreground="#A0A0C0" FontSize="11"/>
                    <TextBox Classes="commit-msg" Text="{Binding GitUserEmail, Mode=TwoWay}"/>
                </StackPanel>

                <!-- Default Remote -->
                <TextBlock Text="DEFAULTS" Classes="panel-header"/>
                <StackPanel Spacing="6">
                    <TextBlock Text="Default Remote" Foreground="#A0A0C0" FontSize="11"/>
                    <TextBox Classes="commit-msg" Text="{Binding DefaultRemote, Mode=TwoWay}" Watermark="origin"/>
                </StackPanel>

                <!-- Theme -->
                <TextBlock Text="APPEARANCE" Classes="panel-header"/>
                <StackPanel Spacing="6">
                    <TextBlock Text="Theme" Foreground="#A0A0C0" FontSize="11"/>
                    <ComboBox Classes="branch-combo"
                              ItemsSource="{Binding AvailableThemes}"
                              SelectedItem="{Binding SelectedTheme, Mode=TwoWay}"/>
                    <TextBlock Text="Terminal Font Size" Foreground="#A0A0C0" FontSize="11"/>
                    <NumericUpDown Value="{Binding TerminalFontSize}" Minimum="8" Maximum="32"
                                   Increment="1" FormatString="F0"
                                   Background="#1A1A30" Foreground="#D0D0E8"/>
                </StackPanel>

                <!-- Diff -->
                <TextBlock Text="DIFF" Classes="panel-header"/>
                <StackPanel Spacing="6">
                    <TextBlock Text="Context Lines" Foreground="#A0A0C0" FontSize="11"/>
                    <NumericUpDown Value="{Binding DiffContextLines}" Minimum="0" Maximum="50"
                                   Increment="1" FormatString="F0"
                                   Background="#1A1A30" Foreground="#D0D0E8"/>
                </StackPanel>

                <!-- Auto-fetch -->
                <TextBlock Text="SYNC" Classes="panel-header"/>
                <StackPanel Spacing="6">
                    <TextBlock Text="Auto-fetch interval (seconds, 0 = disabled)" Foreground="#A0A0C0" FontSize="11"/>
                    <NumericUpDown Value="{Binding AutoFetchIntervalSeconds}" Minimum="0" Maximum="3600"
                                   Increment="30" FormatString="F0"
                                   Background="#1A1A30" Foreground="#D0D0E8"/>
                </StackPanel>

                <!-- Buttons -->
                <StackPanel Orientation="Horizontal" Spacing="10"
                            HorizontalAlignment="Right" Margin="0,10,0,0">
                    <Button Classes="toolbar-btn" Content="Cancel"
                            Command="{Binding CancelCommand}" Padding="16,6"/>
                    <Button Content="Save" Padding="16,6" CornerRadius="4" FontSize="13"
                            Background="#3A5C3A" Foreground="#D0F0D0"
                            Command="{Binding SaveCommand}"/>
                </StackPanel>
            </StackPanel>
        </ScrollViewer>
    </Border>
</Border>
```

### 15.5 Wire settings into MainWindowViewModel

**File:** `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs` (modify)

- Add `[ObservableProperty] private bool _isSettingsVisible;`
- Add `public SettingsViewModel Settings { get; }` -- initialise in constructor.
- Add `[RelayCommand] private void OpenSettings()` -- sets `IsSettingsVisible = true`, calls `Settings.Load()`.
- When `Settings.SaveCommand` completes, set `IsSettingsVisible = false` and show a toast.
- When `Settings.CancelCommand` fires, set `IsSettingsVisible = false`.
- Wire `SettingsViewModel` close callbacks: give `SettingsViewModel` an `Action? OnClose` that `MainWindowViewModel` sets to `() => IsSettingsVisible = false`.

### 15.6 Add Settings button to toolbar

**File:** `src/GrumpyGit.App/Views/MainWindow.axaml` (modify)

Add a settings button at the right end of the toolbar (inside the `DockPanel`, after the existing `StackPanel`). Place it `DockPanel.Dock="Right"` so it hugs the right edge:

```xml
<Button DockPanel.Dock="Right"
        Classes="toolbar-btn"
        Content="Settings"
        Command="{Binding OpenSettingsCommand}"
        Margin="6,0,0,0"/>
```

Insert this line right after the opening `<DockPanel LastChildFill="True">` in the toolbar section (before the left-docked StackPanel), since `DockPanel.Dock="Right"` items must be declared before the fill child.

### 15.7 Apply settings to the app

**File:** `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs` (modify)

After settings are saved:
- **DiffContextLines**: store the value and pass it to `GitService` diff methods. Add an optional `contextLines` parameter to `GetFileDiffAsync`, `GetStagedDiffAsync`, `GetUnstagedDiffAsync` in `IGitService`/`GitService` that appends `-U{n}` to the git diff command.
- **TerminalFontSize**: bind the terminal `TextBlock.FontSize` and `TextBox.FontSize` to a property on the VM (sourced from settings).
- **AutoFetchInterval**: start/stop a `DispatcherTimer` that calls `git fetch` periodically. If interval is 0, stop the timer. On tick, run `git fetch --all` silently via `GitService` and show a toast only if new commits were fetched (compare commit count before/after, or parse fetch output).
- **Theme**: for initial implementation, toggling between dark and light means swapping the hardcoded colour constants. This can be done by exposing theme-dependent colour properties on the VM (e.g. `BackgroundMain`, `BackgroundPanel`, `ForegroundText`, etc.) and binding AXAML colours to these properties instead of hardcoded hex values. This is a larger refactor; for v1, support just the dark theme and note "light theme" as a follow-up task.

### 15.8 Add DiffContextLines to GitService

**File:** `src/GrumpyGit.Core/Git/IGitService.cs` (modify)
**File:** `src/GrumpyGit.Core/Git/GitService.cs` (modify)

Add an `int contextLines = 3` parameter to these methods:
- `GetFileDiffAsync(string repoPath, string commitHash, string filePath, int contextLines = 3)`
- `GetStagedDiffAsync(string repoPath, string filePath, int contextLines = 3)`
- `GetUnstagedDiffAsync(string repoPath, string filePath, int contextLines = 3)`

In the implementation, add `-U{contextLines}` to the git diff arguments.

### Implementation order for Feature 15

1. `AppSettings.cs` -- model
2. `SettingsService.cs` -- persistence + git identity
3. `SettingsViewModel.cs` -- VM with load/save
4. Add `IsSettingsVisible`, `Settings` property, `OpenSettingsCommand` to `MainWindowViewModel`
5. Add settings overlay AXAML to `MainWindow.axaml`
6. Add Settings button to toolbar in `MainWindow.axaml`
7. Add `contextLines` parameter to `IGitService`/`GitService` diff methods
8. Wire `DiffContextLines` from settings into `LoadDiffAsync` calls
9. Bind terminal font size to settings value
10. Implement auto-fetch timer
11. Test: open settings, change values, save, verify JSON file in AppData, verify git config changes, verify diff context lines change

---

## Feature 14: Repository Tabs / Recent Repos

This is the most complex feature. It fundamentally changes the app from single-repo to multi-repo by wrapping the existing `MainWindowViewModel` as a per-tab instance.

### 14.1 Create the RecentRepo model

**File:** `src/GrumpyGit.App/Models/RecentRepo.cs` (new)

```csharp
namespace GrumpyGit.App.Models;

public class RecentRepo
{
    public required string Path { get; set; }
    public required string Name { get; set; }      // directory name, for display
    public DateTime LastOpened { get; set; }
}
```

### 14.2 Create the RecentReposService

**File:** `src/GrumpyGit.App/Services/RecentReposService.cs` (new)

- File path: `Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GrumpyGit", "recent-repos.json")`
- `Load()`: returns `List<RecentRepo>`, sorted by `LastOpened` descending. Max 20 entries.
- `AddOrUpdate(string repoPath)`: adds or updates the entry with `LastOpened = DateTime.UtcNow`. Trims list to 20.
- `Remove(string repoPath)`: removes the entry.
- `Save(List<RecentRepo> repos)`: writes JSON.
- Use `System.Text.Json.JsonSerializer`.

### 14.3 Refactor: extract RepoTabViewModel from MainWindowViewModel

**File:** `src/GrumpyGit.App/ViewModels/RepoTabViewModel.cs` (new)

This is the existing `MainWindowViewModel` renamed and refactored. It represents a single open repository tab.

Key changes:
- Rename class to `RepoTabViewModel`.
- Remove the `OwnerWindow` property and settings-related code (those stay on the shell).
- Add `public string TabTitle` computed property: returns the repo directory name, or "New Tab" if no repo is open.
- Add `public bool HasRepo => !string.IsNullOrEmpty(RepoPath);`
- Keep all git operations, commit list, file list, diff viewer, staging, branches, stash, terminal logic.
- The `ToastService` reference: accept it via constructor injection so all tabs share the same toast overlay.
- The `OpenRepoAsync` command: instead of calling the folder picker directly (which needs the Window reference), raise a callback/event that the shell VM handles. Add `Action<RepoTabViewModel>? RequestOpenRepo` callback.
- Similarly, `SettingsService` is shared -- accept via constructor.

In practice, the safest approach is:
1. Copy `MainWindowViewModel.cs` to `RepoTabViewModel.cs`.
2. Rename the class.
3. Change constructor to accept `ToastService toasts, SettingsService settings, Window ownerWindow`.
4. Remove settings overlay properties.
5. Adjust `OpenRepoAsync` to call `RequestOpenRepo?.Invoke(this)` instead of directly opening the picker (or keep the window reference and open the picker directly -- simpler).

Actually, the simpler approach: keep `OwnerWindow` on `RepoTabViewModel` (set by the shell when creating tabs), and let each tab open its own folder picker. This avoids a complex callback system.

### 14.4 Create the new ShellViewModel (new MainWindowViewModel)

**File:** `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs` (modify -- major rewrite)

The new `MainWindowViewModel` becomes a thin shell:

```csharp
public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ToastService _toasts = new();
    private readonly SettingsService _settings = new();
    private readonly RecentReposService _recentRepos = new();

    public ToastService Toasts => _toasts;
    public ObservableCollection<RepoTabViewModel> Tabs { get; } = new();
    public ObservableCollection<RecentRepo> RecentRepos { get; } = new();

    [ObservableProperty] private RepoTabViewModel? _activeTab;
    [ObservableProperty] private bool _isSettingsVisible;
    [ObservableProperty] private bool _isWelcomeVisible = true;
    public SettingsViewModel Settings { get; }
    public Window? OwnerWindow { get; set; }

    // Commands
    [RelayCommand] private void NewTab() { ... }
    [RelayCommand] private async Task OpenRepoInNewTabAsync() { ... }
    [RelayCommand] private void CloseTab(RepoTabViewModel tab) { ... }
    [RelayCommand] private void OpenRecentRepo(RecentRepo repo) { ... }
    [RelayCommand] private void OpenSettings() { ... }
    [RelayCommand] private void NextTab() { ... }  // Ctrl+Tab
    [RelayCommand] private void PrevTab() { ... }  // Ctrl+Shift+Tab
}
```

Logic:
- `NewTab()`: creates a new `RepoTabViewModel`, adds to `Tabs`, sets it as `ActiveTab`. Sets `IsWelcomeVisible = false`.
- `OpenRepoInNewTabAsync()`: opens folder picker, creates new tab, calls `tab.LoadRepoAsync(path)`, adds to `Tabs`, updates `RecentReposService`.
- `CloseTab(tab)`: removes from `Tabs`. If no tabs remain, set `IsWelcomeVisible = true`. If the closed tab was active, switch to the nearest remaining tab.
- `OpenRecentRepo(repo)`: creates a new tab, loads the repo path. If path no longer exists, show error toast and remove from recents.
- `NextTab()` / `PrevTab()`: cycle through tabs. `ActiveTab = Tabs[(currentIndex + 1) % Tabs.Count]`.
- On startup: load recent repos from `RecentReposService.Load()` into `RecentRepos`. Show welcome screen.
- When `ActiveTab` changes: update `IsWelcomeVisible = false` if a tab is active.

### 14.5 Restructure MainWindow.axaml

**File:** `src/GrumpyGit.App/Views/MainWindow.axaml` (modify -- significant restructure)

The root layout becomes:

```
Grid RowDefinitions="Auto,Auto,*,Auto,Auto"
  Row 0: Tab bar (new)
  Row 1: Toolbar (now bound to ActiveTab)
  Row 2: Content area
    - Welcome/Recent Repos screen (visible when IsWelcomeVisible)
    - Tab content (visible when !IsWelcomeVisible), bound to ActiveTab
  Row 3: Terminal (bound to ActiveTab)
  Row 4: Status bar (bound to ActiveTab)
  ZIndex overlays: Settings, Confirmation dialog, Toasts
```

**Tab bar (new, Row 0):**
```xml
<Border Grid.Row="0" Background="#1A1A2C" BorderBrush="#13131F" BorderThickness="0,0,0,1"
        IsVisible="{Binding !IsWelcomeVisible}">
    <DockPanel>
        <Button DockPanel.Dock="Right" Classes="toolbar-btn" Content="+"
                Command="{Binding NewTabCommand}" Margin="4" ToolTip.Tip="New Tab"/>
        <ItemsControl ItemsSource="{Binding Tabs}">
            <ItemsControl.ItemsPanel>
                <ItemsPanelTemplate>
                    <StackPanel Orientation="Horizontal"/>
                </ItemsPanelTemplate>
            </ItemsControl.ItemsPanel>
            <ItemsControl.ItemTemplate>
                <DataTemplate x:DataType="vm:RepoTabViewModel">
                    <Border Background="{Binding IsActive, Converter=...}"
                            Padding="10,6" Cursor="Hand"
                            CornerRadius="4,4,0,0" Margin="2,4,0,0">
                        <StackPanel Orientation="Horizontal" Spacing="6">
                            <TextBlock Text="{Binding TabTitle}" Foreground="#D0D0E8" FontSize="12"/>
                            <Button Content="x" FontSize="10" Padding="2"
                                    Background="Transparent" Foreground="#8080A0"
                                    Command="{Binding DataContext.CloseTabCommand, RelativeSource={RelativeSource AncestorType=Window}}"
                                    CommandParameter="{Binding}"/>
                        </StackPanel>
                    </Border>
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>
    </DockPanel>
</Border>
```

Tab selection: handle `PointerPressed` on each tab Border in code-behind, setting `vm.ActiveTab = clickedTab`.

**Active tab highlighting:** add an `IsActive` property to `RepoTabViewModel` (set by `MainWindowViewModel` when `ActiveTab` changes). Use a background of `#2A2A3E` for active, `#1E1E2C` for inactive.

**Content binding:** the toolbar, branch bar, commit list, file list, diff viewer, and terminal all currently bind to properties directly on `MainWindowViewModel`. After the refactor, these panels need their `DataContext` set to `{Binding ActiveTab}`. Wrap the existing content (rows 1-3 of the old layout) in a `ContentControl` or just set `DataContext="{Binding ActiveTab}"` on the containing Grid.

```xml
<Grid Grid.Row="1" Grid.RowSpan="3" DataContext="{Binding ActiveTab}"
      IsVisible="{Binding DataContext.ActiveTab, RelativeSource={RelativeSource AncestorType=Window}, Converter={x:Static ObjectConverters.IsNotNull}}">
    <!-- Existing toolbar, branch bar, content area, terminal -- unchanged internally -->
</Grid>
```

**Welcome screen:**
```xml
<Border Grid.Row="1" Grid.RowSpan="3"
        Background="#1E1E2E"
        IsVisible="{Binding IsWelcomeVisible}">
    <StackPanel HorizontalAlignment="Center" VerticalAlignment="Center" Spacing="16" MaxWidth="500">
        <TextBlock Text="GrumpyGit" FontSize="28" FontWeight="Bold" Foreground="#D0D0E8"
                   HorizontalAlignment="Center"/>
        <TextBlock Text="Open a repository to get started" Foreground="#7070A0" FontSize="14"
                   HorizontalAlignment="Center"/>

        <Button Classes="toolbar-btn" Content="Open Repository"
                Command="{Binding OpenRepoInNewTabCommand}"
                HorizontalAlignment="Center" Padding="20,8" FontSize="14"/>

        <TextBlock Text="RECENT REPOSITORIES" Classes="panel-header" Margin="0,16,0,0"/>
        <ListBox Background="Transparent" BorderThickness="0"
                 ItemsSource="{Binding RecentRepos}"
                 MaxHeight="300">
            <ListBox.ItemTemplate>
                <DataTemplate x:DataType="models:RecentRepo">
                    <StackPanel Margin="4,2">
                        <TextBlock Text="{Binding Name}" Foreground="#D0D0E8" FontSize="13"/>
                        <TextBlock Text="{Binding Path}" Foreground="#6060A0" FontSize="11"
                                   TextTrimming="CharacterEllipsis"/>
                    </StackPanel>
                </DataTemplate>
            </ListBox.ItemTemplate>
        </ListBox>
    </ListBox>
    </StackPanel>
</Border>
```

Wire the `ListBox.SelectionChanged` or `DoubleTapped` event (or use a command with `CommandParameter`) to call `OpenRecentRepoCommand`.

### 14.6 Add keyboard shortcuts

**File:** `src/GrumpyGit.App/Views/MainWindow.axaml` (modify)

Add `KeyBindings` to the Window:

```xml
<Window.KeyBindings>
    <KeyBinding Gesture="Ctrl+Tab" Command="{Binding NextTabCommand}"/>
    <KeyBinding Gesture="Ctrl+Shift+Tab" Command="{Binding PrevTabCommand}"/>
    <KeyBinding Gesture="Ctrl+T" Command="{Binding NewTabCommand}"/>
    <KeyBinding Gesture="Ctrl+W" Command="{Binding CloseActiveTabCommand}"/>
</Window.KeyBindings>
```

Add `CloseActiveTabCommand` to `MainWindowViewModel`:
```csharp
[RelayCommand]
private void CloseActiveTab()
{
    if (ActiveTab is not null)
        CloseTab(ActiveTab);
}
```

### 14.7 Update MainWindow.axaml.cs

**File:** `src/GrumpyGit.App/Views/MainWindow.axaml.cs` (modify)

Major changes needed:
- Terminal management must become per-tab. Move terminal fields (`_terminal`, `_terminalReadCts`, etc.) into `RepoTabViewModel` or create a `TerminalState` helper class that each `RepoTabViewModel` owns.
- When `ActiveTab` changes, stop the previous tab's terminal read loop display (but keep the process alive in the background), and wire the new tab's terminal to the UI controls.
- Alternatively (simpler): keep terminal management in code-behind but key everything off `ActiveTab`. When switching tabs, save the terminal buffer to the outgoing tab and restore the incoming tab's buffer. Each tab has its own `ConPtyTerminal` instance.
- Drag-drop wiring: this still works as-is because the ListBoxes are the same controls; they just show different data based on `ActiveTab`.

Recommended approach for terminal per-tab:
1. Create a `TerminalSession` class in `GrumpyGit.Core/Terminal/` that encapsulates `ConPtyTerminal`, the buffer `StringBuilder`, the `CancellationTokenSource`, and the read task.
2. `RepoTabViewModel` owns a `TerminalSession?`.
3. `MainWindow.axaml.cs` watches `ActiveTab` changes. On change, disconnect UI from old session, connect to new session, refresh the display.

### 14.8 Update status bar binding

**File:** `src/GrumpyGit.App/Views/MainWindow.axaml` (modify)

The status bar (Row 4) should bind to `ActiveTab` properties. Set `DataContext="{Binding ActiveTab}"` on the status bar Border, or use `{Binding ActiveTab.CurrentBranch}` style paths.

### 14.9 Update RecentRepos when opening a repo

**File:** `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs` (modify)

In `OpenRepoInNewTabAsync` and `OpenRecentRepo`, after successfully loading:
```csharp
_recentRepos.AddOrUpdate(path);
RefreshRecentRepos();
```

Where `RefreshRecentRepos()` reloads `RecentRepos` from the service.

### Implementation order for Feature 14

1. `RecentRepo.cs` -- model
2. `RecentReposService.cs` -- persistence
3. `RepoTabViewModel.cs` -- extract from `MainWindowViewModel` (copy, rename, adjust constructor)
4. Rewrite `MainWindowViewModel.cs` as the shell (tabs collection, active tab, welcome screen, settings, toasts)
5. Create `TerminalSession` helper class
6. Restructure `MainWindow.axaml`:
   a. Add tab bar (Row 0)
   b. Add welcome screen
   c. Wrap existing content area with `DataContext="{Binding ActiveTab}"`
   d. Add keyboard bindings
7. Update `MainWindow.axaml.cs`:
   a. Handle tab click selection
   b. Refactor terminal management to per-tab
   c. Watch `ActiveTab` changes for terminal switching
8. Test:
   a. App launches to welcome screen with recent repos
   b. Open a repo -- tab appears, content loads
   c. Open a second repo -- second tab, independent state
   d. Click tabs to switch -- correct content shown
   e. Ctrl+Tab / Ctrl+Shift+Tab cycles tabs
   f. Close tab -- removed, adjacent tab activated
   g. Close last tab -- welcome screen shown
   h. Recent repos list persisted and clickable

---

## Overall Implementation Order

Implement in this sequence to manage dependencies:

1. **Feature 16 (Toasts)** -- no dependencies, provides infrastructure for the other features
2. **Feature 15 (Settings)** -- depends on toasts for "Settings saved" feedback
3. **Feature 14 (Tabs / Recent Repos)** -- most complex, refactors the entire VM layer; depends on both toasts and settings being complete so they can be wired into the new shell properly

---

## Files Changed Summary

### New Files
| File | Feature |
|---|---|
| `src/GrumpyGit.App/ViewModels/ToastNotification.cs` | 16 |
| `src/GrumpyGit.App/Services/ToastService.cs` | 16 |
| `src/GrumpyGit.App/Controls/ToastOverlay.axaml` | 16 |
| `src/GrumpyGit.App/Controls/ToastOverlay.axaml.cs` | 16 |
| `src/GrumpyGit.App/Models/AppSettings.cs` | 15 |
| `src/GrumpyGit.App/Services/SettingsService.cs` | 15 |
| `src/GrumpyGit.App/ViewModels/SettingsViewModel.cs` | 15 |
| `src/GrumpyGit.App/Models/RecentRepo.cs` | 14 |
| `src/GrumpyGit.App/Services/RecentReposService.cs` | 14 |
| `src/GrumpyGit.App/ViewModels/RepoTabViewModel.cs` | 14 |

### Modified Files
| File | Feature | Scope of Change |
|---|---|---|
| `src/GrumpyGit.App/Views/MainWindow.axaml` | 16, 15, 14 | Add toast overlay, settings overlay, tab bar, welcome screen, keyboard bindings, restructure content binding |
| `src/GrumpyGit.App/Views/MainWindow.axaml.cs` | 14 | Refactor terminal management to per-tab, add tab click handling |
| `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs` | 16, 15, 14 | Add ToastService (F16), add settings (F15), then major rewrite to shell VM (F14) |
| `src/GrumpyGit.Core/Git/IGitService.cs` | 15 | Add `contextLines` parameter to diff methods |
| `src/GrumpyGit.Core/Git/GitService.cs` | 15 | Add `-U{n}` to diff commands |

### No New NuGet Packages Required
All three features use only what is already in the project: Avalonia controls, CommunityToolkit.Mvvm, System.Text.Json (built into .NET 9), and CliWrap (for git config in settings).

---

## Risk Assessment

| Risk | Mitigation |
|---|---|
| Feature 14 VM refactor breaks existing functionality | Extract `RepoTabViewModel` by copying `MainWindowViewModel` first, verify single-tab mode works identically before adding multi-tab |
| Terminal per-tab complexity | Create `TerminalSession` abstraction first, unit test it in isolation |
| Settings file corruption | Always catch `JsonException` on load and fall back to defaults |
| Recent repos pointing to deleted directories | Check `Directory.Exists` before loading; show error toast and remove stale entries |
| Toast animation jank | Use `DispatcherTimer` at 16ms interval (60fps); if performance is an issue, switch to Avalonia's built-in `Animation` system |
| Theme switching (light mode) | Defer full light theme to a follow-up. For v1, just wire the infrastructure so the setting exists and is persisted, but only dark theme colours are implemented |
