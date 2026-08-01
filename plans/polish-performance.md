# Polish & Performance Implementation Plan

Four features: virtualise the commit graph, search commits, ANSI colour in terminal, keyboard shortcuts panel.

---

## Feature 1: Virtualise the Commit Graph

### Problem

The commit list is a `ListBox` bound to `ObservableCollection<CommitRowViewModel> Commits` in `MainWindowViewModel`. All commits are loaded eagerly into memory and all rows are rendered. On repositories with tens of thousands of commits this will cause:

- High memory consumption (every `CommitRowViewModel` plus `GraphSegment` lists).
- Slow initial load (blocking the UI thread while populating the `ObservableCollection`).
- Sluggish scrolling if Avalonia does not virtualise the ListBox by default.

### Current State

- `MainWindow.axaml` lines 320-357: `ListBox x:Name="CommitListBox"` with `ItemsSource="{Binding Commits}"`.
- `MainWindowViewModel.cs` line 40: `public ObservableCollection<CommitRowViewModel> Commits { get; } = new();`.
- `LoadRepoAsync` (line 325) clears and repopulates the entire collection synchronously on the UI thread after `await`.
- `CommitGraphCell` renders per-row graph segments; its `MeasureOverride` uses `TotalLanes` to set width.

### Design

Avalonia 11's `ListBox` already uses `ItemVirtualizingStackPanel` by default, which means off-screen items are not rendered. However, the current code has two problems that defeat this:

1. **All data is loaded upfront.** `GetCommitGraphAsync` fetches every commit from `git log --all`, parses them, runs graph layout, and adds them all to the `ObservableCollection`. For 50k-commit repos this is slow and memory-heavy.
2. **No incremental loading.** When the user scrolls near the bottom, no additional commits are fetched.

The solution is two-part:

**Part A -- Confirm virtualisation is active (quick fix).**
Explicitly set `VirtualizationMode="Simple"` on the `ListBox` (or verify it defaults on). This ensures Avalonia only materialises `ListBoxItem` containers for visible rows.

**Part B -- Incremental / paginated loading.**
Load commits in pages (e.g., 500 at a time) using `git log --skip=N --max-count=500`. Run graph layout incrementally. Fetch the next page when the user scrolls near the end.

### Files to Modify

| File | Change |
|---|---|
| `src/GrumpyGit.Core/Git/IGitService.cs` | Add `GetCommitGraphPageAsync(repoPath, skip, count)` method |
| `src/GrumpyGit.Core/Git/GitService.cs` | Implement paginated `git log` with `--skip` and `--max-count` |
| `src/GrumpyGit.Core/Graph/GraphLayoutEngine.cs` | Add `ComputeIncremental` method that accepts prior state and new commits |
| `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs` | Replace single `LoadRepoAsync` batch with paginated loading; add scroll-triggered fetch |
| `src/GrumpyGit.App/Views/MainWindow.axaml` | Set `VirtualizationMode` on `CommitListBox`; wire `ScrollChanged` for incremental fetch |
| `src/GrumpyGit.App/Views/MainWindow.axaml.cs` | Add `ScrollChanged` handler that calls ViewModel's `LoadMoreCommitsCommand` |

### Step-by-Step Implementation

#### Step 1: Confirm ListBox virtualisation in AXAML

In `MainWindow.axaml`, on the `CommitListBox` (line 320), add explicit virtualisation attributes:

```xml
<ListBox x:Name="CommitListBox"
         Background="Transparent"
         BorderThickness="0"
         ItemsSource="{Binding Commits}"
         SelectedItem="{Binding SelectedCommit, Mode=TwoWay}"
         ScrollViewer.HorizontalScrollBarVisibility="Auto"
         IsVisible="{Binding !!Commits.Count}">
    <ListBox.ItemsPanel>
        <ItemsPanelTemplate>
            <VirtualizingStackPanel />
        </ItemsPanelTemplate>
    </ListBox.ItemsPanel>
    <!-- existing ItemTemplate unchanged -->
</ListBox>
```

This is the default in Avalonia 11, but making it explicit prevents regressions and documents intent.

#### Step 2: Add paginated git log to IGitService

In `IGitService.cs`, add:

```csharp
/// <summary>
/// Returns a page of commits in topological order.
/// </summary>
/// <param name="skip">Number of commits to skip (for pagination).</param>
/// <param name="maxCount">Maximum number of commits to return.</param>
Task<IReadOnlyList<CommitNode>> GetCommitGraphPageAsync(
    string repoPath, int skip, int maxCount, CancellationToken ct = default);

/// <summary>
/// Returns the total number of commits reachable from --all.
/// </summary>
Task<int> GetCommitCountAsync(string repoPath, CancellationToken ct = default);
```

#### Step 3: Implement in GitService

In `GitService.cs`, implement `GetCommitGraphPageAsync`:

```csharp
public async Task<IReadOnlyList<CommitNode>> GetCommitGraphPageAsync(
    string repoPath, int skip, int maxCount, CancellationToken ct = default)
{
    ValidateRepoPath(repoPath);
    const string format = "%H%x00%P%x00%an%x00%ae%x00%ai%x00%D%x00%s%x1E";

    var result = await Cli.Wrap("git")
        .WithArguments(args => args
            .Add("log")
            .Add("--all")
            .Add($"--format={format}")
            .Add("--topo-order")
            .Add($"--skip={skip}")
            .Add($"--max-count={maxCount}"))
        .WithWorkingDirectory(repoPath)
        .WithValidation(CommandResultValidation.None)
        .ExecuteBufferedAsync(ct);

    if (result.ExitCode != 0)
        throw new GitException("git log failed", result.ExitCode, result.StandardError);

    return ParseCommitGraph(result.StandardOutput);
}

public async Task<int> GetCommitCountAsync(string repoPath, CancellationToken ct = default)
{
    ValidateRepoPath(repoPath);
    var result = await Cli.Wrap("git")
        .WithArguments(args => args
            .Add("rev-list")
            .Add("--all")
            .Add("--count"))
        .WithWorkingDirectory(repoPath)
        .WithValidation(CommandResultValidation.None)
        .ExecuteBufferedAsync(ct);

    return int.TryParse(result.StandardOutput.Trim(), out var count) ? count : 0;
}
```

#### Step 4: Add incremental graph layout

In `GraphLayoutEngine.cs`, add:

```csharp
/// <summary>
/// Holds the state needed to resume graph layout across pages.
/// </summary>
public class GraphLayoutState
{
    public List<(string Hash, int Lane)> OpenLanes { get; set; } = new();
    public int NextRow { get; set; }
    public int MaxLane { get; set; }
}

/// <summary>
/// Computes layout for a page of commits, resuming from prior state.
/// Returns nodes for this page and updates <paramref name="state"/> in place.
/// </summary>
public static IReadOnlyList<GraphNode> ComputeIncremental(
    IReadOnlyList<CommitNode> commits, GraphLayoutState state)
```

The implementation is nearly identical to the existing `Compute` method but uses `state.OpenLanes` and `state.NextRow` instead of local variables, and updates `state.MaxLane` as it discovers new lanes.

#### Step 5: Add paginated loading to MainWindowViewModel

Add new fields and methods:

```csharp
private const int PageSize = 500;
private int _totalCommitCount;
private int _loadedCommitCount;
private bool _isLoadingMore;
private GraphLayoutState _graphState = new();

[RelayCommand]
private async Task LoadMoreCommitsAsync()
{
    if (_isLoadingMore || _loadedCommitCount >= _totalCommitCount) return;
    _isLoadingMore = true;
    try
    {
        var page = await _git.GetCommitGraphPageAsync(RepoPath, _loadedCommitCount, PageSize);
        if (page.Count == 0) return;

        var nodes = GraphLayoutEngine.ComputeIncremental(page, _graphState);
        int totalLanes = _graphState.MaxLane + 1;

        // Update TotalLanes on the working-tree row
        if (Commits.Count > 0 && Commits[0].IsWorkingTree)
            Commits[0] = Commits[0] with { TotalLanes = totalLanes };
        // Note: CommitRowViewModel needs to become a record or get a copy method

        foreach (var node in nodes)
            Commits.Add(ToCommitRowViewModel(node, totalLanes));

        _loadedCommitCount += page.Count;
        StatusMessage = $"Loaded {_loadedCommitCount} of {_totalCommitCount} commits";
    }
    finally { _isLoadingMore = false; }
}
```

Modify `LoadRepoAsync` to only load the first page and store `_totalCommitCount`.

#### Step 6: Wire scroll-to-end trigger in code-behind

In `MainWindow.axaml.cs`, in `OnLoaded`:

```csharp
var commitListBox = this.FindControl<ListBox>("CommitListBox");
var scrollViewer = commitListBox?.GetVisualDescendants()
    .OfType<ScrollViewer>().FirstOrDefault();
if (scrollViewer != null)
{
    scrollViewer.ScrollChanged += (_, args) =>
    {
        // When within 200px of the bottom, load more
        if (scrollViewer.Offset.Y + scrollViewer.Viewport.Height
            >= scrollViewer.Extent.Height - 200)
        {
            if (DataContext is MainWindowViewModel vm)
                vm.LoadMoreCommitsCommand.Execute(null);
        }
    };
}
```

#### Step 7: Update TotalLanes retroactively

When a new page reveals more lanes than previously known, all existing `CommitGraphCell` controls need the updated `TotalLanes`. Two approaches:

**Option A (simpler):** Make `TotalLanes` a shared `[ObservableProperty]` on the ViewModel rather than per-row. Bind `CommitGraphCell.TotalLanes` to the ViewModel's property via `ElementName=CommitListBox`. This way updating one property updates all visible cells.

**Option B:** Keep per-row but iterate and update. Worse performance.

Recommended: Option A. Add `[ObservableProperty] private int _graphTotalLanes = 1;` to `MainWindowViewModel`. In `MainWindow.axaml`, bind:

```xml
<controls:CommitGraphCell ...
    TotalLanes="{Binding DataContext.GraphTotalLanes, ElementName=CommitListBox}" />
```

### Testing

- Load a large repo (linux kernel, chromium) and verify smooth scrolling.
- Verify graph continuity across page boundaries (segments connect correctly).
- Verify working-tree row remains at position 0 and is always visible.
- Verify selecting commits across page boundaries works.

---

## Feature 2: Search Commits

### Problem

There is no way to search for commits by message, author, date range, or SHA prefix. Users must scroll through the entire commit list.

### Design

Add a search bar above the commit list. Use `git log` with `--grep`, `--author`, `--after`, `--before`, and SHA prefix lookup for server-side filtering. Show results inline, replacing the normal commit list while a search is active.

The key design decision is **server-side filtering via git log** rather than client-side filtering of loaded commits. This is critical because with paginated loading (Feature 1), not all commits are in memory.

### Files to Create/Modify

| File | Change |
|---|---|
| `src/GrumpyGit.Core/Models/CommitSearchCriteria.cs` | **New file.** Search criteria record |
| `src/GrumpyGit.Core/Git/IGitService.cs` | Add `SearchCommitsAsync` method |
| `src/GrumpyGit.Core/Git/GitService.cs` | Implement `SearchCommitsAsync` using `git log` flags |
| `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs` | Add search properties, commands, debounce logic |
| `src/GrumpyGit.App/Views/MainWindow.axaml` | Add search bar UI above commit list |

### Step-by-Step Implementation

#### Step 1: Create CommitSearchCriteria model

Create `src/GrumpyGit.Core/Models/CommitSearchCriteria.cs`:

```csharp
namespace GrumpyGit.Core.Models;

/// <summary>
/// Criteria for searching commits via git log flags.
/// All fields are optional; only non-null fields are applied.
/// </summary>
public sealed record CommitSearchCriteria
{
    /// <summary>Matches commit message text (maps to --grep).</summary>
    public string? MessagePattern { get; init; }

    /// <summary>Matches author name or email (maps to --author).</summary>
    public string? AuthorPattern { get; init; }

    /// <summary>Only commits after this date (maps to --after).</summary>
    public DateTimeOffset? After { get; init; }

    /// <summary>Only commits before this date (maps to --before).</summary>
    public DateTimeOffset? Before { get; init; }

    /// <summary>SHA prefix to look up a specific commit.</summary>
    public string? ShaPrefix { get; init; }

    /// <summary>Maximum number of results to return.</summary>
    public int MaxCount { get; init; } = 200;

    public bool IsEmpty => string.IsNullOrWhiteSpace(MessagePattern)
                        && string.IsNullOrWhiteSpace(AuthorPattern)
                        && After is null
                        && Before is null
                        && string.IsNullOrWhiteSpace(ShaPrefix);
}
```

#### Step 2: Add SearchCommitsAsync to IGitService

```csharp
/// <summary>
/// Searches commits using git log flags for server-side filtering.
/// </summary>
Task<IReadOnlyList<CommitNode>> SearchCommitsAsync(
    string repoPath, CommitSearchCriteria criteria, CancellationToken ct = default);
```

#### Step 3: Implement in GitService

```csharp
public async Task<IReadOnlyList<CommitNode>> SearchCommitsAsync(
    string repoPath, CommitSearchCriteria criteria, CancellationToken ct = default)
{
    ValidateRepoPath(repoPath);
    const string format = "%H%x00%P%x00%an%x00%ae%x00%ai%x00%D%x00%s%x1E";

    // If searching by SHA prefix, use git log <prefix> directly
    if (!string.IsNullOrWhiteSpace(criteria.ShaPrefix))
    {
        ValidateHash(criteria.ShaPrefix, nameof(criteria.ShaPrefix));
        var shaResult = await Cli.Wrap("git")
            .WithArguments(args => args
                .Add("log")
                .Add($"--format={format}")
                .Add("--max-count=1")
                .Add(criteria.ShaPrefix))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        return shaResult.ExitCode == 0
            ? ParseCommitGraph(shaResult.StandardOutput)
            : Array.Empty<CommitNode>();
    }

    var result = await Cli.Wrap("git")
        .WithArguments(args =>
        {
            args.Add("log").Add("--all").Add($"--format={format}").Add("--topo-order");
            args.Add($"--max-count={criteria.MaxCount}");

            if (!string.IsNullOrWhiteSpace(criteria.MessagePattern))
            {
                args.Add($"--grep={criteria.MessagePattern}");
                args.Add("--regexp-ignore-case");
            }
            if (!string.IsNullOrWhiteSpace(criteria.AuthorPattern))
                args.Add($"--author={criteria.AuthorPattern}");
            if (criteria.After.HasValue)
                args.Add($"--after={criteria.After.Value:yyyy-MM-dd}");
            if (criteria.Before.HasValue)
                args.Add($"--before={criteria.Before.Value:yyyy-MM-dd}");
        })
        .WithWorkingDirectory(repoPath)
        .WithValidation(CommandResultValidation.None)
        .ExecuteBufferedAsync(ct);

    if (result.ExitCode != 0)
        throw new GitException("git log search failed", result.ExitCode, result.StandardError);

    return ParseCommitGraph(result.StandardOutput);
}
```

**Security note:** The `--grep` and `--author` values are passed as arguments (not interpolated into a shell string) because CliWrap handles argument escaping. The SHA prefix is validated against the hex regex.

#### Step 4: Add search state to MainWindowViewModel

```csharp
// ── Search ────────────────────────────────────────────────────────────────

[ObservableProperty] private string _searchQuery = string.Empty;
[ObservableProperty] private bool _isSearchActive;
private CancellationTokenSource? _searchCts;

partial void OnSearchQueryChanged(string value)
{
    // Debounce: cancel previous search, start new one after 300ms
    _searchCts?.Cancel();
    _searchCts = new CancellationTokenSource();

    if (string.IsNullOrWhiteSpace(value))
    {
        ClearSearch();
        return;
    }

    var cts = _searchCts;
    _ = Task.Delay(300, cts.Token).ContinueWith(async _ =>
    {
        if (cts.Token.IsCancellationRequested) return;
        await Dispatcher.UIThread.InvokeAsync(() => ExecuteSearchAsync(value, cts.Token));
    }, TaskScheduler.Default);
}

private async Task ExecuteSearchAsync(string query, CancellationToken ct)
{
    if (string.IsNullOrEmpty(RepoPath)) return;

    IsSearchActive = true;
    StatusMessage = "Searching...";

    try
    {
        // Determine search type based on query format
        var criteria = BuildCriteria(query);
        var results = await _git.SearchCommitsAsync(RepoPath, criteria, ct);

        ct.ThrowIfCancellationRequested();

        // Replace commit list with search results (no graph layout for search)
        Commits.Clear();
        foreach (var node in results)
        {
            Commits.Add(new CommitRowViewModel
            {
                Hash = node.Hash,
                Subject = node.Subject,
                AuthorName = node.AuthorName,
                AuthorDate = node.AuthorDate,
                RefNames = node.RefNames,
                Lane = 0,
                TotalLanes = 1,
                IsMergeCommit = node.ParentHashes.Length > 1
            });
        }

        StatusMessage = $"Found {results.Count} commit(s)";
    }
    catch (OperationCanceledException) { /* debounce cancelled */ }
    catch (Exception ex) { StatusMessage = $"Search error: {ex.Message}"; }
}

/// <summary>
/// Parses a free-text query into structured criteria.
/// - Looks like a hex string (4+ chars): treat as SHA prefix.
/// - Contains "author:" prefix: extract author pattern.
/// - Otherwise: treat as message grep.
/// </summary>
private static CommitSearchCriteria BuildCriteria(string query)
{
    // SHA prefix detection
    if (System.Text.RegularExpressions.Regex.IsMatch(query.Trim(), @"^[0-9a-fA-F]{4,40}$"))
        return new CommitSearchCriteria { ShaPrefix = query.Trim() };

    // Simple prefix parsing: "author:name" or "by:name"
    if (query.StartsWith("author:", StringComparison.OrdinalIgnoreCase))
        return new CommitSearchCriteria { AuthorPattern = query[7..].Trim() };
    if (query.StartsWith("by:", StringComparison.OrdinalIgnoreCase))
        return new CommitSearchCriteria { AuthorPattern = query[3..].Trim() };

    // Default: message search
    return new CommitSearchCriteria { MessagePattern = query.Trim() };
}

[RelayCommand]
private void ClearSearch()
{
    _searchCts?.Cancel();
    SearchQuery = string.Empty;
    IsSearchActive = false;

    // Reload the normal commit list
    if (!string.IsNullOrEmpty(RepoPath))
        _ = LoadRepoAsync(RepoPath);
}
```

#### Step 5: Add search bar to MainWindow.axaml

Insert a search bar between the "COMMIT GRAPH" header and the `ListBox`. Replace the current `Grid RowDefinitions="Auto,*"` inside the commit log border (lines 309-310) with `RowDefinitions="Auto,Auto,*"`:

```xml
<!-- Row 0: header -->
<TextBlock Grid.Row="0" Classes="panel-header" Text="COMMIT GRAPH"/>

<!-- Row 1: search bar -->
<Border Grid.Row="1" Background="#252540" Padding="6,4">
    <Grid ColumnDefinitions="*,Auto">
        <TextBox Grid.Column="0"
                 Text="{Binding SearchQuery, Mode=TwoWay}"
                 Watermark="Search commits (message, author:name, or SHA)..."
                 Background="#1A1A30"
                 Foreground="#D0D0E8"
                 BorderBrush="#3A3A5C"
                 FontFamily="Consolas,Cascadia Code,monospace"
                 FontSize="12"
                 Padding="6,4"
                 CornerRadius="4"/>
        <Button Grid.Column="1"
                Classes="toolbar-btn"
                Content="X"
                Command="{Binding ClearSearchCommand}"
                IsVisible="{Binding IsSearchActive}"
                Margin="4,0,0,0"
                ToolTip.Tip="Clear search"/>
    </Grid>
</Border>

<!-- Row 2: commit list (existing ListBox, move to Grid.Row="2") -->
```

#### Step 6: Keyboard shortcut for search focus

In `MainWindow.axaml.cs`, add a `KeyDown` handler on the window:

```csharp
protected override void OnKeyDown(KeyEventArgs e)
{
    base.OnKeyDown(e);

    // Ctrl+F focuses the search box
    if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
    {
        var searchBox = this.FindControl<TextBox>("CommitSearchBox");
        searchBox?.Focus();
        e.Handled = true;
    }
    // Escape clears search when search box is focused
    if (e.Key == Key.Escape && IsSearchBoxFocused())
    {
        if (DataContext is MainWindowViewModel vm)
            vm.ClearSearchCommand.Execute(null);
        e.Handled = true;
    }
}
```

Give the search TextBox `x:Name="CommitSearchBox"` in AXAML.

### Testing

- Type "fix" and verify debounced search returns commits with "fix" in the message.
- Type "author:John" and verify filtering by author.
- Paste a 7-char hex SHA prefix and verify it finds the specific commit.
- Press Escape to clear and verify normal commit list returns.
- Verify search works correctly with paginated loading from Feature 1.

---

## Feature 3: ANSI Colour Support in Terminal

### Problem

The terminal output strips ANSI escape codes via `StripAnsiEscapes()` in `MainWindow.axaml.cs` (line 250). Coloured output from git, PowerShell, and other tools appears as plain monochrome text.

### Current State

- `MainWindow.axaml` lines 589-599: `TextBlock x:Name="TerminalOutput"` inside a `ScrollViewer`.
- `MainWindow.axaml.cs` line 41: `AnsiEscapeRegex` strips all CSI/OSC sequences.
- `AppendTerminalOutput` (line 220) appends stripped text to a `StringBuilder` and sets `TextBlock.Text`.

### Design

Replace the `TextBlock` with a `SelectableTextBlock` (or a custom approach using `TextBlock.Inlines`) that supports `Run` elements with individual `Foreground` colours. Parse ANSI SGR (Select Graphic Rendition) sequences to extract foreground/background colour information, and emit styled `Run` elements instead of plain text.

**Approach:** Create an `AnsiParser` class in `GrumpyGit.Core` that converts ANSI-escaped text into a sequence of `AnsiTextSegment` records (text + foreground colour + background colour + bold flag). The code-behind converts these segments into Avalonia `Run` elements with appropriate `Foreground` brushes.

This approach avoids pulling Avalonia dependencies into the Core project.

### Files to Create/Modify

| File | Change |
|---|---|
| `src/GrumpyGit.Core/Terminal/AnsiParser.cs` | **New file.** Parses ANSI escape sequences into styled segments |
| `src/GrumpyGit.Core/Terminal/AnsiTextSegment.cs` | **New file.** Record for a segment of text with colour metadata |
| `src/GrumpyGit.App/Views/MainWindow.axaml` | Replace `TextBlock` with `SelectableTextBlock` using `Inlines` |
| `src/GrumpyGit.App/Views/MainWindow.axaml.cs` | Replace `StripAnsiEscapes` + `TextBlock.Text` with parsed coloured `Run` elements |

### Step-by-Step Implementation

#### Step 1: Create AnsiTextSegment model

Create `src/GrumpyGit.Core/Terminal/AnsiTextSegment.cs`:

```csharp
namespace GrumpyGit.Core.Terminal;

/// <summary>
/// A segment of terminal text with ANSI colour attributes resolved.
/// </summary>
public readonly record struct AnsiTextSegment(
    string Text,
    AnsiColor Foreground,
    AnsiColor Background,
    bool IsBold);

/// <summary>Standard ANSI 16-colour palette plus Default (terminal default).</summary>
public enum AnsiColor : byte
{
    Default = 0,
    Black, Red, Green, Yellow, Blue, Magenta, Cyan, White,
    BrightBlack, BrightRed, BrightGreen, BrightYellow,
    BrightBlue, BrightMagenta, BrightCyan, BrightWhite,
    // Extended 256-colour and RGB are mapped to the nearest 16-colour equivalent
    // or stored as a custom value. For simplicity, this implementation handles
    // the 16-colour SGR codes (30-37, 90-97, 40-47, 100-107) and resets.
}
```

#### Step 2: Create AnsiParser

Create `src/GrumpyGit.Core/Terminal/AnsiParser.cs`:

```csharp
namespace GrumpyGit.Core.Terminal;

/// <summary>
/// Parses text containing ANSI CSI SGR escape sequences into a list of
/// <see cref="AnsiTextSegment"/> values with resolved colour attributes.
///
/// Handles: ESC[0m (reset), ESC[1m (bold), ESC[30-37m / ESC[90-97m (fg),
/// ESC[40-47m / ESC[100-107m (bg), and compound sequences like ESC[1;31m.
/// Non-SGR CSI sequences (cursor movement, erase, etc.) and OSC sequences
/// are stripped silently.
/// </summary>
public static class AnsiParser
{
    public static List<AnsiTextSegment> Parse(string input) { ... }
}
```

The parser is a state machine that:
1. Scans character by character.
2. When `\x1B[` is found, reads the parameter bytes and the final byte.
3. If the final byte is `m` (SGR), parses the semicolon-separated parameters to update current foreground, background, and bold state.
4. If the final byte is something else (cursor control, etc.), discards the sequence.
5. Non-escape text is accumulated and emitted as an `AnsiTextSegment` with the current style state.
6. OSC sequences (`\x1B]...\x07` or `\x1B]...\x1B\\`) are stripped entirely.

Colour mapping for SGR parameters:
- `0`: reset all to default
- `1`: bold on
- `22`: bold off
- `30-37`: foreground Black through White
- `39`: foreground default
- `40-47`: background Black through White
- `49`: background default
- `90-97`: bright foreground
- `100-107`: bright background

#### Step 3: Add Avalonia colour mapping in code-behind

In `MainWindow.axaml.cs`, add a static colour lookup:

```csharp
private static readonly Dictionary<AnsiColor, IBrush> AnsiBrushes = new()
{
    [AnsiColor.Default]       = new SolidColorBrush(Color.Parse("#D0D0E8")),
    [AnsiColor.Black]         = new SolidColorBrush(Color.Parse("#45475A")),
    [AnsiColor.Red]           = new SolidColorBrush(Color.Parse("#F38BA8")),
    [AnsiColor.Green]         = new SolidColorBrush(Color.Parse("#A6E3A1")),
    [AnsiColor.Yellow]        = new SolidColorBrush(Color.Parse("#F9E2AF")),
    [AnsiColor.Blue]          = new SolidColorBrush(Color.Parse("#89B4FA")),
    [AnsiColor.Magenta]       = new SolidColorBrush(Color.Parse("#F5C2E7")),
    [AnsiColor.Cyan]          = new SolidColorBrush(Color.Parse("#94E2D5")),
    [AnsiColor.White]         = new SolidColorBrush(Color.Parse("#BAC2DE")),
    [AnsiColor.BrightBlack]   = new SolidColorBrush(Color.Parse("#585B70")),
    [AnsiColor.BrightRed]     = new SolidColorBrush(Color.Parse("#F38BA8")),
    [AnsiColor.BrightGreen]   = new SolidColorBrush(Color.Parse("#A6E3A1")),
    [AnsiColor.BrightYellow]  = new SolidColorBrush(Color.Parse("#F9E2AF")),
    [AnsiColor.BrightBlue]    = new SolidColorBrush(Color.Parse("#89B4FA")),
    [AnsiColor.BrightMagenta] = new SolidColorBrush(Color.Parse("#F5C2E7")),
    [AnsiColor.BrightCyan]    = new SolidColorBrush(Color.Parse("#94E2D5")),
    [AnsiColor.BrightWhite]   = new SolidColorBrush(Color.Parse("#A6ADC8")),
};
```

These colours are from the Catppuccin Mocha palette to match the app's theme.

#### Step 4: Replace TextBlock with SelectableTextBlock

In `MainWindow.axaml`, replace the terminal output `TextBlock` (line 594) with:

```xml
<SelectableTextBlock x:Name="TerminalOutput"
                     FontFamily="Consolas,Cascadia Code,monospace"
                     FontSize="13"
                     Foreground="#D0D0E8"
                     Padding="8,4"
                     TextWrapping="NoWrap"/>
```

`SelectableTextBlock` supports `Inlines` (adding `Run` elements programmatically) and allows the user to select and copy text.

#### Step 5: Replace text append with styled Run elements

Replace the current `UpdateTerminalDisplay` and `AppendTerminalOutput` methods:

```csharp
private void AppendTerminalOutput(string rawText)
{
    // Parse ANSI sequences into styled segments
    var segments = AnsiParser.Parse(rawText);

    // Append to the existing inlines
    var outputBlock = this.FindControl<SelectableTextBlock>("TerminalOutput");
    if (outputBlock == null) return;

    foreach (var seg in segments)
    {
        if (string.IsNullOrEmpty(seg.Text)) continue;

        var run = new Avalonia.Controls.Documents.Run(seg.Text);
        if (seg.Foreground != AnsiColor.Default && AnsiBrushes.TryGetValue(seg.Foreground, out var fg))
            run.Foreground = fg;
        if (seg.IsBold)
            run.FontWeight = Avalonia.Media.FontWeight.Bold;

        outputBlock.Inlines!.Add(run);
    }

    // Trim to max inline count to prevent unbounded memory growth
    while (outputBlock.Inlines!.Count > MaxTerminalLines * 2)
        outputBlock.Inlines.RemoveAt(0);

    // Auto-scroll
    var scrollViewer = this.FindControl<ScrollViewer>("TerminalScrollViewer");
    scrollViewer?.ScrollToEnd();
}
```

Remove the `_terminalBuffer` StringBuilder, `StripAnsiEscapes`, and `UpdateTerminalDisplay` methods. The `StartReadLoop` now passes raw (un-stripped) text to `AppendTerminalOutput`.

#### Step 6: Handle clear screen (CSI 2J)

The `AnsiParser` should detect `ESC[2J` (clear screen) or `ESC[H` (cursor home) and signal to the caller to clear existing content. Add a flag to the segment:

```csharp
public readonly record struct AnsiTextSegment(
    string Text,
    AnsiColor Foreground,
    AnsiColor Background,
    bool IsBold,
    bool ClearScreen = false);
```

In the append method, if any segment has `ClearScreen = true`, clear all existing inlines before appending.

### Testing

- Run `git log --oneline --color=always` in the terminal and verify coloured output.
- Run `ls --color` (or PowerShell equivalent) and verify directory colours.
- Run a command that produces bold text and verify bold rendering.
- Verify that plain text commands still display correctly.
- Verify the line-trimming still prevents unbounded memory growth.
- Verify text selection works in the `SelectableTextBlock`.

---

## Feature 4: Keyboard Shortcuts Panel

### Problem

There is no discoverable way for users to learn available keyboard shortcuts. Shortcuts are hardcoded in various event handlers but not documented in the UI.

### Design

Add a modal overlay panel (similar to the existing confirmation dialog pattern) that lists all keyboard shortcuts grouped by context. Toggle it with `Ctrl+/` (or `F1`). The panel reuses the existing overlay pattern from the confirmation dialog.

### Current Shortcuts (from code analysis)

From `MainWindow.axaml.cs`:
- **Terminal:** Enter (send command), Ctrl+C (interrupt), Up/Down (history), Tab (completion)

Additional shortcuts to wire up as part of this feature:
- **Global:** Ctrl+O (open repo), Ctrl+F (search commits -- from Feature 2), F1/Ctrl+/ (show shortcuts), Ctrl+` (toggle terminal)
- **Commit list:** Up/Down (navigate), Enter (select)
- **Staging:** Ctrl+Enter (commit)
- **Diff viewer:** (no specific shortcuts currently)

### Files to Create/Modify

| File | Change |
|---|---|
| `src/GrumpyGit.App/ViewModels/KeyboardShortcut.cs` | **New file.** Model for a shortcut entry |
| `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs` | Add shortcut panel visibility toggle, shortcut registry |
| `src/GrumpyGit.App/Views/MainWindow.axaml` | Add shortcut panel overlay |
| `src/GrumpyGit.App/Views/MainWindow.axaml.cs` | Add global KeyDown handler for all shortcuts |

### Step-by-Step Implementation

#### Step 1: Create KeyboardShortcut model

Create `src/GrumpyGit.App/ViewModels/KeyboardShortcut.cs`:

```csharp
namespace GrumpyGit.App.ViewModels;

public record KeyboardShortcut(string Keys, string Description);

public record ShortcutGroup(string Context, IReadOnlyList<KeyboardShortcut> Shortcuts);
```

#### Step 2: Add shortcut panel state to MainWindowViewModel

```csharp
[ObservableProperty] private bool _isShortcutPanelVisible;

[RelayCommand]
private void ToggleShortcutPanel() => IsShortcutPanelVisible = !IsShortcutPanelVisible;

public IReadOnlyList<ShortcutGroup> ShortcutGroups { get; } = new List<ShortcutGroup>
{
    new("Global", new List<KeyboardShortcut>
    {
        new("Ctrl+O", "Open repository"),
        new("Ctrl+F", "Search commits"),
        new("Ctrl+`", "Toggle terminal"),
        new("Ctrl+G", "Toggle commit graph"),
        new("F1", "Show keyboard shortcuts"),
        new("Escape", "Close panel / clear search"),
    }),
    new("Staging & Committing", new List<KeyboardShortcut>
    {
        new("Ctrl+Enter", "Commit staged changes"),
    }),
    new("Terminal", new List<KeyboardShortcut>
    {
        new("Enter", "Execute command"),
        new("Ctrl+C", "Send interrupt (SIGINT)"),
        new("Up / Down", "Command history"),
        new("Tab", "Tab completion"),
    }),
};
```

#### Step 3: Add shortcut panel overlay to MainWindow.axaml

Add after the confirmation dialog overlay (after line 658), before the status bar:

```xml
<!-- Keyboard shortcuts panel overlay -->
<Border Grid.Row="0" Grid.RowSpan="5"
        ZIndex="99"
        Background="#88000000"
        IsVisible="{Binding IsShortcutPanelVisible}">
    <Border Background="#2A2A3E"
            BorderBrush="#404070"
            BorderThickness="1"
            CornerRadius="8"
            Padding="24,20"
            MaxWidth="560"
            MaxHeight="500"
            HorizontalAlignment="Center"
            VerticalAlignment="Center">
        <Grid RowDefinitions="Auto,*,Auto">
            <!-- Header -->
            <TextBlock Grid.Row="0"
                       Text="Keyboard Shortcuts"
                       Foreground="#E0E0F0"
                       FontSize="18"
                       FontWeight="SemiBold"
                       Margin="0,0,0,12"/>

            <!-- Shortcut list -->
            <ScrollViewer Grid.Row="1"
                          VerticalScrollBarVisibility="Auto"
                          MaxHeight="380">
                <ItemsControl ItemsSource="{Binding ShortcutGroups}">
                    <ItemsControl.ItemTemplate>
                        <DataTemplate x:DataType="vm:ShortcutGroup"
                                      x:CompileBindings="False">
                            <StackPanel Margin="0,0,0,12">
                                <!-- Group header -->
                                <TextBlock Text="{Binding Context}"
                                           Foreground="#8899DD"
                                           FontSize="13"
                                           FontWeight="SemiBold"
                                           Margin="0,0,0,6"/>
                                <!-- Shortcuts in this group -->
                                <ItemsControl ItemsSource="{Binding Shortcuts}">
                                    <ItemsControl.ItemTemplate>
                                        <DataTemplate x:DataType="vm:KeyboardShortcut"
                                                      x:CompileBindings="False">
                                            <Grid ColumnDefinitions="140,*"
                                                  Margin="8,2">
                                                <Border Grid.Column="0"
                                                        Background="#1A1A30"
                                                        CornerRadius="3"
                                                        Padding="6,2"
                                                        HorizontalAlignment="Left">
                                                    <TextBlock Text="{Binding Keys}"
                                                               Foreground="#A0A0C0"
                                                               FontFamily="Consolas,Cascadia Code,monospace"
                                                               FontSize="12"/>
                                                </Border>
                                                <TextBlock Grid.Column="1"
                                                           Text="{Binding Description}"
                                                           Foreground="#D0D0E8"
                                                           FontSize="12"
                                                           VerticalAlignment="Center"/>
                                            </Grid>
                                        </DataTemplate>
                                    </ItemsControl.ItemTemplate>
                                </ItemsControl>
                            </StackPanel>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>
            </ScrollViewer>

            <!-- Close button -->
            <Button Grid.Row="2"
                    Classes="toolbar-btn"
                    Content="Close (F1)"
                    Command="{Binding ToggleShortcutPanelCommand}"
                    HorizontalAlignment="Right"
                    Margin="0,12,0,0"
                    Padding="16,6"/>
        </Grid>
    </Border>
</Border>
```

#### Step 4: Wire global keyboard shortcuts in MainWindow.axaml.cs

Override `OnKeyDown` on the `MainWindow`:

```csharp
protected override void OnKeyDown(KeyEventArgs e)
{
    base.OnKeyDown(e);
    if (DataContext is not MainWindowViewModel vm) return;

    // F1: Toggle shortcut panel
    if (e.Key == Key.F1)
    {
        vm.ToggleShortcutPanelCommand.Execute(null);
        e.Handled = true;
        return;
    }

    // Escape: close any open panel, or clear search
    if (e.Key == Key.Escape)
    {
        if (vm.IsShortcutPanelVisible)
        {
            vm.IsShortcutPanelVisible = false;
            e.Handled = true;
            return;
        }
        if (vm.IsSearchActive)
        {
            vm.ClearSearchCommand.Execute(null);
            e.Handled = true;
            return;
        }
    }

    // All other shortcuts require no overlay to be open
    if (vm.IsShortcutPanelVisible || vm.IsConfirmDialogVisible) return;

    switch (e.Key)
    {
        // Ctrl+O: Open repo
        case Key.O when e.KeyModifiers.HasFlag(KeyModifiers.Control):
            vm.OpenRepoCommand.Execute(null);
            e.Handled = true;
            break;

        // Ctrl+F: Focus search
        case Key.F when e.KeyModifiers.HasFlag(KeyModifiers.Control):
            this.FindControl<TextBox>("CommitSearchBox")?.Focus();
            e.Handled = true;
            break;

        // Ctrl+`: Toggle terminal
        case Key.OemTilde when e.KeyModifiers.HasFlag(KeyModifiers.Control):
            vm.ToggleConsoleCommand.Execute(null);
            e.Handled = true;
            break;

        // Ctrl+G: Toggle graph
        case Key.G when e.KeyModifiers.HasFlag(KeyModifiers.Control):
            vm.ToggleGraphCommand.Execute(null);
            e.Handled = true;
            break;

        // Ctrl+Enter: Commit (when commit message box is focused)
        case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Control):
            if (vm.IsWorkingTreeSelected && !string.IsNullOrWhiteSpace(vm.CommitMessage))
                vm.CommitCommand.Execute(null);
            e.Handled = true;
            break;
    }
}
```

#### Step 5: Close panel when clicking the backdrop

In the AXAML, add `PointerPressed` on the outer backdrop `Border` to close the panel when the user clicks outside the content area. In code-behind:

```csharp
// In OnLoaded or constructor:
// Wire backdrop click to close shortcut panel
```

Or simpler: just rely on F1/Escape to close it, which is sufficient and avoids complexity.

### Testing

- Press F1 to open the shortcuts panel. Verify all groups and shortcuts display correctly.
- Press F1 again or Escape to close.
- Verify Ctrl+O triggers the folder picker.
- Verify Ctrl+F focuses the search box (from Feature 2).
- Verify Ctrl+` toggles the terminal.
- Verify Ctrl+G toggles the graph.
- Verify Ctrl+Enter commits when a message is typed and working tree is selected.
- Verify shortcuts do not fire when the confirmation dialog is open.
- Verify shortcuts do not fire when the shortcut panel itself is open (except F1/Escape).

---

## Implementation Order

The four features have minimal dependencies on each other. Recommended order:

1. **Feature 4: Keyboard Shortcuts Panel** -- smallest scope, no backend changes, establishes the keyboard shortcut infrastructure that Feature 2 uses (Ctrl+F).

2. **Feature 3: ANSI Colour Support in Terminal** -- self-contained in the terminal subsystem, no interaction with commit/diff code.

3. **Feature 2: Search Commits** -- requires backend changes (GitService) and UI changes, but no dependency on virtualisation.

4. **Feature 1: Virtualise the Commit Graph** -- largest scope, touches the git service, graph engine, view model, and view. Should be done last because it changes the fundamental data loading pattern, which could introduce regressions in commit selection, diff loading, and search.

Features 3 and 4 can be developed in parallel since they touch completely different files. Feature 2 should come before Feature 1 because the search implementation needs to account for paginated loading (or not -- search always does its own `git log` query), and Feature 1's incremental loading changes would complicate the search-results-to-normal-list transition.

### Estimated Complexity

| Feature | New Files | Modified Files | Estimated Effort |
|---|---|---|---|
| 1. Virtualise Commit Graph | 0 | 6 | Large (graph state, pagination, scroll wiring) |
| 2. Search Commits | 1 | 4 | Medium (git log flags, debounce, UI) |
| 3. ANSI Colour Support | 2 | 2 | Medium (parser state machine, Run elements) |
| 4. Keyboard Shortcuts Panel | 1 | 3 | Small (static data, overlay, KeyDown handler) |
