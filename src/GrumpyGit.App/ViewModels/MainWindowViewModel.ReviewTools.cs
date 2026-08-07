using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrumpyGit.App.Services;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// Review-mode tooling: the directory tree over the changed files, and per-file notes.
/// </summary>
public partial class MainWindowViewModel
{
    // ── File tree ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Tree view over the changed files. Off by default because the flat list is
    /// faster to scan for a handful of files; the tree earns its keep on large
    /// changesets.
    /// </summary>
    [ObservableProperty] private bool _isFileTreeVisible;

    // Separate trees per section rather than one merged tree: the staged/unstaged
    // split drives every staging action, so collapsing it into a single hierarchy
    // would lose the section headers and their bulk operations.
    public ObservableCollection<FileTreeNodeViewModel> StagedTree { get; } = new();
    public ObservableCollection<FileTreeNodeViewModel> UnstagedTree { get; } = new();
    public ObservableCollection<FileTreeNodeViewModel> ConflictedTree { get; } = new();

    [RelayCommand]
    private void ToggleFileTree() => IsFileTreeVisible = !IsFileTreeVisible;

    /// <summary>
    /// The tree has to be built here rather than in the command, because the toggle in
    /// the UI binds <see cref="IsFileTreeVisible"/> directly — going through the command
    /// only would leave the tree empty whenever the user flipped the toggle.
    /// </summary>
    partial void OnIsFileTreeVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(IsFlatFileListVisible));
        if (value) RebuildFileTree();
    }

    /// <summary>The flat list and the tree are alternatives, never both at once.</summary>
    public bool IsFlatFileListVisible => !IsFileTreeVisible;

    /// <summary>
    /// Rebuilds the tree from whichever files are currently listed. Called after any
    /// reload, since staging moves files between the collections.
    /// </summary>
    public void RebuildFileTree()
    {
        StagedTree.Clear();
        UnstagedTree.Clear();
        ConflictedTree.Clear();
        if (!IsFileTreeVisible) return;

        foreach (var node in FileTreeBuilder.Build(ConflictedFiles)) ConflictedTree.Add(node);
        foreach (var node in FileTreeBuilder.Build(StagedFiles)) StagedTree.Add(node);
        foreach (var node in FileTreeBuilder.Build(ChangedFiles)) UnstagedTree.Add(node);

        // A freshly built tree is expanded, so keep the toggle honest.
        SetExpandedRecursive(AllTreeNodes, IsTreeFullyExpanded);
    }

    private System.Collections.Generic.IEnumerable<FileTreeNodeViewModel> AllTreeNodes =>
        ConflictedTree.Concat(StagedTree).Concat(UnstagedTree);

    /// <summary>
    /// Single expand/collapse-all control. One toggle reads better than two buttons
    /// because the tree is only ever fully-open or fully-closed from this affordance.
    /// </summary>
    [ObservableProperty] private bool _isTreeFullyExpanded = true;

    partial void OnIsTreeFullyExpandedChanged(bool value) =>
        SetExpandedRecursive(AllTreeNodes, value);

    private static void SetExpandedRecursive(
        System.Collections.Generic.IEnumerable<FileTreeNodeViewModel> nodes,
        bool expanded)
    {
        foreach (var node in nodes)
        {
            // Only directories have an expanded state; recursing into file nodes is
            // harmless but pointless, and their Children list is always empty.
            if (!node.IsDirectory) continue;

            node.IsExpanded = expanded;
            SetExpandedRecursive(node.Children, expanded);
        }
    }

    /// <summary>Selecting a tree node selects the underlying file; folders just expand.</summary>
    [RelayCommand]
    private void SelectTreeNode(FileTreeNodeViewModel? node)
    {
        if (node is null) return;

        if (node.IsDirectory)
        {
            node.IsExpanded = !node.IsExpanded;
            return;
        }

        if (node.File is not null)
            SelectedFile = node.File;
    }

    // ── Review notes ──────────────────────────────────────────────────────────

    private ReviewNotesStore? _notesStore;

    [ObservableProperty] private bool _isNotesPanelVisible;
    [ObservableProperty] private string _currentNote = string.Empty;
    [ObservableProperty] private int _notedFileCount;

    public bool HasNotedFiles => NotedFileCount > 0;

    partial void OnNotedFileCountChanged(int value) => OnPropertyChanged(nameof(HasNotedFiles));

    [RelayCommand]
    private void ToggleNotesPanel() => IsNotesPanelVisible = !IsNotesPanelVisible;

    /// <summary>
    /// Writes the note through on every keystroke. Notes are small and the store is a
    /// single local file, so persisting eagerly is cheaper than the risk of losing a
    /// finding because the app closed before an explicit save.
    /// </summary>
    /// <summary>
    /// Path the note belongs to. Taken from the selection rather than
    /// <c>DiffFilePath</c>, which is only assigned once the diff has finished loading —
    /// using it would attach the note to the previously viewed file.
    /// </summary>
    private string? CurrentNotePath => SelectedFile?.Path ?? SelectedReviewFile?.FilePath;

    partial void OnCurrentNoteChanged(string value)
    {
        // Skip when the text changed because we switched files rather than because
        // the user typed — otherwise selecting a file overwrites its note.
        if (_suppressNotePersist) return;

        var path = CurrentNotePath;
        if (_notesStore is null || string.IsNullOrEmpty(path)) return;

        _notesStore.Set(path, value);
        NotedFileCount = _notesStore.Count;
        MarkNotedFiles();
    }

    /// <summary>Loads the note belonging to the file now on screen.</summary>
    public void LoadNoteForCurrentFile()
    {
        var path = CurrentNotePath;
        SetNoteWithoutPersisting(
            _notesStore is null || string.IsNullOrEmpty(path)
                ? string.Empty
                : _notesStore.Get(path));
    }

    private bool _suppressNotePersist;

    /// <summary>
    /// Sets the note text without writing it back — used when switching files, where
    /// the change reflects a different file rather than an edit by the user. Without
    /// this, selecting a file would overwrite its note with the previous file's text.
    /// </summary>
    private void SetNoteWithoutPersisting(string value)
    {
        if (CurrentNote == value) return;

        _suppressNotePersist = true;
        try { CurrentNote = value; }
        finally { _suppressNotePersist = false; }
    }

    private void MarkNotedFiles()
    {
        if (_notesStore is null) return;

        var noted = _notesStore.NotedPaths.ToHashSet(System.StringComparer.Ordinal);

        foreach (var f in ChangedFiles) f.HasNote = noted.Contains(f.Path);
        foreach (var f in StagedFiles) f.HasNote = noted.Contains(f.Path);
        foreach (var f in ConflictedFiles) f.HasNote = noted.Contains(f.Path);
        foreach (var f in PrFiles) f.HasNote = noted.Contains(f.FilePath);
    }

    /// <summary>Initialises the notes store for a repository.</summary>
    private void InitialiseReviewTools()
    {
        _notesStore = string.IsNullOrEmpty(RepoPath) ? null : new ReviewNotesStore(RepoPath);

        // A failed write must be visible: the note is on screen but not on disk, and
        // the user would otherwise only find out after restarting.
        if (_notesStore is not null)
            _notesStore.SaveFailed += message => StatusMessage = message;

        NotedFileCount = _notesStore?.Count ?? 0;
        SetNoteWithoutPersisting(string.Empty);
    }
}
