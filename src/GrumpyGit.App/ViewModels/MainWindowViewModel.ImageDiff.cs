using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using GrumpyGit.Core.Models;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// Rendering image files as pictures instead of as a "binary files differ" stub.
///
/// For a visual git client this is the difference between an icon change being
/// reviewable and being invisible — a text diff of a PNG tells you nothing at all.
/// </summary>
public partial class MainWindowViewModel
{
    [ObservableProperty] private ImageDiffViewModel? _imageDiff;

    /// <summary>True while an image comparison is on screen instead of the text diff.</summary>
    public bool IsImageDiff => ImageDiff is not null;

    /// <summary>
    /// The text diff, the image preview and blame all occupy the same row, so exactly
    /// one of them must claim it. Expressed here rather than as a compound binding so
    /// the precedence is stated once.
    /// </summary>
    public bool IsTextDiffVisible => !IsBlameVisible && !IsImageDiff;

    partial void OnImageDiffChanged(ImageDiffViewModel? oldValue, ImageDiffViewModel? newValue)
    {
        // Bitmaps hold unmanaged memory; browsing a folder of icons would otherwise
        // accumulate them for the lifetime of the process.
        oldValue?.Dispose();
        OnPropertyChanged(nameof(IsImageDiff));
        OnPropertyChanged(nameof(IsTextDiffVisible));
    }

    private void ClearImageDiff() => ImageDiff = null;

    // IsBlameVisible is declared in the main partial; its change notification has to
    // reach IsTextDiffVisible too, or switching to blame leaves the text diff drawn
    // underneath it in the same grid row.
    partial void OnIsBlameVisibleChanged(bool value) =>
        OnPropertyChanged(nameof(IsTextDiffVisible));

    /// <summary>
    /// Loads before/after pictures for an image file.
    ///
    /// Returns false when this is not an image path, so the caller falls straight
    /// through to the normal text diff.
    /// </summary>
    private async Task<bool> TryLoadImageDiffAsync(CommitRowViewModel commit, FileChangeViewModel file)
    {
        if (!ImageFileTypes.IsImage(file.Path))
            return false;

        try
        {
            byte[] beforeBytes;
            byte[] afterBytes;

            if (commit.IsWorkingTree)
            {
                // "Before" is whatever is committed; "after" is the file as it sits on
                // disk right now, which is what the user is actually looking at.
                beforeBytes = await _git.GetFileBlobAsync(RepoPath, "HEAD", file.Path);
                afterBytes = ReadWorkingTreeFile(file.Path);
            }
            else
            {
                afterBytes = await _git.GetFileBlobAsync(RepoPath, commit.Hash, file.Path);

                // A root commit has no parent to compare against, so everything in it
                // is an addition.
                beforeBytes = commit.IsMergeCommit || string.IsNullOrEmpty(commit.Hash)
                    ? []
                    : await _git.GetFileBlobAsync(RepoPath, commit.Hash + "^", file.Path);
            }

            ImageDiff = new ImageDiffViewModel
            {
                FilePath = file.Path,
                Before = ImageSide.FromBytes(beforeBytes),
                After = ImageSide.FromBytes(afterBytes),
            };

            // The text panes must be cleared or the previous file's diff stays visible
            // behind the image view.
            CurrentDiff = null;
            DiffHunks.Clear();
            DiffFilePath = file.Path;
            UpdateDiffStats(null);

            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not load image preview: {ex.Message}";
            ClearImageDiff();
            return false;
        }
    }

    /// <summary>
    /// Reads the working-tree copy, re-checking containment because this path bypasses
    /// GitService's own validation by touching the filesystem directly.
    /// </summary>
    private byte[] ReadWorkingTreeFile(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(RepoPath, relativePath));
        var root = Path.GetFullPath(RepoPath) + Path.DirectorySeparatorChar;

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            return [];

        return File.ReadAllBytes(full);
    }
}
