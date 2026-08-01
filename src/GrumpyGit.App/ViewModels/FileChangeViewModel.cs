using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GrumpyGit.App.ViewModels;

public partial class FileChangeViewModel : ObservableObject
{
    public string Path { get; init; } = string.Empty;
    public string OldPath { get; init; } = string.Empty;

    /// <summary>Status letter: "A" added, "M" modified, "D" deleted, "R" renamed, "?" untracked.</summary>
    public string StatusLabel { get; init; } = string.Empty;

    /// <summary>True when this entry represents a change already staged in the index.</summary>
    public bool IsStaged { get; init; }

    /// <summary>True when this file comes from the working tree (not from a historical commit).</summary>
    public bool IsWorkingTreeFile { get; init; }

    /// <summary>Stage or unstage this file. Null for historical-commit files.</summary>
    public IRelayCommand? ToggleStagingCommand { get; init; }

    public string DisplayPath => string.IsNullOrEmpty(OldPath) ? Path : $"{OldPath} → {Path}";

    /// <summary>
    /// Just the file name. Shown as the primary label so that trimming a long path in a
    /// narrow panel eats the directory, never the part that identifies the file.
    /// </summary>
    public string FileName
    {
        get
        {
            var idx = Path.LastIndexOfAny(['/', '\\']);
            return idx >= 0 ? Path[(idx + 1)..] : Path;
        }
    }

    /// <summary>Directory portion, shown dimmed beside the name.</summary>
    public string DirectoryPath
    {
        get
        {
            var idx = Path.LastIndexOfAny(['/', '\\']);
            return idx >= 0 ? Path[..idx] : string.Empty;
        }
    }

    public string DisplayText => string.IsNullOrEmpty(StatusLabel)
        ? DisplayPath
        : $"[{StatusLabel}]  {DisplayPath}";

    /// <summary>"−" when staged (click to unstage), "+" when unstaged (click to stage).</summary>
    public string StagingButtonText => IsStaged ? "−" : "+";

    // ── Per-file churn ────────────────────────────────────────────────────────

    [ObservableProperty] private int _linesAdded;
    [ObservableProperty] private int _linesRemoved;

    /// <summary>
    /// False for binary files, where git reports "-" instead of counts. Showing
    /// "+0 −0" for a changed binary would be actively misleading.
    /// </summary>
    [ObservableProperty] private bool _hasLineStats;

    /// <summary>True when the reviewer has written a note against this file.</summary>
    [ObservableProperty] private bool _hasNote;

    public string AddedLabel => $"+{LinesAdded}";
    public string RemovedLabel => $"−{LinesRemoved}";

    partial void OnLinesAddedChanged(int value) => OnPropertyChanged(nameof(AddedLabel));
    partial void OnLinesRemovedChanged(int value) => OnPropertyChanged(nameof(RemovedLabel));
}
