namespace GrumpyGit.Core.Models;

/// <summary>What happened to a symbol, independent of how many lines moved.</summary>
public enum SymbolChangeKind
{
    /// <summary>Only added lines — the symbol is new, or gained code.</summary>
    Added,

    /// <summary>Only removed lines — the symbol lost code, or went away.</summary>
    Removed,

    /// <summary>Both added and removed — the symbol was rewritten in place.</summary>
    Modified,
}

/// <summary>
/// One symbol touched by a diff, with the line budget it accounts for and where to
/// jump to see it.
/// </summary>
/// <param name="Symbol">
/// Enclosing declaration reported by git's hunk header, e.g. <c>private void OnLoaded(...)</c>.
/// Empty when no language driver applied — the summary then falls back to naming the
/// hunk by line range rather than inventing a symbol.
/// </param>
/// <param name="Added">Lines added within this symbol's hunks.</param>
/// <param name="Removed">Lines removed within this symbol's hunks.</param>
/// <param name="RenderedLineNumber">
/// Line in the rendered diff to scroll to — taken from the first hunk attributed to this
/// symbol, so clicking the entry lands on the change rather than the file header.
/// </param>
/// <param name="HunkCount">How many separate hunks were folded into this entry.</param>
/// <param name="Description">
/// A few words on what the change did, from <see cref="Git.ChangeDescriber"/> — e.g.
/// "reworked 2 lines · guard added". Rule-based and deliberately conservative: it counts
/// when it cannot be certain, so it is never confidently wrong.
/// </param>
public sealed record SymbolChange(
    string Symbol,
    int Added,
    int Removed,
    int RenderedLineNumber,
    int HunkCount,
    SymbolChangeKind Kind,
    string Description)
{
    /// <summary>True when git gave us no enclosing declaration for this hunk.</summary>
    public bool IsAnonymous => string.IsNullOrWhiteSpace(Symbol);
}

/// <summary>A file's contribution to a change, broken down by symbol.</summary>
public sealed record FileChangeSummary(
    string Path,
    int Added,
    int Removed,
    IReadOnlyList<SymbolChange> Symbols);
