namespace GrumpyGit.Core.Models;

/// <summary>
/// Which paths are worth rendering as pictures instead of text in the diff.
///
/// Deliberately extension-based rather than content-sniffing: the decision has to be
/// made before the blob is fetched (to choose which fetch to do at all), and a file
/// named <c>.png</c> that is not a PNG will simply fail to decode and fall back to the
/// binary summary — a cheaper failure than sniffing every binary blob in a repo.
/// </summary>
public static class ImageFileTypes
{
    /// <summary>
    /// Formats Skia can decode, which is what Avalonia's Bitmap uses underneath.
    /// SVG is excluded on purpose — it is text, so the normal line diff is strictly
    /// more useful for it than two rendered pictures.
    /// </summary>
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".webp", ".wbmp",
    };

    public static bool IsImage(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var dot = path.LastIndexOf('.');
        if (dot < 0 || dot == path.Length - 1)
            return false;

        return Extensions.Contains(path[dot..]);
    }
}
