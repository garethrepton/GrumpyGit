using System;
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// One side of an image diff: the decoded picture plus the facts worth stating about it.
///
/// Decoding is attempted eagerly so a corrupt or unsupported file degrades to "cannot
/// preview" with the byte count still shown, rather than leaving an empty pane with no
/// explanation.
/// </summary>
public sealed class ImageSide
{
    public Bitmap? Image { get; init; }

    /// <summary>Size of the blob on disk, regardless of whether it decoded.</summary>
    public int ByteCount { get; init; }

    /// <summary>True when the file simply does not exist on this side (added or deleted).</summary>
    public bool IsAbsent { get; init; }

    public bool HasImage => Image is not null;

    /// <summary>Present but undecodable — corrupt, or a format Skia does not handle.</summary>
    public bool FailedToDecode => !IsAbsent && Image is null && ByteCount > 0;

    public string Dimensions => Image is null
        ? "—"
        : $"{Image.PixelSize.Width} × {Image.PixelSize.Height}";

    public string SizeLabel => IsAbsent ? "—" : FormatBytes(ByteCount);

    public static ImageSide Absent() => new() { IsAbsent = true };

    /// <summary>
    /// Decodes blob bytes into a bitmap. Never throws: an image that will not decode is
    /// a display problem, not a reason to fail loading the diff.
    /// </summary>
    public static ImageSide FromBytes(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return Absent();

        try
        {
            using var stream = new MemoryStream(bytes);
            return new ImageSide { Image = new Bitmap(stream), ByteCount = bytes.Length };
        }
        catch
        {
            return new ImageSide { ByteCount = bytes.Length };
        }
    }

    private static string FormatBytes(int bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.##} MB",
    };
}

/// <summary>
/// A before/after image comparison, shown in place of the text diff for picture files.
/// </summary>
public partial class ImageDiffViewModel : ObservableObject, IDisposable
{
    public required string FilePath { get; init; }
    public required ImageSide Before { get; init; }
    public required ImageSide After { get; init; }

    public bool IsAdded => Before.IsAbsent && !After.IsAbsent;
    public bool IsDeleted => !Before.IsAbsent && After.IsAbsent;

    public string ChangeSummary =>
        IsAdded ? "Added" :
        IsDeleted ? "Deleted" :
        "Modified";

    /// <summary>
    /// Byte delta between the two sides, e.g. "+1.2 KB". Only meaningful when both
    /// sides exist — for an add or delete the size is the whole file, not a change.
    /// </summary>
    public string SizeDelta
    {
        get
        {
            if (Before.IsAbsent || After.IsAbsent)
                return string.Empty;

            var delta = After.ByteCount - Before.ByteCount;
            if (delta == 0) return "same size";

            var sign = delta > 0 ? "+" : "−";
            var magnitude = Math.Abs(delta);
            var text = magnitude < 1024
                ? $"{magnitude} B"
                : magnitude < 1024 * 1024
                    ? $"{magnitude / 1024.0:0.#} KB"
                    : $"{magnitude / (1024.0 * 1024.0):0.##} MB";

            return $"{sign}{text}";
        }
    }

    /// <summary>
    /// True when both sides decoded but differ in pixel dimensions — worth calling out
    /// explicitly, because a resize is easy to miss when the two are shown at fitted
    /// scale side by side.
    /// </summary>
    public bool DimensionsChanged =>
        Before.HasImage && After.HasImage &&
        Before.Image!.PixelSize != After.Image!.PixelSize;

    public void Dispose()
    {
        Before.Image?.Dispose();
        After.Image?.Dispose();
    }
}
