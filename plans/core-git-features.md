# Core Git Features -- Implementation Plan (Features 5-10)

This document provides a step-by-step implementation plan for six features in GrumpyGit. Each feature specifies the exact files to create or modify, the code changes required, and the implementation order.

---

## Current State Summary

**GitService** (`src/GrumpyGit.Core/Git/GitService.cs`) already provides: commit graph loading, file-change listing for commits, working-tree status (porcelain v2), staged/unstaged diffs, commit-range diff (`GetCommitRangeDiffAsync`), staging/unstaging (file + hunk), commit, push/pull, branch CRUD, merge, stash, discard, revert, and undo. The porcelain v2 parser already recognizes `u ` (unmerged) entries but currently skips them.

**MainWindowViewModel** (`src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`) manages a single selected commit (`SelectedCommit`), populates `ChangedFiles`/`StagedFiles`, and loads diffs via `SelectedFile`. There is no multi-select on the commit list, no context menu on the file list, and no secondary view modes (blame, history).

**IGitService** (`src/GrumpyGit.Core/Git/IGitService.cs`) is the interface for all git operations.

**Models**: `CommitNode`, `FileChange` (with `FileChangeStatus` enum), `ParsedDiff`, `DiffHunk`, `DiffLine`.

**ViewModels**: `CommitRowViewModel`, `FileChangeViewModel`, `DiffHunkViewModel`, `MainWindowViewModel`.

---

## Feature 5: Commit Range Comparison UI

### Goal
Allow the user to Ctrl+click a second commit in the commit list to compare the aggregated diff between two commits. Show a "Comparing X..Y" header, the file list of all changes between the two commits, and the diff viewer for individual files.

### Implementation Order

#### Step 5.1: Add `GetCommitRangeFilesAsync` to GitService

**File**: `src/GrumpyGit.Core/Git/IGitService.cs`
- Add interface method:
  ```csharp
  Task<IReadOnlyList<FileChange>> GetCommitRangeFilesAsync(
      string repoPath, string fromHash, string toHash, CancellationToken ct = default);
  ```

**File**: `src/GrumpyGit.Core/Git/GitService.cs`
- Add implementation that runs `git diff --name-status -z <fromHash> <toHash>` and parses the NUL-delimited output using the existing `ParseDiffTreeOutput` method (same format as `diff-tree --name-status -z`).

#### Step 5.2: Add `GetCommitRangeFileDiffAsync` to GitService

**File**: `src/GrumpyGit.Core/Git/IGitService.cs`
- Add interface method:
  ```csharp
  Task<string> GetCommitRangeFileDiffAsync(
      string repoPath, string fromHash, string toHash, string filePath, CancellationToken ct = default);
  ```

**File**: `src/GrumpyGit.Core/Git/GitService.cs`
- Add implementation that runs `git diff <fromHash> <toHash> -- <filePath>` and returns the raw unified diff output. Validate all inputs (repoPath, both hashes, filePath).

#### Step 5.3: Add comparison state to MainWindowViewModel

**File**: `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`
- Add observable properties:
  ```csharp
  [ObservableProperty] private CommitRowViewModel? _comparisonCommit;
  [ObservableProperty] private bool _isComparing;
  [ObservableProperty] private string _comparisonHeader = string.Empty;
  ```
- Add a `ClearComparison()` method that resets `ComparisonCommit`, `IsComparing`, and `ComparisonHeader`.
- Add a `SelectComparisonCommitAsync(CommitRowViewModel commit)` method that:
  1. Sets `ComparisonCommit = commit`, `IsComparing = true`.
  2. Sets `ComparisonHeader = $"Comparing {SelectedCommit.ShortHash}..{commit.ShortHash}"`.
  3. Calls `_git.GetCommitRangeFilesAsync(RepoPath, SelectedCommit.Hash, commit.Hash)`.
  4. Populates `ChangedFiles` with the results.
  5. Clears `StagedFiles` (not relevant in comparison mode).

#### Step 5.4: Handle Ctrl+click on the commit list

**File**: `src/GrumpyGit.App/Views/MainWindow.axaml.cs`
- In `OnLoaded`, wire a `PointerPressed` handler on `CommitListBox` (or use `SelectionChanged` + check keyboard modifiers).
- When Ctrl is held and a second commit is clicked (and `SelectedCommit` is already set and is not the working-tree row):
  1. Prevent the default `SelectedItem` change from firing `OnSelectedCommitChanged`.
  2. Call `vm.SelectComparisonCommitAsync(clickedCommit)`.
- When Ctrl is NOT held, call `vm.ClearComparison()` and let normal selection proceed.

#### Step 5.5: Override diff loading for comparison mode

**File**: `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`
- Modify `LoadDiffAsync` (or add a `LoadComparisonDiffAsync` method) so that when `IsComparing` is true:
  1. It calls `_git.GetCommitRangeFileDiffAsync(RepoPath, SelectedCommit.Hash, ComparisonCommit.Hash, file.Path)` instead of `GetFileDiffAsync`.
  2. Parses the result with `UnifiedDiffParser.Parse(raw)`.
  3. Hunk staging buttons are hidden (comparison mode is read-only).

#### Step 5.6: Add comparison header to the UI

**File**: `src/GrumpyGit.App/Views/MainWindow.axaml`
- Above the FILES panel header (or replacing it), add a conditional TextBlock:
  ```xml
  <TextBlock Text="{Binding ComparisonHeader}"
             Foreground="#C0A0FF"
             FontSize="12"
             FontWeight="SemiBold"
             IsVisible="{Binding IsComparing}"
             Margin="8,4"/>
  ```
- When `IsComparing` is true, hide the commit message box, Stage All button, and staged/unstaged sections (show only the flat file list).

#### Step 5.7: Add "Exit Comparison" button

**File**: `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`
- Add a `[RelayCommand] private void ExitComparison()` that calls `ClearComparison()` and re-selects the original `SelectedCommit` to reload its files.

**File**: `src/GrumpyGit.App/Views/MainWindow.axaml`
- Add a small "Exit Comparison" button next to the comparison header, visible only when `IsComparing` is true.

---

## Feature 6: Conflict Resolution UI

### Goal
When `git status --porcelain=v2` reports unmerged entries (`u ` prefix), show them in a dedicated "Conflicts" section in the file list. For each conflicted file, provide a three-panel merge view (ours | theirs | result) with accept-left/right/both per hunk, plus whole-file resolution via `git checkout --ours/--theirs`. Mark resolved with `git add`.

### Implementation Order

#### Step 6.1: Add `Unmerged` to `FileChangeStatus` enum

**File**: `src/GrumpyGit.Core/Models/FileChange.cs`
- Add `Unmerged` to the `FileChangeStatus` enum.

#### Step 6.2: Parse unmerged entries in porcelain v2

**File**: `src/GrumpyGit.Core/Git/GitService.cs`
- In `ParsePorcelainV2`, change the `u ` branch from skipping to parsing:
  ```
  Format: u XY sub m1 m2 m3 mW h1 h2 h3 path
  ```
  - Split on space (11 fields). Field index 10 is the path.
  - Create a `FileChange(path, "", FileChangeStatus.Unmerged, IsStaged: false)`.
  - The `XY` field for unmerged entries uses letters like `UU`, `AA`, `DD`, `AU`, `UA`, `DU`, `UD` -- store the XY in a new optional `UnmergedStatus` field on `FileChange` (or parse just the path for now and determine conflict type later).

**File**: `src/GrumpyGit.Core/Models/FileChange.cs`
- Optionally extend the record to include `string UnmergedStatus = ""` for richer conflict type display.

#### Step 6.3: Add conflict-related git operations to GitService

**File**: `src/GrumpyGit.Core/Git/IGitService.cs`
- Add:
  ```csharp
  Task<string> GetConflictOursAsync(string repoPath, string filePath, CancellationToken ct = default);
  Task<string> GetConflictTheirsAsync(string repoPath, string filePath, CancellationToken ct = default);
  Task<string> GetConflictBaseAsync(string repoPath, string filePath, CancellationToken ct = default);
  Task CheckoutOursAsync(string repoPath, string filePath, CancellationToken ct = default);
  Task CheckoutTheirsAsync(string repoPath, string filePath, CancellationToken ct = default);
  Task MarkResolvedAsync(string repoPath, string filePath, CancellationToken ct = default);
  Task AbortMergeAsync(string repoPath, CancellationToken ct = default);
  ```

**File**: `src/GrumpyGit.Core/Git/GitService.cs`
- `GetConflictOursAsync`: Run `git show :2:<filePath>` (stage 2 = ours), return stdout.
- `GetConflictTheirsAsync`: Run `git show :3:<filePath>` (stage 3 = theirs), return stdout.
- `GetConflictBaseAsync`: Run `git show :1:<filePath>` (stage 1 = base), return stdout. Handle exit code 128 (no base exists for add/add conflicts) by returning empty string.
- `CheckoutOursAsync`: Run `git checkout --ours -- <filePath>`.
- `CheckoutTheirsAsync`: Run `git checkout --theirs -- <filePath>`.
- `MarkResolvedAsync`: Run `git add -- <filePath>`.
- `AbortMergeAsync`: Run `git merge --abort`.

#### Step 6.4: Add ConflictFiles collection to MainWindowViewModel

**File**: `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`
- Add:
  ```csharp
  public ObservableCollection<FileChangeViewModel> ConflictFiles { get; } = new();
  [ObservableProperty] private bool _hasConflicts;
  ```
- In `LoadWorkingTreeFilesAsync`, separate `FileChangeStatus.Unmerged` entries into `ConflictFiles` instead of `ChangedFiles`/`StagedFiles`.
- Set `HasConflicts = ConflictFiles.Count > 0`.

#### Step 6.5: Add CONFLICTS section to MainWindow.axaml

**File**: `src/GrumpyGit.App/Views/MainWindow.axaml`
- Inside the file list `ScrollViewer`, before the STAGED section, add:
  ```xml
  <StackPanel IsVisible="{Binding HasConflicts}">
      <Grid Background="#3E1E1E" ColumnDefinitions="*,Auto,Auto">
          <TextBlock Grid.Column="0" Classes="panel-header" Text="CONFLICTS" Padding="8,4"/>
          <Button Grid.Column="1" Classes="stage-btn" Content="Abort Merge"
                  Command="{Binding AbortMergeCommand}" Margin="4,2"/>
      </Grid>
      <ListBox Background="Transparent" BorderThickness="0"
               ItemsSource="{Binding ConflictFiles}"
               SelectedItem="{Binding SelectedFile, Mode=TwoWay}">
          <ListBox.ItemTemplate>
              <DataTemplate x:DataType="vm:FileChangeViewModel">
                  <Grid ColumnDefinitions="*,Auto,Auto,Auto">
                      <TextBlock Grid.Column="0" Text="{Binding DisplayText}"
                                 Foreground="#FF8080" TextTrimming="CharacterEllipsis"/>
                      <Button Grid.Column="1" Classes="stage-btn" Content="Ours"
                              Command="{Binding AcceptOursCommand}" Margin="2,0"/>
                      <Button Grid.Column="2" Classes="stage-btn" Content="Theirs"
                              Command="{Binding AcceptTheirsCommand}" Margin="2,0"/>
                      <Button Grid.Column="3" Classes="stage-btn" Content="Resolved"
                              Command="{Binding MarkResolvedCommand}" Margin="2,0"/>
                  </Grid>
              </DataTemplate>
          </ListBox.ItemTemplate>
      </ListBox>
  </StackPanel>
  ```

#### Step 6.6: Add conflict commands to FileChangeViewModel

**File**: `src/GrumpyGit.App/ViewModels/FileChangeViewModel.cs`
- Add nullable command properties:
  ```csharp
  public IRelayCommand? AcceptOursCommand { get; init; }
  public IRelayCommand? AcceptTheirsCommand { get; init; }
  public IRelayCommand? MarkResolvedCommand { get; init; }
  ```

**File**: `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`
- When creating `FileChangeViewModel` for unmerged files, wire up these commands:
  - `AcceptOursCommand`: calls `_git.CheckoutOursAsync` then `_git.MarkResolvedAsync`, then refreshes.
  - `AcceptTheirsCommand`: calls `_git.CheckoutTheirsAsync` then `_git.MarkResolvedAsync`, then refreshes.
  - `MarkResolvedCommand`: calls `_git.MarkResolvedAsync` (assumes user has manually edited the file), then refreshes.

#### Step 6.7: Show three-panel merge view for conflicted files

**File**: `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`
- In `LoadDiffAsync`, when the selected file has `StatusLabel == "U"` (unmerged):
  1. Fetch ours, theirs, and base content in parallel using the new `GetConflictOurs/Theirs/BaseAsync` methods.
  2. Compute two diffs: base-vs-ours and base-vs-theirs using DiffPlex or by running `git diff --no-index` between temp files.
  3. For MVP, show a side-by-side diff of ours vs theirs using the existing `DiffViewer` (set `LeftText` = ours content, `RightText` = theirs content, compute colored lines with DiffPlex).

**File**: `src/GrumpyGit.App/Controls/DiffViewer.axaml` / `.axaml.cs`
- No changes needed for MVP. The existing side-by-side diff viewer can display ours-vs-theirs. A future enhancement could add a third "result" editor pane.

#### Step 6.8: Add AbortMerge command

**File**: `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`
- Add:
  ```csharp
  [RelayCommand]
  private async Task AbortMergeAsync()
  {
      if (string.IsNullOrEmpty(RepoPath)) return;
      var confirmed = await ShowConfirmationAsync("Abort Merge?",
          "This will discard all merge progress and return to the pre-merge state.");
      if (!confirmed) return;
      await _git.AbortMergeAsync(RepoPath);
      await LoadRepoAsync(RepoPath);
  }
  ```

---

## Feature 7: Interactive Rebase UI

### Goal
Show a draggable list of commits that can be reordered. Each commit has a dropdown to pick the action (pick, reword, squash, fixup, drop, edit). Generate a git-rebase-todo format and execute via `GIT_SEQUENCE_EDITOR` override piped through CliWrap. Show progress and handle conflicts.

### Implementation Order

#### Step 7.1: Add rebase-related git operations to GitService

**File**: `src/GrumpyGit.Core/Git/IGitService.cs`
- Add:
  ```csharp
  Task<IReadOnlyList<CommitNode>> GetRebaseCommitsAsync(
      string repoPath, string ontoHash, CancellationToken ct = default);
  Task StartInteractiveRebaseAsync(
      string repoPath, string ontoHash, string todoContent, CancellationToken ct = default);
  Task RebaseContinueAsync(string repoPath, CancellationToken ct = default);
  Task RebaseAbortAsync(string repoPath, CancellationToken ct = default);
  Task RebaseSkipAsync(string repoPath, CancellationToken ct = default);
  Task<bool> IsRebaseInProgressAsync(string repoPath, CancellationToken ct = default);
  Task AmendCommitMessageAsync(string repoPath, string message, CancellationToken ct = default);
  ```

**File**: `src/GrumpyGit.Core/Git/GitService.cs`
- `GetRebaseCommitsAsync`: Run `git log --format="%H%x00%s" --reverse <ontoHash>..HEAD` and parse into CommitNode list (only hash and subject needed). These are the commits that will be rebased.
- `StartInteractiveRebaseAsync`:
  1. Write the `todoContent` string to a temp file.
  2. Run `git rebase -i <ontoHash>` with environment variable `GIT_SEQUENCE_EDITOR=<path-to-script>` where the script simply copies the temp file over the todo file. On Windows, use a cmd/bat script: `copy /Y <tempfile> %1` or use `GIT_SEQUENCE_EDITOR=cat` and pipe the todo content. Simpler approach: set `GIT_SEQUENCE_EDITOR` to a command that writes the todo content. CliWrap supports `.WithEnvironmentVariables(env => env.Set("GIT_SEQUENCE_EDITOR", ...))`.
  3. The cleanest approach: write the todo to a temp file, then set `GIT_SEQUENCE_EDITOR=cmd /c copy /Y "<tempfile>" "%1"` (on Windows). The script replaces the editor's input file with our generated todo.
- `RebaseContinueAsync`: Run `git rebase --continue` with `GIT_EDITOR=true` (to auto-accept the commit message for squash/fixup). If the user chose "reword", we need a different flow -- see Step 7.5.
- `RebaseAbortAsync`: Run `git rebase --abort`.
- `RebaseSkipAsync`: Run `git rebase --skip`.
- `IsRebaseInProgressAsync`: Check if directory `.git/rebase-merge` or `.git/rebase-apply` exists.
- `AmendCommitMessageAsync`: Run `git commit --amend -m <message>`.

Add a `ValidateOntoHash` helper -- this reuses `ValidateHash`.

#### Step 7.2: Create RebaseItemViewModel

**File**: `src/GrumpyGit.App/ViewModels/RebaseItemViewModel.cs` (NEW)
- Properties:
  ```csharp
  public string Hash { get; init; }
  public string ShortHash => Hash[..7];
  public string Subject { get; init; }
  public RebaseAction Action { get; set; } = RebaseAction.Pick;
  public string? RewordMessage { get; set; }
  ```
- Enum (in same file or in Models):
  ```csharp
  public enum RebaseAction { Pick, Reword, Squash, Fixup, Drop, Edit }
  ```
- Method `ToTodoLine()`:
  ```csharp
  public string ToTodoLine() => Action switch
  {
      RebaseAction.Pick => $"pick {Hash} {Subject}",
      RebaseAction.Reword => $"reword {Hash} {Subject}",
      RebaseAction.Squash => $"squash {Hash} {Subject}",
      RebaseAction.Fixup => $"fixup {Hash} {Subject}",
      RebaseAction.Drop => $"drop {Hash} {Subject}",
      RebaseAction.Edit => $"edit {Hash} {Subject}",
      _ => $"pick {Hash} {Subject}"
  };
  ```

#### Step 7.3: Add rebase state to MainWindowViewModel

**File**: `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`
- Add:
  ```csharp
  public ObservableCollection<RebaseItemViewModel> RebaseItems { get; } = new();
  [ObservableProperty] private bool _isRebaseMode;
  [ObservableProperty] private bool _isRebaseInProgress;
  [ObservableProperty] private string _rebaseOntoHash = string.Empty;
  ```

#### Step 7.4: Add "Interactive Rebase" to commit context menu

**File**: `src/GrumpyGit.App/Views/MainWindow.axaml`
- In the commit list `ContextMenu`, add:
  ```xml
  <MenuItem Header="Interactive Rebase from Here..."
            CommandParameter="{Binding Hash}"
            Command="{Binding DataContext.StartInteractiveRebaseCommand, ElementName=CommitListBox}"
            IsVisible="{Binding !IsWorkingTree}"/>
  ```

**File**: `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`
- Add `[RelayCommand] private async Task StartInteractiveRebaseAsync(string? ontoHash)`:
  1. Validate working tree is clean via `_git.IsWorkingTreeCleanAsync`.
  2. Fetch commits via `_git.GetRebaseCommitsAsync(RepoPath, ontoHash)`.
  3. Populate `RebaseItems` with one `RebaseItemViewModel` per commit (in order, oldest first).
  4. Set `IsRebaseMode = true`, `RebaseOntoHash = ontoHash`.

#### Step 7.5: Create the rebase panel UI

**File**: `src/GrumpyGit.App/Views/MainWindow.axaml`
- Add a new overlay panel (similar to the confirm dialog) or replace the commit list content when `IsRebaseMode` is true:
  ```xml
  <Border IsVisible="{Binding IsRebaseMode}" Background="#1E1E2E" ZIndex="50">
      <Grid RowDefinitions="Auto,*,Auto">
          <TextBlock Grid.Row="0" Text="Interactive Rebase" Classes="panel-header"/>
          <ListBox Grid.Row="1" ItemsSource="{Binding RebaseItems}">
              <!-- Each item: drag handle, action ComboBox, short hash, subject -->
              <ListBox.ItemTemplate>
                  <DataTemplate>
                      <Grid ColumnDefinitions="Auto,100,Auto,*">
                          <TextBlock Grid.Column="0" Text=":::" Cursor="Hand" Margin="4,0"/>
                          <ComboBox Grid.Column="1"
                                    ItemsSource="{x:Static vm:RebaseActions.All}"
                                    SelectedItem="{Binding Action, Mode=TwoWay}"
                                    MinWidth="90"/>
                          <TextBlock Grid.Column="2" Text="{Binding ShortHash}"
                                     Foreground="#8080C0" Margin="8,0"/>
                          <TextBlock Grid.Column="3" Text="{Binding Subject}"
                                     TextTrimming="CharacterEllipsis"/>
                      </Grid>
                  </DataTemplate>
              </ListBox.ItemTemplate>
          </ListBox>
          <StackPanel Grid.Row="2" Orientation="Horizontal" Spacing="8" Margin="8">
              <Button Classes="toolbar-btn" Content="Start Rebase"
                      Command="{Binding ExecuteRebaseCommand}"/>
              <Button Classes="toolbar-btn" Content="Cancel"
                      Command="{Binding CancelRebaseCommand}"/>
          </StackPanel>
      </Grid>
  </Border>
  ```

#### Step 7.6: Implement drag-to-reorder on the rebase list

**File**: `src/GrumpyGit.App/Views/MainWindow.axaml.cs`
- Add drag-drop wiring for the rebase ListBox, similar to the existing file drag-drop logic.
- On drop, reorder items in `RebaseItems` by removing and re-inserting at the target index.

#### Step 7.7: Implement ExecuteRebase and CancelRebase commands

**File**: `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`
- `ExecuteRebaseCommand`:
  1. Build todo content: `string.Join("\n", RebaseItems.Select(r => r.ToTodoLine())) + "\n"`.
  2. Call `_git.StartInteractiveRebaseAsync(RepoPath, RebaseOntoHash, todoContent)`.
  3. If the rebase completes without conflicts, set `IsRebaseMode = false` and reload.
  4. If the rebase stops (conflict or `edit`), set `IsRebaseInProgress = true`, `IsRebaseMode = false`, and reload the working tree to show conflicts.
  5. Catch `GitException` and check if the error indicates a conflict. If so, show the conflict state.

- `CancelRebaseCommand`:
  1. If `IsRebaseInProgress`, call `_git.RebaseAbortAsync(RepoPath)`.
  2. Set `IsRebaseMode = false`, `IsRebaseInProgress = false`.
  3. Reload repo.

#### Step 7.8: Add rebase-in-progress toolbar indicators

**File**: `src/GrumpyGit.App/Views/MainWindow.axaml`
- When `IsRebaseInProgress` is true, show buttons in the branch operations bar:
  - "Continue Rebase" (calls `RebaseContinueCommand`)
  - "Skip" (calls `RebaseSkipCommand`)
  - "Abort Rebase" (calls `CancelRebaseCommand`)

**File**: `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`
- Add `[RelayCommand] private async Task RebaseContinueAsync()` and `[RelayCommand] private async Task RebaseSkipAsync()`.
- After each, check `IsRebaseInProgressAsync` and either continue showing the rebase UI or complete and reload.

#### Step 7.9: Handle reword action

**File**: `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`
- When the rebase stops on a `reword` commit, detect this by checking for `.git/rebase-merge/message` file.
- Show a text input dialog (reuse the confirmation dialog pattern but with a TextBox) for the new commit message.
- Call `_git.AmendCommitMessageAsync(RepoPath, newMessage)` then `_git.RebaseContinueAsync(RepoPath)`.

---

## Feature 8: Blame View

### Goal
Add a "Blame" option when right-clicking a file. Run `git blame --porcelain <file>` and parse the output. Show the file content with blame annotations (commit SHA, author, date) in a gutter alongside the code. Clicking a blame annotation navigates to that commit in the graph.

### Implementation Order

#### Step 8.1: Create BlameLine and BlameResult models

**File**: `src/GrumpyGit.Core/Models/BlameLine.cs` (NEW)
```csharp
namespace GrumpyGit.Core.Models;

public sealed class BlameLine
{
    public string CommitHash { get; init; } = string.Empty;
    public string AuthorName { get; init; } = string.Empty;
    public DateTimeOffset AuthorDate { get; init; }
    public int OriginalLineNumber { get; init; }
    public int FinalLineNumber { get; init; }
    public string Content { get; init; } = string.Empty;
}

public sealed class BlameResult
{
    public IReadOnlyList<BlameLine> Lines { get; init; } = [];
}
```

#### Step 8.2: Add `GetBlameAsync` to GitService

**File**: `src/GrumpyGit.Core/Git/IGitService.cs`
- Add:
  ```csharp
  Task<BlameResult> GetBlameAsync(
      string repoPath, string filePath, string? commitHash = null, CancellationToken ct = default);
  ```

**File**: `src/GrumpyGit.Core/Git/GitService.cs`
- Run `git blame --porcelain [<commitHash>] -- <filePath>`.
- Parse the porcelain format:
  - Each block starts with `<hash> <orig-line> <final-line> [<num-lines>]`.
  - Followed by header lines: `author <name>`, `author-mail <email>`, `author-time <epoch>`, `author-tz <tz>`, etc.
  - Ends with a tab-prefixed content line: `\t<content>`.
  - Build a dictionary of commit hashes to author info (porcelain only emits full headers on first occurrence of each commit).
  - Return a `BlameResult` with one `BlameLine` per source line.

#### Step 8.3: Create BlameViewModel

**File**: `src/GrumpyGit.App/ViewModels/BlameLineViewModel.cs` (NEW)
```csharp
public class BlameLineViewModel
{
    public string ShortHash { get; init; } = string.Empty;
    public string FullHash { get; init; } = string.Empty;
    public string AuthorName { get; init; } = string.Empty;
    public string FormattedDate { get; init; } = string.Empty;
    public int LineNumber { get; init; }
    public string Content { get; init; } = string.Empty;
    public bool IsFirstLineOfCommitBlock { get; init; }
}
```

#### Step 8.4: Add blame state to MainWindowViewModel

**File**: `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`
- Add:
  ```csharp
  public ObservableCollection<BlameLineViewModel> BlameLines { get; } = new();
  [ObservableProperty] private bool _isBlameMode;
  [ObservableProperty] private string _blameFilePath = string.Empty;
  ```
- Add `[RelayCommand] private async Task ShowBlameAsync(string? filePath)`:
  1. Determine the commit hash: if a historical commit is selected, use its hash; if working tree, pass `null` (blame HEAD).
  2. Call `_git.GetBlameAsync(RepoPath, filePath, commitHash)`.
  3. Convert `BlameResult.Lines` to `BlameLineViewModel` list. Mark `IsFirstLineOfCommitBlock = true` when the commit hash changes from the previous line (for visual grouping).
  4. Set `IsBlameMode = true`, `BlameFilePath = filePath`.
- Add `[RelayCommand] private void ExitBlame()` that clears blame state.
- Add `NavigateToCommitFromBlame(string commitHash)` that finds the commit in `Commits` and sets `SelectedCommit`.

#### Step 8.5: Add "Blame" to file context menu

**File**: `src/GrumpyGit.App/Views/MainWindow.axaml`
- Add a context menu to the file list items (the historical commit file ListBox and the unstaged/staged ListBoxes):
  ```xml
  <Grid.ContextMenu>
      <ContextMenu>
          <MenuItem Header="Blame"
                    CommandParameter="{Binding Path}"
                    Command="{Binding DataContext.ShowBlameCommand, ElementName=CommitListBox}"/>
          <!-- History menu item will be added in Feature 9 -->
      </ContextMenu>
  </Grid.ContextMenu>
  ```

#### Step 8.6: Create BlameView control

**File**: `src/GrumpyGit.App/Controls/BlameView.axaml` (NEW)
**File**: `src/GrumpyGit.App/Controls/BlameView.axaml.cs` (NEW)
- A UserControl with:
  - A header bar showing the file path and an "Exit Blame" button.
  - A virtualized ListBox (or ItemsRepeater) bound to `BlameLines`.
  - Each row displays: `[short-hash] [author] [date] | [line-number] [content]`.
  - The blame annotation columns use a darker background. The `IsFirstLineOfCommitBlock` flag triggers a top border/separator line and shows the author/date (subsequent lines in the same block show blank gutter to reduce noise).
  - Clicking on the hash text triggers `NavigateToCommitFromBlame`.
- Use monospace font (`Consolas,Cascadia Code,monospace`) for the content column.
- Use AvaloniaEdit for the code content with TextMate syntax highlighting (create a single read-only editor, bind its text to the full file content, and overlay the blame gutter as a custom margin).

Alternative simpler approach: Use a single AvaloniaEdit `TextEditor` for the code, and implement a custom `AbstractMargin` subclass (`BlameGutterMargin`) that renders the blame annotations (hash, author, date) in the left margin. This integrates cleanly with AvaloniaEdit's architecture.

#### Step 8.7: Wire BlameView into MainWindow

**File**: `src/GrumpyGit.App/Views/MainWindow.axaml`
- In the diff viewer area (bottom-right), add the BlameView as an alternative to the DiffViewer, toggled by `IsBlameMode`:
  ```xml
  <controls:DiffViewer IsVisible="{Binding !IsBlameMode}" ... />
  <controls:BlameView  IsVisible="{Binding IsBlameMode}"
                        BlameLines="{Binding BlameLines}"
                        FilePath="{Binding BlameFilePath}" />
  ```

---

## Feature 9: File History

### Goal
Add a "History" option when right-clicking a file. Run `git log --follow -- <file>` and show the commits that touched that file in a filtered list. Selecting a commit shows the diff for just that file at that commit.

### Implementation Order

#### Step 9.1: Add `GetFileHistoryAsync` to GitService

**File**: `src/GrumpyGit.Core/Git/IGitService.cs`
- Add:
  ```csharp
  Task<IReadOnlyList<CommitNode>> GetFileHistoryAsync(
      string repoPath, string filePath, CancellationToken ct = default);
  ```

**File**: `src/GrumpyGit.Core/Git/GitService.cs`
- Run `git log --follow --format="%H%x00%P%x00%an%x00%ae%x00%ai%x00%D%x00%s%x1E" -- <filePath>`.
- Reuse the existing `ParseCommitGraph` method to parse the output (same format).
- Validate `filePath` with `ValidateFilePath`.

#### Step 9.2: Add file history state to MainWindowViewModel

**File**: `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`
- Add:
  ```csharp
  public ObservableCollection<CommitRowViewModel> FileHistoryCommits { get; } = new();
  [ObservableProperty] private bool _isFileHistoryMode;
  [ObservableProperty] private string _fileHistoryPath = string.Empty;
  [ObservableProperty] private CommitRowViewModel? _selectedFileHistoryCommit;
  ```
- Add `partial void OnSelectedFileHistoryCommitChanged(CommitRowViewModel? value)`:
  1. When a commit is selected in the file history list, load the diff for just the tracked file at that commit using `_git.GetFileDiffAsync(RepoPath, value.Hash, FileHistoryPath)`.
  2. Parse and display in the diff viewer.

#### Step 9.3: Add ShowFileHistory command

**File**: `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`
- Add `[RelayCommand] private async Task ShowFileHistoryAsync(string? filePath)`:
  1. Call `_git.GetFileHistoryAsync(RepoPath, filePath)`.
  2. Convert results to `CommitRowViewModel` (without graph segments -- set `TotalLanes = 0`, `Segments = []`).
  3. Populate `FileHistoryCommits`.
  4. Set `IsFileHistoryMode = true`, `FileHistoryPath = filePath`.
- Add `[RelayCommand] private void ExitFileHistory()`:
  1. Set `IsFileHistoryMode = false`.
  2. Clear `FileHistoryCommits`.

#### Step 9.4: Add "History" to file context menu

**File**: `src/GrumpyGit.App/Views/MainWindow.axaml`
- In the file context menu (added in Step 8.5), add:
  ```xml
  <MenuItem Header="History"
            CommandParameter="{Binding Path}"
            Command="{Binding DataContext.ShowFileHistoryCommand, ElementName=CommitListBox}"/>
  ```

#### Step 9.5: Add file history panel to MainWindow

**File**: `src/GrumpyGit.App/Views/MainWindow.axaml`
- When `IsFileHistoryMode` is true, replace the commit list area with the file history view:
  ```xml
  <!-- In the top panel (commit list area), add an alternative view -->
  <Border IsVisible="{Binding IsFileHistoryMode}" Background="#2A2A3E">
      <Grid RowDefinitions="Auto,*">
          <StackPanel Grid.Row="0" Orientation="Horizontal" Spacing="8" Margin="8,4">
              <TextBlock Classes="panel-header"
                         Text="{Binding FileHistoryPath, StringFormat='HISTORY: {0}'}"/>
              <Button Classes="toolbar-btn" Content="Back"
                      Command="{Binding ExitFileHistoryCommand}"/>
          </StackPanel>
          <ListBox Grid.Row="1" Background="Transparent" BorderThickness="0"
                   ItemsSource="{Binding FileHistoryCommits}"
                   SelectedItem="{Binding SelectedFileHistoryCommit, Mode=TwoWay}">
              <ListBox.ItemTemplate>
                  <DataTemplate x:DataType="vm:CommitRowViewModel">
                      <Grid ColumnDefinitions="*,Auto">
                          <TextBlock Grid.Column="0" Text="{Binding DisplayText}"
                                     TextTrimming="CharacterEllipsis"/>
                          <TextBlock Grid.Column="1" Text="{Binding FormattedDate}"
                                     Foreground="#6060A0" FontSize="11" Margin="12,0"/>
                      </Grid>
                  </DataTemplate>
              </ListBox.ItemTemplate>
          </ListBox>
      </Grid>
  </Border>
  ```
- The existing commit list panel should be hidden when `IsFileHistoryMode` is true (use `IsVisible="{Binding !IsFileHistoryMode}"`).
- The bottom file list panel is hidden or shows only the single file. The diff viewer shows the diff for the selected commit.

---

## Feature 10: Tag Management

### Goal
Add tag operations: create tag (lightweight and annotated), delete tag, push tags. Show tags in the branch sidebar grouped separately from branches. Allow creating a tag from any commit via right-click context menu.

### Implementation Order

#### Step 10.1: Add tag-related git operations to GitService

**File**: `src/GrumpyGit.Core/Git/IGitService.cs`
- Add:
  ```csharp
  Task<IReadOnlyList<TagInfo>> GetTagsAsync(string repoPath, CancellationToken ct = default);
  Task CreateLightweightTagAsync(string repoPath, string tagName, string commitHash, CancellationToken ct = default);
  Task CreateAnnotatedTagAsync(string repoPath, string tagName, string commitHash, string message, CancellationToken ct = default);
  Task DeleteTagAsync(string repoPath, string tagName, CancellationToken ct = default);
  Task PushTagAsync(string repoPath, string tagName, string remote = "origin", CancellationToken ct = default);
  Task PushAllTagsAsync(string repoPath, string remote = "origin", CancellationToken ct = default);
  Task DeleteRemoteTagAsync(string repoPath, string tagName, string remote = "origin", CancellationToken ct = default);
  ```

**File**: `src/GrumpyGit.Core/Models/TagInfo.cs` (NEW)
```csharp
namespace GrumpyGit.Core.Models;

public record TagInfo(
    string Name,
    string CommitHash,
    bool IsAnnotated,
    string? Message = null,
    string? TaggerName = null,
    DateTimeOffset? TagDate = null);
```

**File**: `src/GrumpyGit.Core/Git/GitService.cs`
- Add a `ValidateTagName` method (same regex as `ValidateBranch` -- tag names follow the same rules).
- `GetTagsAsync`:
  - Run `git tag --list --format="%(refname:short)%x00%(objecttype)%x00%(*objectname)%x00%(objectname)%x00%(creatordate:iso-strict)%x00%(subject)%x00%(creatorname)%x1E"`.
  - Parse: `objecttype` is `commit` for lightweight tags, `tag` for annotated tags. For annotated tags, `*objectname` is the dereferenced commit hash; for lightweight tags, use `objectname`.
  - Return a list of `TagInfo`.
- `CreateLightweightTagAsync`: Run `git tag <tagName> <commitHash>`.
- `CreateAnnotatedTagAsync`: Run `git tag -a <tagName> <commitHash> -m <message>`.
- `DeleteTagAsync`: Run `git tag -d <tagName>`.
- `PushTagAsync`: Run `git push <remote> <tagName>`.
- `PushAllTagsAsync`: Run `git push <remote> --tags`.
- `DeleteRemoteTagAsync`: Run `git push <remote> --delete <tagName>`.

#### Step 10.2: Add tag collections to MainWindowViewModel

**File**: `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`
- Add:
  ```csharp
  public ObservableCollection<TagInfo> Tags { get; } = new();
  ```
- In `LoadRepoAsync`, add a `_git.GetTagsAsync(RepoPath)` call (run in parallel with the other tasks). Populate the `Tags` collection.

#### Step 10.3: Show tags in the branch sidebar

**File**: `src/GrumpyGit.App/Views/MainWindow.axaml`
- In the branch sidebar (Column 0), between the branch list and the stash section, add a TAGS section:
  ```xml
  <StackPanel IsVisible="{Binding !!Tags.Count}">
      <TextBlock Classes="panel-header" Text="TAGS" Background="#252540" Padding="8,4"/>
      <ListBox Background="Transparent" BorderThickness="0"
               ItemsSource="{Binding Tags}"
               MaxHeight="150"
               ScrollViewer.VerticalScrollBarVisibility="Auto">
          <ListBox.ItemTemplate>
              <DataTemplate x:DataType="models:TagInfo">
                  <Grid ColumnDefinitions="*,Auto">
                      <Grid.ContextMenu>
                          <ContextMenu>
                              <MenuItem Header="Push Tag"
                                        CommandParameter="{Binding Name}"
                                        Command="{Binding DataContext.PushTagCommand, ...}"/>
                              <MenuItem Header="Delete Tag"
                                        CommandParameter="{Binding Name}"
                                        Command="{Binding DataContext.DeleteTagCommand, ...}"/>
                              <MenuItem Header="Delete Remote Tag"
                                        CommandParameter="{Binding Name}"
                                        Command="{Binding DataContext.DeleteRemoteTagCommand, ...}"/>
                          </ContextMenu>
                      </Grid.ContextMenu>
                      <TextBlock Grid.Column="0" Text="{Binding Name}"
                                 FontSize="12" Foreground="#B0D0B0"
                                 TextTrimming="CharacterEllipsis"/>
                      <TextBlock Grid.Column="1"
                                 Text="{Binding CommitHash, Converter={...}}"
                                 FontSize="10" Foreground="#606080" Margin="4,0"/>
                  </Grid>
              </DataTemplate>
          </ListBox.ItemTemplate>
      </ListBox>
  </StackPanel>
  ```
- Use a different color for tags (e.g., green-tinted `#B0D0B0`) to distinguish from branches.

#### Step 10.4: Add "Create Tag" to commit context menu

**File**: `src/GrumpyGit.App/Views/MainWindow.axaml`
- In the commit list `ContextMenu`, add:
  ```xml
  <MenuItem Header="Create Tag..."
            CommandParameter="{Binding Hash}"
            Command="{Binding DataContext.StartCreateTagCommand, ElementName=CommitListBox}"
            IsVisible="{Binding !IsWorkingTree}"/>
  ```

#### Step 10.5: Add tag creation state and commands

**File**: `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`
- Add:
  ```csharp
  [ObservableProperty] private bool _isCreatingTag;
  [ObservableProperty] private string _newTagName = string.Empty;
  [ObservableProperty] private string _newTagMessage = string.Empty;
  [ObservableProperty] private bool _isAnnotatedTag;
  [ObservableProperty] private string _tagTargetHash = string.Empty;
  ```
- `StartCreateTagCommand(string? commitHash)`:
  1. Set `TagTargetHash = commitHash`, `IsCreatingTag = true`.
  2. Show the tag creation bar (reuse the branch operations bar pattern).
- `ConfirmCreateTagCommand`:
  1. If `IsAnnotatedTag`, call `_git.CreateAnnotatedTagAsync(RepoPath, NewTagName, TagTargetHash, NewTagMessage)`.
  2. Otherwise, call `_git.CreateLightweightTagAsync(RepoPath, NewTagName, TagTargetHash)`.
  3. Reset state and reload tags.
- `CancelCreateTagCommand`: Reset `IsCreatingTag`.

#### Step 10.6: Add tag creation bar to the UI

**File**: `src/GrumpyGit.App/Views/MainWindow.axaml`
- In Row 1 (branch operations bar), add a tag creation sub-panel alongside the existing branch/merge panels:
  ```xml
  <StackPanel Orientation="Horizontal" Spacing="6" IsVisible="{Binding IsCreatingTag}">
      <TextBlock Text="New tag:" Foreground="#A0A0C0" VerticalAlignment="Center" FontSize="12"/>
      <TextBox Classes="branch-input" Text="{Binding NewTagName, Mode=TwoWay}"
               Watermark="v1.0.0" Width="120"/>
      <CheckBox Content="Annotated" IsChecked="{Binding IsAnnotatedTag}"
                Foreground="#A0A0C0" VerticalAlignment="Center"/>
      <TextBox Classes="branch-input" Text="{Binding NewTagMessage, Mode=TwoWay}"
               Watermark="Tag message..." Width="200"
               IsVisible="{Binding IsAnnotatedTag}"/>
      <TextBlock Text="{Binding TagTargetHash}" Foreground="#606090"
                 FontSize="11" VerticalAlignment="Center" Margin="4,0"/>
      <Button Classes="toolbar-btn" Content="Create" Command="{Binding ConfirmCreateTagCommand}"/>
      <Button Classes="toolbar-btn" Content="Cancel" Command="{Binding CancelCreateTagCommand}"/>
  </StackPanel>
  ```
- Update `IsBranchBarVisible` to also include `IsCreatingTag`:
  ```csharp
  public bool IsBranchBarVisible => IsCreatingBranch || IsMerging || IsCreatingTag;
  ```

#### Step 10.7: Add tag management commands

**File**: `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs`
- `[RelayCommand] private async Task PushTagAsync(string? tagName)`:
  1. Call `_git.PushTagAsync(RepoPath, tagName)`.
  2. Show status message.
- `[RelayCommand] private async Task DeleteTagAsync(string? tagName)`:
  1. Show confirmation dialog.
  2. Call `_git.DeleteTagAsync(RepoPath, tagName)`.
  3. Reload tags.
- `[RelayCommand] private async Task DeleteRemoteTagAsync(string? tagName)`:
  1. Show confirmation dialog.
  2. Call `_git.DeleteRemoteTagAsync(RepoPath, tagName)`.
  3. Show status message.
- Add a "Push Tags" toolbar button that calls `_git.PushAllTagsAsync(RepoPath)`.

#### Step 10.8: Navigate to tag's commit when clicking a tag in sidebar

**File**: `src/GrumpyGit.App/Views/MainWindow.axaml.cs` (or via command)
- When a tag is double-clicked or selected in the sidebar, find the commit with matching hash in the `Commits` collection and set `SelectedCommit` to navigate there.

---

## Implementation Sequence Across Features

The recommended order of implementation, considering dependencies:

1. **Feature 10 (Tags)** -- Minimal dependencies, adds to existing sidebar. Good warm-up.
2. **Feature 5 (Commit Range Comparison)** -- Builds on existing `GetCommitRangeDiffAsync`. Self-contained.
3. **Feature 9 (File History)** -- Simple git log variant, reuses existing parsing. Self-contained.
4. **Feature 8 (Blame)** -- New porcelain parsing and new UI control. Medium complexity.
5. **Feature 6 (Conflict Resolution)** -- Requires parsing unmerged entries and new git operations. Builds on the merge flow that already exists.
6. **Feature 7 (Interactive Rebase)** -- Most complex. Requires managing multi-step async git operations, environment variable injection, and a draggable UI.

---

## Files Summary

### New Files
| File | Feature |
|---|---|
| `src/GrumpyGit.Core/Models/TagInfo.cs` | 10 |
| `src/GrumpyGit.Core/Models/BlameLine.cs` | 8 |
| `src/GrumpyGit.App/ViewModels/BlameLineViewModel.cs` | 8 |
| `src/GrumpyGit.App/ViewModels/RebaseItemViewModel.cs` | 7 |
| `src/GrumpyGit.App/Controls/BlameView.axaml` | 8 |
| `src/GrumpyGit.App/Controls/BlameView.axaml.cs` | 8 |

### Modified Files
| File | Features |
|---|---|
| `src/GrumpyGit.Core/Git/IGitService.cs` | 5, 6, 7, 8, 9, 10 |
| `src/GrumpyGit.Core/Git/GitService.cs` | 5, 6, 7, 8, 9, 10 |
| `src/GrumpyGit.Core/Models/FileChange.cs` | 6 |
| `src/GrumpyGit.App/ViewModels/MainWindowViewModel.cs` | 5, 6, 7, 8, 9, 10 |
| `src/GrumpyGit.App/ViewModels/FileChangeViewModel.cs` | 6 |
| `src/GrumpyGit.App/Views/MainWindow.axaml` | 5, 6, 7, 8, 9, 10 |
| `src/GrumpyGit.App/Views/MainWindow.axaml.cs` | 5, 7 |
