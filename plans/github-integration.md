# GitHub Integration Plan -- Features 11, 12, 13

This plan covers three features that introduce Octokit-based GitHub integration into GrumpyGit. All three share a common foundation: authenticating with GitHub using tokens retrieved from Git Credential Manager via `git credential fill`, parsing the remote URL to extract owner/repo, and creating a reusable `GitHubService` class in `GrumpyGit.Core`.

Octokit 14.0.0 is already referenced in `GrumpyGit.App.csproj`. It will also need to be added to `GrumpyGit.Core.csproj` since the service layer lives there.

---

## Shared Foundation (implement first)

### Step 0.1: Add Octokit to GrumpyGit.Core

**File:** `src/GrumpyGit.Core/GrumpyGit.Core.csproj`

Add:
```xml
<PackageReference Include="Octokit" Version="14.0.0" />
```

This keeps the GitHub service in the Core project alongside GitService, maintaining the architecture where Core holds all domain logic and external integrations.

---

### Step 0.2: Create GitHub credential helper in GitService

**File:** `src/GrumpyGit.Core/Git/GitService.cs`
**Interface:** `src/GrumpyGit.Core/Git/IGitService.cs`

Add a new method to retrieve the GitHub token from Git Credential Manager:

```csharp
Task<string?> GetGitHubTokenAsync(string repoPath, CancellationToken ct = default);
```

**Implementation approach:**

1. Call `GetRemoteUrlAsync(repoPath)` to get the origin URL.
2. Parse it to extract the host (e.g., `github.com`). Must handle both HTTPS (`https://github.com/owner/repo.git`) and SSH (`git@github.com:owner/repo.git`) URL formats.
3. Pipe credentials request to `git credential fill` via CliWrap stdin:
   ```
   protocol=https
   host=github.com
   ```
4. Parse the stdout for the `password=<token>` line. Git Credential Manager stores the GitHub OAuth/PAT token as the "password" field.
5. Return the token string, or `null` if credential lookup fails.

**Security considerations:**
- Never log or persist the token.
- The token lives only in memory for the lifetime of the Octokit client.
- Do not store it in any ViewModel observable property (it would be visible in debug tooling).

---

### Step 0.3: Create remote URL parser utility

**File (new):** `src/GrumpyGit.Core/Git/GitRemoteParser.cs`

A static utility class that extracts owner and repo name from a git remote URL.

```csharp
public static class GitRemoteParser
{
    /// <summary>
    /// Parses a GitHub remote URL and returns (owner, repo).
    /// Returns null if the URL is not a recognized GitHub URL.
    /// Handles:
    ///   https://github.com/owner/repo.git
    ///   https://github.com/owner/repo
    ///   git@github.com:owner/repo.git
    ///   ssh://git@github.com/owner/repo.git
    /// </summary>
    public static (string Owner, string Repo)? ParseGitHubRemote(string remoteUrl);
}
```

Strip the `.git` suffix if present. Validate that the host is `github.com` (or `github.com` variants). Return `null` for non-GitHub remotes so the UI can gracefully hide GitHub features.

---

### Step 0.4: Create GitHubService

**File (new):** `src/GrumpyGit.Core/GitHub/GitHubService.cs`
**File (new):** `src/GrumpyGit.Core/GitHub/IGitHubService.cs`

This is the central Octokit wrapper. It manages the authenticated `GitHubClient` instance.

```csharp
public interface IGitHubService
{
    /// <summary>
    /// Initializes the service for a specific repo. Must be called after loading a repo.
    /// Returns true if the repo is a GitHub repo and authentication succeeded.
    /// </summary>
    Task<bool> InitializeAsync(string repoPath, CancellationToken ct = default);

    /// <summary>True after successful InitializeAsync for a GitHub-hosted repo.</summary>
    bool IsAvailable { get; }

    string Owner { get; }
    string Repo { get; }

    // -- PR operations (Feature 11 + 12) --
    Task<IReadOnlyList<PullRequestModel>> GetOpenPullRequestsAsync(CancellationToken ct = default);
    Task<PullRequestDetailModel> GetPullRequestDetailAsync(int number, CancellationToken ct = default);
    Task<IReadOnlyList<PullRequestFileModel>> GetPullRequestFilesAsync(int number, CancellationToken ct = default);
    Task<PullRequestModel> CreatePullRequestAsync(string title, string body, string head, string baseBranch, bool isDraft, CancellationToken ct = default);

    // -- Issue operations (Feature 13) --
    Task<IReadOnlyList<IssueModel>> GetOpenIssuesAsync(CancellationToken ct = default);
    Task<IssueModel> GetIssueAsync(int number, CancellationToken ct = default);

    // -- Check runs (Feature 11) --
    Task<IReadOnlyList<CheckRunModel>> GetCheckRunsForRefAsync(string gitRef, CancellationToken ct = default);
}
```

**Implementation details:**

- Constructor takes `IGitService` (for `GetRemoteUrlAsync` and `GetGitHubTokenAsync`).
- `InitializeAsync` calls `GetRemoteUrlAsync` to get the remote, parses it with `GitRemoteParser`, calls `GetGitHubTokenAsync` to get the token, then creates:
  ```csharp
  var client = new GitHubClient(new ProductHeaderValue("GrumpyGit"));
  client.Credentials = new Credentials(token);
  ```
- Cache the `GitHubClient` instance. Re-initialize when the repo path changes.
- Set `UserAgent` to `"GrumpyGit"` via `ProductHeaderValue`.
- All Octokit calls should catch `ApiException` and `AuthorizationException` gracefully.
- Use `ApiOptions` with `PageSize = 100` for list endpoints to reduce round-trips.

---

### Step 0.5: Create shared GitHub models

**File (new):** `src/GrumpyGit.Core/Models/PullRequestModel.cs`

```csharp
public record PullRequestModel(
    int Number,
    string Title,
    string AuthorLogin,
    string AuthorAvatarUrl,
    string State,           // "open", "closed"
    string BaseBranch,
    string HeadBranch,
    string HeadSha,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    bool IsDraft,
    int CommentsCount,
    int ReviewCommentsCount,
    string MergeableState,  // "clean", "dirty", "unstable", "blocked", etc.
    IReadOnlyList<string> Labels,
    IReadOnlyList<PullRequestReviewModel> Reviews);

public record PullRequestReviewModel(
    string ReviewerLogin,
    string State);          // "APPROVED", "CHANGES_REQUESTED", "COMMENTED", "PENDING"

public record PullRequestDetailModel(
    PullRequestModel PullRequest,
    string Body,
    int Additions,
    int Deletions,
    int ChangedFiles,
    string DiffUrl,
    string HtmlUrl);

public record PullRequestFileModel(
    string Filename,
    string Status,          // "added", "removed", "modified", "renamed"
    int Additions,
    int Deletions,
    string Patch);
```

**File (new):** `src/GrumpyGit.Core/Models/IssueModel.cs`

```csharp
public record IssueModel(
    int Number,
    string Title,
    string State,           // "open", "closed"
    string AuthorLogin,
    IReadOnlyList<string> Labels,
    DateTimeOffset CreatedAt);
```

**File (new):** `src/GrumpyGit.Core/Models/CheckRunModel.cs`

```csharp
public record CheckRunModel(
    string Name,
    string Status,          // "queued", "in_progress", "completed"
    string? Conclusion,     // "success", "failure", "neutral", "cancelled", "timed_out", etc.
    string DetailsUrl);
```

---

### Step 0.6: Wire GitHubService into MainWindowViewModel

**File:** `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`

- Add a `GitHubService` field alongside the existing `GitService` field:
  ```csharp
  private readonly GitHubService _github;
  ```
- In `LoadRepoAsync`, after loading branches and remote URL, call:
  ```csharp
  var githubReady = await _github.InitializeAsync(RepoPath);
  ```
- Add an observable property:
  ```csharp
  [ObservableProperty] private bool _isGitHubRepo;
  ```
  Set it from the result of `InitializeAsync`. Use it to show/hide GitHub-specific UI elements (PR button, issue section, etc.).

---

## Feature 11: PR List and Review

### Step 11.1: Create PullRequestListViewModel

**File (new):** `src/GrumpyGit.App/ViewModels/PullRequestListViewModel.cs`

```csharp
public partial class PullRequestListViewModel : ObservableObject
{
    public int Number { get; init; }
    public string Title { get; init; }
    public string AuthorLogin { get; init; }
    public string HeadBranch { get; init; }
    public string BaseBranch { get; init; }
    public bool IsDraft { get; init; }
    public string ReviewState { get; init; }     // "Approved", "Changes Requested", "Pending", ""
    public string ReviewStateColor { get; init; } // hex color for the review badge
    public string StatusSummary { get; init; }    // "2 checks passed", "1 failing", etc.
    public string TimeAgo { get; init; }          // "3 hours ago", "2 days ago"
    public int CommentsCount { get; init; }
    public IReadOnlyList<string> Labels { get; init; }
}
```

### Step 11.2: Create PullRequestDetailViewModel

**File (new):** `src/GrumpyGit.App/ViewModels/PullRequestDetailViewModel.cs`

```csharp
public partial class PullRequestDetailViewModel : ObservableObject
{
    // Header
    public int Number { get; init; }
    public string Title { get; init; }
    public string Body { get; init; }            // Markdown body
    public string AuthorLogin { get; init; }
    public string BaseBranch { get; init; }
    public string HeadBranch { get; init; }
    public bool IsDraft { get; init; }
    public string HtmlUrl { get; init; }

    // Stats
    public int Additions { get; init; }
    public int Deletions { get; init; }
    public int ChangedFilesCount { get; init; }

    // Reviews
    [ObservableProperty] private ObservableCollection<PullRequestReviewViewModel> _reviews = new();

    // Check runs
    [ObservableProperty] private ObservableCollection<CheckRunViewModel> _checkRuns = new();

    // Files
    [ObservableProperty] private ObservableCollection<PullRequestFileViewModel> _files = new();

    // Selected file diff
    [ObservableProperty] private PullRequestFileViewModel? _selectedFile;
    [ObservableProperty] private ParsedDiff? _currentDiff;
}
```

Supporting sub-ViewModels:

```csharp
public class PullRequestReviewViewModel
{
    public string ReviewerLogin { get; init; }
    public string State { get; init; }           // "Approved", "Changes Requested", etc.
    public string StateColor { get; init; }
}

public class CheckRunViewModel
{
    public string Name { get; init; }
    public string Status { get; init; }
    public string? Conclusion { get; init; }
    public string ConclusionColor { get; init; } // green for success, red for failure
    public string DetailsUrl { get; init; }
}

public class PullRequestFileViewModel
{
    public string Filename { get; init; }
    public string Status { get; init; }
    public int Additions { get; init; }
    public int Deletions { get; init; }
    public string Patch { get; init; }
    public string DisplayText => $"[{Status[0].ToString().ToUpper()}] {Filename} (+{Additions} -{Deletions})";
}
```

### Step 11.3: Add PR state and commands to MainWindowViewModel

**File:** `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`

Add the following properties and commands:

```csharp
// PR panel visibility
[ObservableProperty] private bool _isPrPanelVisible;

// PR list
public ObservableCollection<PullRequestListViewModel> OpenPullRequests { get; } = new();

// Selected PR detail
[ObservableProperty] private PullRequestListViewModel? _selectedPullRequest;
[ObservableProperty] private PullRequestDetailViewModel? _prDetail;

[RelayCommand]
private void TogglePrPanel() => IsPrPanelVisible = !IsPrPanelVisible;

[RelayCommand]
private async Task LoadPullRequestsAsync()
{
    if (!_github.IsAvailable) return;
    StatusMessage = "Loading pull requests...";
    try
    {
        var prs = await _github.GetOpenPullRequestsAsync();
        OpenPullRequests.Clear();
        foreach (var pr in prs)
            OpenPullRequests.Add(MapToPrListViewModel(pr));
        StatusMessage = $"{prs.Count} open PR(s)";
    }
    catch (Exception ex)
    {
        StatusMessage = $"Failed to load PRs: {ex.Message}";
    }
}

partial void OnSelectedPullRequestChanged(PullRequestListViewModel? value)
{
    if (value is not null)
        _ = LoadPrDetailAsync(value.Number);
}

private async Task LoadPrDetailAsync(int prNumber)
{
    StatusMessage = "Loading PR details...";
    try
    {
        var detailTask = _github.GetPullRequestDetailAsync(prNumber);
        var filesTask = _github.GetPullRequestFilesAsync(prNumber);
        await Task.WhenAll(detailTask, filesTask);

        var detail = detailTask.Result;
        var files = filesTask.Result;

        // Also fetch check runs for the head SHA
        var checks = await _github.GetCheckRunsForRefAsync(detail.PullRequest.HeadSha);

        PrDetail = MapToPrDetailViewModel(detail, files, checks);
        StatusMessage = string.Empty;
    }
    catch (Exception ex)
    {
        StatusMessage = $"Failed to load PR detail: {ex.Message}";
    }
}
```

When a file is selected in the PR detail view, parse the patch string using `UnifiedDiffParser.Parse()` and display it in the existing DiffViewer. Alternatively, use `git diff origin/<base>...origin/<head> -- <file>` to get the full diff locally if the branches are fetched.

**Diff strategy (important design decision):**

- **Primary approach:** Use the `patch` field from Octokit's `PullRequestFile` response. This is the unified diff for each file, returned directly by the GitHub API. Parse it with `UnifiedDiffParser.Parse()`.
- **Fallback for large diffs:** The GitHub API truncates patches over 300 lines. For truncated patches, fall back to a local `git diff` if the PR branches exist locally: `git diff <base_sha>...<head_sha> -- <filename>`. This requires the commits to be fetched locally (they usually are if the user has pulled recently).
- **Indication:** Show a "diff truncated by GitHub API" notice when the patch is null/empty and the commits are not available locally.

### Step 11.4: Create PR panel AXAML view

**File:** `src/GrumpyGit/App/Views/MainWindow.axaml`

Add a PR panel that appears as an overlay or a new column in the main content area. The recommended approach is a slide-in panel from the right side, similar to how VS Code shows source control panels.

**Layout change to MainWindow.axaml Row 2 content grid:**

The current Row 2 is `Grid ColumnDefinitions="180,4,*"` (branches sidebar + main content). Modify the inner content area (currently `Grid RowDefinitions="*,4,*"` for commits + detail) to add an optional right panel:

```xml
<!-- Wrap the existing Column 2 content in a new Grid -->
<Grid Grid.Column="2" ColumnDefinitions="*,Auto">
    <!-- Existing commit log + detail panel goes in Column 0 -->
    <Grid Grid.Column="0" RowDefinitions="*,4,*">
        <!-- ... existing commit graph, files, diff ... -->
    </Grid>

    <!-- PR Panel (Column 1) — slides in from right -->
    <Border Grid.Column="1"
            Background="#222236"
            BorderBrush="#13131F"
            BorderThickness="1,0,0,0"
            Width="400"
            IsVisible="{Binding IsPrPanelVisible}">
        <Grid RowDefinitions="Auto,Auto,*">
            <!-- Header -->
            <DockPanel Grid.Row="0" Background="#252540">
                <TextBlock Classes="panel-header" Text="PULL REQUESTS" DockPanel.Dock="Left"/>
                <Button Classes="toolbar-btn" Content="Refresh"
                        Command="{Binding LoadPullRequestsCommand}"
                        DockPanel.Dock="Right" Margin="4"/>
            </DockPanel>

            <!-- PR List -->
            <ListBox Grid.Row="1"
                     ItemsSource="{Binding OpenPullRequests}"
                     SelectedItem="{Binding SelectedPullRequest, Mode=TwoWay}"
                     MaxHeight="250"
                     Background="Transparent" BorderThickness="0">
                <ListBox.ItemTemplate>
                    <DataTemplate>
                        <StackPanel Spacing="2" Margin="4,6">
                            <DockPanel>
                                <TextBlock Text="{Binding Title}" FontWeight="SemiBold"
                                           Foreground="#D0D0E8" TextTrimming="CharacterEllipsis"/>
                                <TextBlock Text="{Binding ReviewState}" DockPanel.Dock="Right"
                                           FontSize="10" Margin="8,0,0,0"/>
                            </DockPanel>
                            <StackPanel Orientation="Horizontal" Spacing="8">
                                <TextBlock Text="{Binding AuthorLogin}" FontSize="11"
                                           Foreground="#8080A8"/>
                                <TextBlock Text="{Binding HeadBranch}" FontSize="11"
                                           Foreground="#6080C0"/>
                                <TextBlock Text="{Binding TimeAgo}" FontSize="11"
                                           Foreground="#505080"/>
                            </StackPanel>
                        </StackPanel>
                    </DataTemplate>
                </ListBox.ItemTemplate>
            </ListBox>

            <!-- PR Detail (when a PR is selected) -->
            <ScrollViewer Grid.Row="2" IsVisible="{Binding PrDetail, Converter={x:Static ObjectConverters.IsNotNull}}">
                <StackPanel Margin="8" Spacing="8">
                    <!-- PR title, body, stats, reviews, check runs, file list -->
                    <!-- ... (detailed AXAML below) ... -->
                </StackPanel>
            </ScrollViewer>
        </Grid>
    </Border>
</Grid>
```

**PR Detail section contents (inside the ScrollViewer):**

- **Header area:** PR number + title, author, base <- head branch labels, draft badge.
- **Stats row:** "+{additions} -{deletions}" in green/red, "{changedFiles} files changed".
- **Reviews section:** List of reviewers with colored state badges:
  - Green dot + "Approved"
  - Red dot + "Changes Requested"
  - Yellow dot + "Commented"
- **CI Checks section:** List of check run names with status icons:
  - Green checkmark for "success"
  - Red X for "failure"
  - Yellow spinner for "in_progress"
  - Gray clock for "queued"
  - Each row is clickable (opens `DetailsUrl` in the default browser via `Process.Start`).
- **Files list:** Same layout as the existing changed files panel, but using PR file data. Clicking a file parses the patch and shows it in an embedded DiffViewer or reuses the main DiffViewer.

### Step 11.5: Add "PRs" toolbar button

**File:** `src/GrumpyGit/App/Views/MainWindow.axaml`

Add to the toolbar StackPanel, after the Terminal button:

```xml
<Border Width="1" Background="#404055" Margin="6,2"/>
<Button Classes="toolbar-btn"
        Content="PRs"
        Command="{Binding TogglePrPanelCommand}"
        IsVisible="{Binding IsGitHubRepo}"
        ToolTip.Tip="Toggle Pull Requests panel"/>
```

The button only appears when `IsGitHubRepo` is true.

### Step 11.6: Auto-load PRs on panel open

In `MainWindowViewModel`, add logic in `OnIsPrPanelVisibleChanged`:

```csharp
partial void OnIsPrPanelVisibleChanged(bool value)
{
    if (value && OpenPullRequests.Count == 0 && _github.IsAvailable)
        _ = LoadPullRequestsAsync();
}
```

---

## Feature 12: Create PR from Branch

### Step 12.1: Add ahead/behind detection to GitService

**File:** `src/GrumpyGit.Core/Git/GitService.cs`
**File:** `src/GrumpyGit.Core/Git/IGitService.cs`

Add a method to check if the current branch is ahead of its upstream:

```csharp
Task<(int Ahead, int Behind)> GetAheadBehindAsync(string repoPath, CancellationToken ct = default);
```

**Implementation:**

```csharp
public async Task<(int Ahead, int Behind)> GetAheadBehindAsync(
    string repoPath, CancellationToken ct = default)
{
    ValidateRepoPath(repoPath);

    var result = await Cli.Wrap("git")
        .WithArguments(args => args
            .Add("rev-list")
            .Add("--left-right")
            .Add("--count")
            .Add("HEAD...@{upstream}"))
        .WithWorkingDirectory(repoPath)
        .WithValidation(CommandResultValidation.None)
        .ExecuteBufferedAsync(ct);

    if (result.ExitCode != 0)
        return (0, 0); // No upstream configured

    var parts = result.StandardOutput.Trim().Split('\t');
    if (parts.Length == 2
        && int.TryParse(parts[0], out var ahead)
        && int.TryParse(parts[1], out var behind))
        return (ahead, behind);

    return (0, 0);
}
```

Also add a method to get the latest commit message (for pre-filling the PR title):

```csharp
Task<string> GetLastCommitMessageAsync(string repoPath, CancellationToken ct = default);
```

Implementation: `git log -1 --format=%s`

Also add a method to list remote branches (needed for the base branch selector):

```csharp
Task<IReadOnlyList<string>> GetRemoteBranchesAsync(string repoPath, string remote = "origin", CancellationToken ct = default);
```

Implementation: `git branch -r --list "origin/*" --format="%(refname:short)"`, then strip the `origin/` prefix.

### Step 12.2: Add CreatePullRequest to GitHubService

This is already specified in Step 0.4 interface. The implementation:

```csharp
public async Task<PullRequestModel> CreatePullRequestAsync(
    string title, string body, string head, string baseBranch, bool isDraft,
    CancellationToken ct = default)
{
    var newPr = new NewPullRequest(title, head, baseBranch)
    {
        Body = body,
        Draft = isDraft
    };

    var created = await _client.PullRequest.Create(Owner, Repo, newPr);
    return MapToModel(created);
}
```

**Important:** The `head` parameter should be just the branch name (e.g., `feature/my-branch`), not `owner:branch`, because we are creating a PR within the same repo. If the user has forked, we would need `username:branch`, but that is an edge case to handle later.

### Step 12.3: Create CreatePullRequestViewModel

**File (new):** `src/GrumpyGit.App/ViewModels/CreatePullRequestViewModel.cs`

```csharp
public partial class CreatePullRequestViewModel : ObservableObject
{
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _body = string.Empty;
    [ObservableProperty] private string _baseBranch = string.Empty;
    [ObservableProperty] private string _headBranch = string.Empty;
    [ObservableProperty] private bool _isDraft;
    [ObservableProperty] private bool _isSubmitting;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _resultUrl;      // Set after successful creation
    [ObservableProperty] private bool _isVisible;

    public ObservableCollection<string> AvailableBaseBranches { get; } = new();
}
```

### Step 12.4: Add Create PR commands to MainWindowViewModel

**File:** `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`

```csharp
[ObservableProperty] private CreatePullRequestViewModel _createPrVm = new();
[ObservableProperty] private bool _canCreatePr;  // true when ahead of upstream

// Update in LoadRepoAsync, after loading branches:
private async Task UpdateCanCreatePrAsync()
{
    if (!_github.IsAvailable)
    {
        CanCreatePr = false;
        return;
    }
    var (ahead, _) = await _git.GetAheadBehindAsync(RepoPath);
    CanCreatePr = ahead > 0;
}

[RelayCommand]
private async Task StartCreatePrAsync()
{
    if (!_github.IsAvailable) return;

    // Pre-fill the form
    var lastMsg = await _git.GetLastCommitMessageAsync(RepoPath);
    var remoteBranches = await _git.GetRemoteBranchesAsync(RepoPath);

    CreatePrVm = new CreatePullRequestViewModel
    {
        Title = lastMsg,
        HeadBranch = CurrentBranch,
        IsVisible = true
    };

    CreatePrVm.AvailableBaseBranches.Clear();
    foreach (var b in remoteBranches)
        CreatePrVm.AvailableBaseBranches.Add(b);

    // Default base branch: "main" if it exists, else "master", else first branch
    CreatePrVm.BaseBranch = remoteBranches.Contains("main") ? "main"
        : remoteBranches.Contains("master") ? "master"
        : remoteBranches.FirstOrDefault() ?? "main";
}

[RelayCommand]
private async Task SubmitCreatePrAsync()
{
    var vm = CreatePrVm;
    if (string.IsNullOrWhiteSpace(vm.Title)) return;

    vm.IsSubmitting = true;
    vm.ErrorMessage = null;

    try
    {
        // Ensure branch is pushed first
        await _git.PushAsync(RepoPath);

        var pr = await _github.CreatePullRequestAsync(
            vm.Title, vm.Body, vm.HeadBranch, vm.BaseBranch, vm.IsDraft);

        vm.ResultUrl = $"https://github.com/{_github.Owner}/{_github.Repo}/pull/{pr.Number}";
        StatusMessage = $"PR #{pr.Number} created";

        // Refresh PR list if panel is open
        if (IsPrPanelVisible)
            await LoadPullRequestsAsync();
    }
    catch (Exception ex)
    {
        vm.ErrorMessage = ex.Message;
    }
    finally
    {
        vm.IsSubmitting = false;
    }
}

[RelayCommand]
private void CancelCreatePr()
{
    CreatePrVm.IsVisible = false;
}

[RelayCommand]
private void OpenPrInBrowser(string? url)
{
    if (string.IsNullOrEmpty(url)) return;
    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
```

### Step 12.5: Create PR dialog AXAML

**File:** `src/GrumpyGit.App/Views/MainWindow.axaml`

Add a modal overlay dialog (same pattern as the existing confirmation dialog). Place it inside the root Grid, spanning all rows with ZIndex="100":

```xml
<!-- Create PR Dialog Overlay -->
<Border Grid.Row="0" Grid.RowSpan="5"
        ZIndex="100"
        Background="#88000000"
        IsVisible="{Binding CreatePrVm.IsVisible}">
    <Border Background="#2A2A3E"
            BorderBrush="#404070"
            BorderThickness="1"
            CornerRadius="8"
            Padding="24,20"
            MaxWidth="600"
            MaxHeight="500"
            HorizontalAlignment="Center"
            VerticalAlignment="Center">
        <StackPanel Spacing="12">
            <TextBlock Text="Create Pull Request"
                       Foreground="#E0E0F0"
                       FontSize="16"
                       FontWeight="SemiBold"/>

            <!-- Head -> Base branch display -->
            <StackPanel Orientation="Horizontal" Spacing="6">
                <TextBlock Text="{Binding CreatePrVm.HeadBranch}"
                           Foreground="#6080C0" FontSize="13"/>
                <TextBlock Text="into" Foreground="#8080A8" FontSize="13"/>
                <ComboBox Classes="branch-combo"
                          ItemsSource="{Binding CreatePrVm.AvailableBaseBranches}"
                          SelectedItem="{Binding CreatePrVm.BaseBranch, Mode=TwoWay}"
                          MinWidth="150"/>
            </StackPanel>

            <!-- Title -->
            <TextBox Classes="commit-msg"
                     Watermark="PR Title"
                     Text="{Binding CreatePrVm.Title, Mode=TwoWay}"/>

            <!-- Body (markdown) -->
            <TextBox Classes="commit-msg"
                     Watermark="Description (markdown)"
                     Text="{Binding CreatePrVm.Body, Mode=TwoWay}"
                     AcceptsReturn="True"
                     Height="120"
                     TextWrapping="Wrap"/>

            <!-- Draft toggle -->
            <CheckBox Content="Create as draft"
                      IsChecked="{Binding CreatePrVm.IsDraft, Mode=TwoWay}"
                      Foreground="#B0B0D0"/>

            <!-- Error message -->
            <TextBlock Text="{Binding CreatePrVm.ErrorMessage}"
                       Foreground="#FF8080"
                       FontSize="12"
                       TextWrapping="Wrap"
                       IsVisible="{Binding CreatePrVm.ErrorMessage, Converter={x:Static StringConverters.IsNotNullOrEmpty}}"/>

            <!-- Success URL -->
            <StackPanel Orientation="Horizontal" Spacing="8"
                        IsVisible="{Binding CreatePrVm.ResultUrl, Converter={x:Static StringConverters.IsNotNullOrEmpty}}">
                <TextBlock Text="Created:" Foreground="#80C080" FontSize="12"/>
                <Button Content="{Binding CreatePrVm.ResultUrl}"
                        Command="{Binding OpenPrInBrowserCommand}"
                        CommandParameter="{Binding CreatePrVm.ResultUrl}"
                        Background="Transparent"
                        Foreground="#6080C0"
                        Padding="0"
                        FontSize="12"
                        Cursor="Hand"/>
            </StackPanel>

            <!-- Action buttons -->
            <StackPanel Orientation="Horizontal"
                        Spacing="10"
                        HorizontalAlignment="Right"
                        Margin="0,8,0,0">
                <Button Classes="toolbar-btn"
                        Content="Cancel"
                        Command="{Binding CancelCreatePrCommand}"
                        Padding="16,6"/>
                <Button Content="Create PR"
                        Command="{Binding SubmitCreatePrCommand}"
                        IsEnabled="{Binding !CreatePrVm.IsSubmitting}"
                        Background="#3A5C3A"
                        Foreground="#D0F0D0"
                        Padding="16,6"
                        CornerRadius="4"
                        FontSize="13"/>
            </StackPanel>
        </StackPanel>
    </Border>
</Border>
```

### Step 12.6: Add "Create PR" toolbar button

**File:** `src/GrumpyGit/App/Views/MainWindow.axaml`

Add next to the "PRs" button in the toolbar:

```xml
<Button Classes="toolbar-btn"
        Content="Create PR"
        Command="{Binding StartCreatePrCommand}"
        IsVisible="{Binding IsGitHubRepo}"
        IsEnabled="{Binding CanCreatePr}"
        ToolTip.Tip="Create a pull request from the current branch"/>
```

---

## Feature 13: Issue Linking

### Step 13.1: Create issue reference parser

**File (new):** `src/GrumpyGit.Core/GitHub/IssueReferenceParser.cs`

A static utility that scans commit messages for GitHub issue references.

```csharp
public static class IssueReferenceParser
{
    /// <summary>
    /// Finds all issue references in text. Returns parsed references.
    /// Supported formats:
    ///   #123           -> (null, null, 123)
    ///   GH-123         -> (null, null, 123)
    ///   owner/repo#123 -> (owner, repo, 123)
    /// </summary>
    public static IReadOnlyList<IssueReference> Parse(string text);
}

public record IssueReference(
    string? Owner,       // null for same-repo references
    string? Repo,        // null for same-repo references
    int Number);
```

**Regex pattern:**

```csharp
private static readonly Regex Pattern = new(
    @"(?:(?<owner>[A-Za-z0-9\-]+)/(?<repo>[A-Za-z0-9._\-]+))?#(?<num>\d+)|GH-(?<ghnum>\d+)",
    RegexOptions.Compiled);
```

Important edge cases:
- Do not match issue numbers inside URLs (check that the character before `#` is not `/` or `&` or `?`).
- Do not match `#` inside code blocks or inline code. (For commit messages, this is not typically an issue, but for PR bodies it could be. Keep it simple for now -- commit messages rarely contain code blocks.)

### Step 13.2: Add issue reference display to commit detail view

**File:** `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`

Add:

```csharp
public ObservableCollection<IssueReferenceViewModel> CommitIssueReferences { get; } = new();

// In OnSelectedCommitChanged, after loading files:
private async Task LoadIssueReferencesAsync(CommitRowViewModel commit)
{
    CommitIssueReferences.Clear();
    if (!_github.IsAvailable || commit.IsWorkingTree) return;

    var refs = IssueReferenceParser.Parse(commit.Subject);
    if (refs.Count == 0) return;

    foreach (var issueRef in refs)
    {
        // Only fetch details for same-repo references
        if (issueRef.Owner is null)
        {
            try
            {
                var issue = await _github.GetIssueAsync(issueRef.Number);
                CommitIssueReferences.Add(new IssueReferenceViewModel
                {
                    Number = issue.Number,
                    Title = issue.Title,
                    State = issue.State,
                    StateColor = issue.State == "open" ? "#80C080" : "#C08080",
                    Url = $"https://github.com/{_github.Owner}/{_github.Repo}/issues/{issue.Number}"
                });
            }
            catch
            {
                // Issue might not exist or might be a PR reference
                CommitIssueReferences.Add(new IssueReferenceViewModel
                {
                    Number = issueRef.Number,
                    Title = $"#{issueRef.Number}",
                    State = "unknown",
                    StateColor = "#808080",
                    Url = $"https://github.com/{_github.Owner}/{_github.Repo}/issues/{issueRef.Number}"
                });
            }
        }
        else
        {
            // Cross-repo reference -- just show as a link, don't fetch
            CommitIssueReferences.Add(new IssueReferenceViewModel
            {
                Number = issueRef.Number,
                Title = $"{issueRef.Owner}/{issueRef.Repo}#{issueRef.Number}",
                State = "external",
                StateColor = "#808080",
                Url = $"https://github.com/{issueRef.Owner}/{issueRef.Repo}/issues/{issueRef.Number}"
            });
        }
    }
}
```

**File (new):** `src/GrumpyGit.App/ViewModels/IssueReferenceViewModel.cs`

```csharp
public class IssueReferenceViewModel
{
    public int Number { get; init; }
    public string Title { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string StateColor { get; init; } = "#808080";
    public string Url { get; init; } = string.Empty;
    public string DisplayText => $"#{Number} {Title}";
}
```

### Step 13.3: Add "ISSUES" section to commit detail UI

**File:** `src/GrumpyGit/App/Views/MainWindow.axaml`

Add an ISSUES section in the FILES panel (Column 0 of the bottom detail area), below the file list but above the diff viewer. It appears only when there are issue references:

```xml
<!-- Issues section (below file list) -->
<StackPanel IsVisible="{Binding !!CommitIssueReferences.Count}">
    <TextBlock Classes="panel-header"
               Text="REFERENCED ISSUES"
               Background="#1E1E38"
               Padding="8,4"/>
    <ItemsControl ItemsSource="{Binding CommitIssueReferences}"
                  Margin="8,4">
        <ItemsControl.ItemTemplate>
            <DataTemplate>
                <StackPanel Orientation="Horizontal" Spacing="6" Margin="0,2">
                    <!-- State dot -->
                    <Ellipse Width="8" Height="8"
                             Fill="{Binding StateColor}"
                             VerticalAlignment="Center"/>
                    <!-- Clickable issue text -->
                    <Button Content="{Binding DisplayText}"
                            CommandParameter="{Binding Url}"
                            Background="Transparent"
                            Foreground="#6080C0"
                            Padding="0"
                            FontSize="12"
                            Cursor="Hand"
                            x:CompileBindings="False"
                            Command="{Binding DataContext.OpenPrInBrowserCommand, ElementName=CommitListBox}"/>
                </StackPanel>
            </DataTemplate>
        </ItemsControl.ItemTemplate>
    </ItemsControl>
</StackPanel>
```

Note: Reuse the `OpenPrInBrowserCommand` (rename it to `OpenUrlInBrowserCommand` for clarity) since it just calls `Process.Start` with a URL.

### Step 13.4: Add issue autocomplete to commit message TextBox

**File:** `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`

Add:

```csharp
// Cached open issues for autocomplete
public ObservableCollection<IssueModel> CachedOpenIssues { get; } = new();
[ObservableProperty] private bool _isIssueAutocompleteVisible;
[ObservableProperty] private ObservableCollection<IssueModel> _filteredIssues = new();

private async Task LoadOpenIssuesForAutocompleteAsync()
{
    if (!_github.IsAvailable) return;
    try
    {
        var issues = await _github.GetOpenIssuesAsync();
        CachedOpenIssues.Clear();
        foreach (var issue in issues)
            CachedOpenIssues.Add(issue);
    }
    catch { /* Silently fail -- autocomplete is a convenience feature */ }
}

// Call this when '#' is typed in the commit message
public void UpdateIssueAutocomplete(string textAfterHash)
{
    if (CachedOpenIssues.Count == 0 || string.IsNullOrEmpty(textAfterHash))
    {
        FilteredIssues.Clear();
        IsIssueAutocompleteVisible = CachedOpenIssues.Count > 0 && string.IsNullOrEmpty(textAfterHash);
        // Show all issues when just '#' is typed
        if (IsIssueAutocompleteVisible)
        {
            foreach (var issue in CachedOpenIssues.Take(15))
                FilteredIssues.Add(issue);
        }
        return;
    }

    FilteredIssues.Clear();
    var matches = CachedOpenIssues
        .Where(i => i.Number.ToString().StartsWith(textAfterHash)
                  || i.Title.Contains(textAfterHash, StringComparison.OrdinalIgnoreCase))
        .Take(10);
    foreach (var m in matches)
        FilteredIssues.Add(m);
    IsIssueAutocompleteVisible = FilteredIssues.Count > 0;
}
```

### Step 13.5: Implement autocomplete UI for commit message

**File:** `src/GrumpyGit.App/Views/MainWindow.axaml`

Replace the simple commit message TextBox with a container that has a popup:

```xml
<Grid>
    <TextBox x:Name="CommitMessageBox"
             Classes="commit-msg"
             Watermark="Commit message..."
             Text="{Binding CommitMessage, Mode=TwoWay}"
             AcceptsReturn="False"/>
    <!-- Autocomplete popup -->
    <Popup IsOpen="{Binding IsIssueAutocompleteVisible}"
           PlacementTarget="{Binding #CommitMessageBox}"
           Placement="Bottom"
           MaxHeight="200"
           Width="{Binding #CommitMessageBox.Bounds.Width}">
        <Border Background="#2A2A3E"
                BorderBrush="#404070"
                BorderThickness="1"
                CornerRadius="4">
            <ListBox x:Name="IssueAutocompleteList"
                     ItemsSource="{Binding FilteredIssues}"
                     Background="Transparent"
                     BorderThickness="0">
                <ListBox.ItemTemplate>
                    <DataTemplate>
                        <StackPanel Orientation="Horizontal" Spacing="8">
                            <TextBlock Text="{Binding Number, StringFormat='#{0}'}"
                                       Foreground="#6080C0" FontSize="12" FontWeight="SemiBold"/>
                            <TextBlock Text="{Binding Title}"
                                       Foreground="#B0B0D0" FontSize="12"
                                       TextTrimming="CharacterEllipsis"/>
                        </StackPanel>
                    </DataTemplate>
                </ListBox.ItemTemplate>
            </ListBox>
        </Border>
    </Popup>
</Grid>
```

### Step 13.6: Wire up autocomplete behavior in code-behind

**File:** `src/GrumpyGit.App/Views/MainWindow.axaml.cs`

Add event handlers for the commit message TextBox:

```csharp
// In InitializeComponent or constructor:
CommitMessageBox.TextChanged += OnCommitMessageTextChanged;
IssueAutocompleteList.SelectionChanged += OnIssueAutocompletSelected;

private void OnCommitMessageTextChanged(object? sender, TextChangedEventArgs e)
{
    if (DataContext is not MainWindowViewModel vm) return;

    var text = CommitMessageBox.Text ?? string.Empty;
    var caretPos = CommitMessageBox.CaretIndex;

    // Find the last '#' before the caret
    var hashIndex = text.LastIndexOf('#', Math.Max(0, caretPos - 1));
    if (hashIndex >= 0 && hashIndex < caretPos)
    {
        var afterHash = text[(hashIndex + 1)..caretPos];
        // Only trigger if text after # contains no spaces (issue references don't have spaces)
        if (!afterHash.Contains(' '))
        {
            vm.UpdateIssueAutocomplete(afterHash);
            return;
        }
    }

    vm.IsIssueAutocompleteVisible = false;
}

private void OnIssueAutocompletSelected(object? sender, SelectionChangedEventArgs e)
{
    if (DataContext is not MainWindowViewModel vm) return;
    if (IssueAutocompleteList.SelectedItem is not IssueModel issue) return;

    var text = CommitMessageBox.Text ?? string.Empty;
    var caretPos = CommitMessageBox.CaretIndex;
    var hashIndex = text.LastIndexOf('#', Math.Max(0, caretPos - 1));

    if (hashIndex >= 0)
    {
        // Replace from # to caret with #123
        var before = text[..hashIndex];
        var after = caretPos < text.Length ? text[caretPos..] : string.Empty;
        var replacement = $"#{issue.Number}";
        CommitMessageBox.Text = before + replacement + after;
        CommitMessageBox.CaretIndex = (before + replacement).Length;
    }

    vm.IsIssueAutocompleteVisible = false;
    IssueAutocompleteList.SelectedItem = null;
}
```

### Step 13.7: Load open issues when repo is loaded

**File:** `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`

In `LoadRepoAsync`, after `InitializeAsync` for GitHub:

```csharp
if (IsGitHubRepo)
{
    // Fire-and-forget: pre-cache open issues for autocomplete
    _ = LoadOpenIssuesForAutocompleteAsync();
}
```

---

## Implementation Order

The features share the foundation layer and should be built in this sequence:

### Phase 1: Foundation (shared by all three features)
1. Step 0.1 -- Add Octokit to Core .csproj
2. Step 0.2 -- `GetGitHubTokenAsync` in GitService
3. Step 0.3 -- `GitRemoteParser`
4. Step 0.4 -- `GitHubService` + `IGitHubService`
5. Step 0.5 -- Shared models (PullRequestModel, IssueModel, CheckRunModel)
6. Step 0.6 -- Wire into MainWindowViewModel

### Phase 2: Feature 11 (PR List and Review)
7. Step 11.1 -- PullRequestListViewModel
8. Step 11.2 -- PullRequestDetailViewModel + sub-ViewModels
9. Step 11.3 -- PR commands in MainWindowViewModel
10. Step 11.4 -- PR panel AXAML
11. Step 11.5 -- PRs toolbar button
12. Step 11.6 -- Auto-load on panel open

### Phase 3: Feature 12 (Create PR)
13. Step 12.1 -- Ahead/behind detection + helper methods in GitService
14. Step 12.2 -- CreatePullRequest in GitHubService (already in interface from Phase 1)
15. Step 12.3 -- CreatePullRequestViewModel
16. Step 12.4 -- Create PR commands in MainWindowViewModel
17. Step 12.5 -- Create PR dialog AXAML
18. Step 12.6 -- Create PR toolbar button

### Phase 4: Feature 13 (Issue Linking)
19. Step 13.1 -- IssueReferenceParser
20. Step 13.2 -- Issue reference display logic in MainWindowViewModel
21. Step 13.3 -- ISSUES section in commit detail AXAML
22. Step 13.4 -- Issue autocomplete logic in MainWindowViewModel
23. Step 13.5 -- Autocomplete UI AXAML
24. Step 13.6 -- Wire autocomplete behavior in code-behind
25. Step 13.7 -- Pre-cache issues on repo load

---

## New Files Summary

| File | Purpose |
|------|---------|
| `src/GrumpyGit.Core/Git/GitRemoteParser.cs` | Parse GitHub remote URLs to extract owner/repo |
| `src/GrumpyGit.Core/GitHub/IGitHubService.cs` | Interface for GitHub API operations |
| `src/GrumpyGit.Core/GitHub/GitHubService.cs` | Octokit wrapper: PRs, issues, check runs |
| `src/GrumpyGit.Core/GitHub/IssueReferenceParser.cs` | Parse `#123`, `GH-123`, `org/repo#123` from text |
| `src/GrumpyGit.Core/Models/PullRequestModel.cs` | PR domain models (PullRequestModel, ReviewModel, DetailModel, FileModel) |
| `src/GrumpyGit.Core/Models/IssueModel.cs` | Issue domain model |
| `src/GrumpyGit.Core/Models/CheckRunModel.cs` | CI check run domain model |
| `src/GrumpyGit.App/ViewModels/PullRequestListViewModel.cs` | PR list item ViewModel |
| `src/GrumpyGit.App/ViewModels/PullRequestDetailViewModel.cs` | PR detail ViewModel + sub-ViewModels |
| `src/GrumpyGit.App/ViewModels/CreatePullRequestViewModel.cs` | Create PR dialog ViewModel |
| `src/GrumpyGit.App/ViewModels/IssueReferenceViewModel.cs` | Issue reference display ViewModel |

## Modified Files Summary

| File | Changes |
|------|---------|
| `src/GrumpyGit.Core/GrumpyGit.Core.csproj` | Add Octokit 14.0.0 package reference |
| `src/GrumpyGit.Core/Git/IGitService.cs` | Add `GetGitHubTokenAsync`, `GetAheadBehindAsync`, `GetLastCommitMessageAsync`, `GetRemoteBranchesAsync` |
| `src/GrumpyGit.Core/Git/GitService.cs` | Implement the four new methods above |
| `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs` | Add GitHub state properties, PR panel commands, create PR commands, issue reference loading, issue autocomplete |
| `src/GrumpyGit.App/Views/MainWindow.axaml` | Add PRs toolbar button, Create PR toolbar button, PR side panel, Create PR dialog overlay, ISSUES section in commit detail, issue autocomplete popup |
| `src/GrumpyGit.App/Views/MainWindow.axaml.cs` | Add commit message autocomplete event handlers |

---

## Error Handling Strategy

All GitHub API calls should be wrapped in try/catch blocks that catch:

- `Octokit.AuthorizationException` -- Token is invalid or expired. Show "GitHub authentication failed. Please check your Git Credential Manager configuration." Set `IsGitHubRepo = false`.
- `Octokit.RateLimitExceededException` -- Show "GitHub API rate limit exceeded. Try again in {resetTime}." Display the rate limit reset time from the exception.
- `Octokit.NotFoundException` -- The resource does not exist. For issues, show "Issue not found". For PRs, show "PR not found".
- `Octokit.ApiException` -- General API error. Show the message from the exception.
- `HttpRequestException` / `TaskCanceledException` -- Network errors. Show "Network error. Check your internet connection."

The `StatusMessage` bar at the bottom of the window is used for all status and error messages, consistent with the existing pattern.

---

## Caching Strategy

- **PR list:** Cache in `OpenPullRequests` collection. Refreshed manually via the "Refresh" button or automatically when the PR panel is opened and the list is empty.
- **PR detail:** Not cached. Fetched fresh each time a PR is selected (details change frequently with new reviews and CI results).
- **Open issues:** Cached in `CachedOpenIssues` on repo load. Refreshed when the repo is reloaded. This is acceptable because issue lists change slowly relative to a single editing session.
- **Issue details for commit references:** Fetched on demand when a commit is selected. Consider adding a simple `Dictionary<int, IssueModel>` cache in MainWindowViewModel to avoid re-fetching the same issue when navigating back to a previously viewed commit.

---

## Testing Strategy

### Unit tests (GrumpyGit.Core.Tests)

1. **GitRemoteParser tests:**
   - HTTPS URL with .git suffix
   - HTTPS URL without .git suffix
   - SSH URL (git@github.com:owner/repo.git)
   - SSH URL (ssh://git@github.com/owner/repo.git)
   - Non-GitHub URLs return null
   - Edge cases: trailing slashes, unusual characters in owner/repo names

2. **IssueReferenceParser tests:**
   - `#123` -> (null, null, 123)
   - `GH-123` -> (null, null, 123)
   - `owner/repo#456` -> ("owner", "repo", 456)
   - Multiple references in one string
   - No false positives on URLs containing `#`
   - No match on `#` at end of string with no number
   - Case sensitivity of `GH-` prefix

3. **GitHubService tests:**
   - Mock IGitService to return known remote URLs and tokens
   - Verify InitializeAsync sets Owner/Repo correctly
   - Verify IsAvailable is false for non-GitHub repos

### Integration tests (manual)

- Open a GitHub-hosted repo, verify PR list loads
- Click a PR, verify detail view shows reviews and checks
- Create a PR from a branch that is ahead of origin
- Type `#` in commit message, verify autocomplete appears
- Click an issue reference in commit detail, verify browser opens
