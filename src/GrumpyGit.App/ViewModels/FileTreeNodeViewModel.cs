using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// A node in the changed-files tree: either a directory or a file.
///
/// A flat list of 60+ paths is hard to scan because the meaningful part of each path
/// (the file name) sits behind a long shared prefix. Grouping by directory puts that
/// prefix in one place and lets whole subtrees be collapsed away.
/// </summary>
public partial class FileTreeNodeViewModel : ObservableObject
{
    /// <summary>Display name — the folder or file name, not the full path.</summary>
    public required string Name { get; init; }

    /// <summary>Full repo-relative path. Empty for directory nodes.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>The underlying file row, or null for a directory.</summary>
    public FileChangeViewModel? File { get; init; }

    public bool IsDirectory => File is null;

    public ObservableCollection<FileTreeNodeViewModel> Children { get; } = new();

    [ObservableProperty] private bool _isExpanded = true;

    // Churn rolled up from descendants for directories, or the file's own counts.
    [ObservableProperty] private int _linesAdded;
    [ObservableProperty] private int _linesRemoved;
    [ObservableProperty] private int _fileCount;

    public string AddedLabel => $"+{LinesAdded}";
    public string RemovedLabel => $"−{LinesRemoved}";

    /// <summary>Directories show a file count; files show their status letter.</summary>
    public string CountLabel => IsDirectory
        ? $"{FileCount}"
        : File?.StatusLabel ?? string.Empty;

    partial void OnLinesAddedChanged(int value) => OnPropertyChanged(nameof(AddedLabel));
    partial void OnLinesRemovedChanged(int value) => OnPropertyChanged(nameof(RemovedLabel));
    partial void OnFileCountChanged(int value) => OnPropertyChanged(nameof(CountLabel));
}

/// <summary>Builds a directory tree from a flat list of changed files.</summary>
public static class FileTreeBuilder
{
    /// <summary>
    /// Groups files by their directory segments.
    ///
    /// Single-child directory chains are collapsed into one node
    /// (<c>src/GrumpyGit.App/Controls</c> rather than three nested levels), because
    /// expanding three times to reach one file is worse than the flat list it replaced.
    /// </summary>
    public static ObservableCollection<FileTreeNodeViewModel> Build(
        IEnumerable<FileChangeViewModel> files)
    {
        var root = new FileTreeNodeViewModel { Name = string.Empty };

        foreach (var file in files)
        {
            var segments = file.Path.Replace('\\', '/').Split('/');
            var current = root;

            // Everything except the last segment is a directory.
            for (var i = 0; i < segments.Length - 1; i++)
            {
                current = GetOrAddDirectory(current, segments[i]);
            }

            current.Children.Add(new FileTreeNodeViewModel
            {
                Name = segments[^1],
                Path = file.Path,
                File = file,
                LinesAdded = file.LinesAdded,
                LinesRemoved = file.LinesRemoved,
                FileCount = 1,
            });
        }

        CollapseSingleChildDirectories(root);
        RollUpTotals(root);

        return root.Children;
    }

    private static FileTreeNodeViewModel GetOrAddDirectory(FileTreeNodeViewModel parent, string name)
    {
        foreach (var child in parent.Children)
        {
            if (child.IsDirectory && child.Name == name)
                return child;
        }

        var created = new FileTreeNodeViewModel { Name = name };
        parent.Children.Add(created);
        return created;
    }

    private static void CollapseSingleChildDirectories(FileTreeNodeViewModel node)
    {
        for (var i = 0; i < node.Children.Count; i++)
        {
            var child = node.Children[i];
            if (!child.IsDirectory) continue;

            CollapseSingleChildDirectories(child);

            // A directory whose only entry is another directory adds a level of
            // nesting without adding information — merge their names.
            while (child.Children.Count == 1 && child.Children[0].IsDirectory)
            {
                var grandchild = child.Children[0];
                var merged = new FileTreeNodeViewModel { Name = $"{child.Name}/{grandchild.Name}" };
                foreach (var c in grandchild.Children)
                    merged.Children.Add(c);

                node.Children[i] = merged;
                child = merged;
            }
        }
    }

    private static (int Added, int Removed, int Files) RollUpTotals(FileTreeNodeViewModel node)
    {
        if (!node.IsDirectory)
            return (node.LinesAdded, node.LinesRemoved, 1);

        var added = 0;
        var removed = 0;
        var files = 0;

        foreach (var child in node.Children)
        {
            var (a, r, f) = RollUpTotals(child);
            added += a;
            removed += r;
            files += f;
        }

        node.LinesAdded = added;
        node.LinesRemoved = removed;
        node.FileCount = files;

        return (added, removed, files);
    }
}
