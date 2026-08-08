namespace GrumpyGit.Core.LocalModel;

/// <summary>
/// The directory of models this application downloaded, and the only place it will delete
/// from.
///
/// It exists to keep one rule enforceable in one file: <strong>the app removes only files
/// it fetched itself</strong>. A user is free to point the setting at their own GGUF —
/// somewhere in their own downloads, or a share, or beside a dozen other models — and none
/// of that is ours to tidy. So deletion is expressed as "delete this catalogue entry",
/// never "delete this path", and the two checks below make an arbitrary path impossible to
/// express even if a later caller wanted to.
/// </summary>
public sealed class ModelStore
{
    private readonly string _directory;

    public ModelStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Directory must not be empty.", nameof(directory));

        _directory = Path.GetFullPath(directory);
    }

    public string Directory => _directory;

    /// <summary>Where this model would live, whether or not it is there yet.</summary>
    public string PathFor(ModelOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        return Path.Combine(_directory, option.FileName);
    }

    /// <summary>
    /// True only when every part is present. A sharded model missing its third file is not
    /// a model — llama.cpp would fail to load it, and the offer to download should stand.
    /// </summary>
    public bool IsInstalled(ModelOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        return option.Parts.All(p => File.Exists(Path.Combine(_directory, p.FileName)));
    }

    /// <summary>Bytes on disk for whichever parts are present. Zero when none are.</summary>
    public long InstalledBytes(ModelOption option)
    {
        ArgumentNullException.ThrowIfNull(option);

        long bytes = 0;
        foreach (var part in option.Parts)
        {
            var info = new FileInfo(Path.Combine(_directory, part.FileName));
            if (info.Exists) bytes += info.Length;
        }

        return bytes;
    }

    /// <summary>
    /// True when some but not all parts are present — an interrupted multi-file download.
    /// Worth surfacing, because the disk is spent and the model still will not load.
    /// </summary>
    public bool IsPartiallyInstalled(ModelOption option) =>
        !IsInstalled(option) && InstalledBytes(option) > 0;

    /// <summary>
    /// Deletes every part of <paramref name="option"/>, plus any <c>.part</c> left by an
    /// interrupted download. Returns the bytes reclaimed.
    ///
    /// Missing files are not an error: this is how a half-finished download is cleared, and
    /// the user asked for the model to be gone, not for a report on which files existed.
    /// </summary>
    public long Delete(ModelOption option)
    {
        ArgumentNullException.ThrowIfNull(option);

        long removed = 0;

        foreach (var part in option.Parts)
        {
            removed += TryDelete(part.FileName);
            removed += TryDelete(part.FileName + ".part");
        }

        return removed;
    }

    private long TryDelete(string fileName)
    {
        var path = Path.GetFullPath(Path.Combine(_directory, fileName));

        // Belt and braces. Every name reaching here is a compile-time constant from
        // ModelCatalogue, so neither check can fire today — they are here so that a future
        // caller passing something from a settings file or a repository cannot turn this
        // into a general-purpose file deleter.
        if (!path.StartsWith(_directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to delete outside the model directory.");

        if (!string.Equals(Path.GetFileName(path), fileName, StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to delete a path that is not a plain file name.");

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return 0;

            var length = info.Length;
            info.Delete();
            return length;
        }
        catch (IOException)
        {
            // Most likely the model currently loaded into llama.cpp still has the file
            // mapped. The caller unloads first; if it is still locked, saying so beats
            // half-deleting a sharded model.
            throw new InvalidOperationException(
                $"{fileName} is in use — close the review panel and try again.");
        }
    }
}
