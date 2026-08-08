using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Git;

/// <summary>
/// One sentence on what happened to a whole file, from data git already produced.
///
/// The companion to <see cref="ChangeDescriber"/>, which does the same job per symbol, and
/// it inherits the same rule: <strong>never say anything that could be confidently
/// wrong</strong>. It reports structure — what was added, removed or reworked, and where —
/// and never intent. "Adds Guard() and reworks Close()" is decidable from the diff;
/// "tightens validation" is not.
///
/// Deliberately independent of the local model. This is always present, on every file, for
/// every user, whether or not any weights exist on the machine — the model's reading sits
/// above it and adds interpretation, but is never the only description of a file.
/// </summary>
public static class FileChangeDescriber
{
    /// <summary>Beyond this many named symbols the list stops being readable at a glance.</summary>
    private const int MaxNamedSymbols = 2;

    public static string Describe(FileChangeSummary summary, ParsedDiff diff)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(diff);

        // git states these outright in the file header, so they need no inference and
        // outrank anything the symbol list would say.
        if (HasHeader(diff, "new file mode"))
            return $"New file — {Plural(summary.Added, "line")}.";

        if (HasHeader(diff, "deleted file mode"))
            return $"File deleted — {Plural(summary.Removed, "line")} gone.";

        var renamed = RenameTarget(diff);
        var prefix = renamed is null ? string.Empty : $"Renamed to {renamed}. ";

        if (summary.Added == 0 && summary.Removed == 0)
            return renamed is null ? "No content change." : prefix.TrimEnd();

        var counts = $"+{summary.Added} −{summary.Removed}";

        // No language driver for this file type, so there are no symbol names to give.
        // Counting is the most that can be said without inventing structure.
        var named = summary.Symbols.Where(s => !s.IsAnonymous).ToList();
        if (named.Count == 0)
            return $"{prefix}{counts} across {Plural(diff.Hunks.Count, "hunk")}.";

        var verb = VerbFor(named);
        var names = NameList(named);

        return $"{prefix}{verb} {names}. {counts}.";
    }

    /// <summary>
    /// A verb the whole file can carry. Mixed kinds get the neutral one rather than the
    /// verb of whichever symbol happened to sort first.
    /// </summary>
    private static string VerbFor(IReadOnlyList<SymbolChange> symbols)
    {
        var kinds = symbols.Select(s => s.Kind).Distinct().ToList();
        if (kinds.Count > 1) return "Changes";

        return kinds[0] switch
        {
            SymbolChangeKind.Added => "Adds",
            SymbolChangeKind.Removed => "Removes",
            _ => "Reworks",
        };
    }

    private static string NameList(IReadOnlyList<SymbolChange> symbols)
    {
        var shown = symbols.Take(MaxNamedSymbols).Select(s => Shorten(s.Symbol)).ToList();
        var rest = symbols.Count - shown.Count;

        var list = shown.Count == 1
            ? shown[0]
            : string.Join(" and ", shown);

        return rest == 0 ? list : $"{list} and {rest} more";
    }

    /// <summary>
    /// Git's hunk header carries the whole declaration — modifiers, parameters and all.
    /// Cut at the parameter list so a sentence made of two of them still fits on a line.
    /// </summary>
    private static string Shorten(string symbol)
    {
        var paren = symbol.IndexOf('(');
        var text = (paren > 0 ? symbol[..paren] : symbol).Trim();

        // Take the last word: "private static void Guard" is Guard to a reader.
        var space = text.LastIndexOf(' ');
        if (space > 0 && space < text.Length - 1)
            text = text[(space + 1)..];

        return paren > 0 ? text + "()" : text;
    }

    private static bool HasHeader(ParsedDiff diff, string marker) =>
        diff.FileHeaderLines.Any(l => l.StartsWith(marker, StringComparison.Ordinal));

    private static string? RenameTarget(ParsedDiff diff)
    {
        const string marker = "rename to ";
        var line = diff.FileHeaderLines.FirstOrDefault(l => l.StartsWith(marker, StringComparison.Ordinal));
        return line?[marker.Length..].Trim();
    }

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
