using GrumpyGit.Core.Models;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// One row of the change summary: a symbol the diff touched, what happened to it, and
/// where to jump to see it.
/// </summary>
public sealed class SymbolChangeViewModel
{
    public required SymbolChange Model { get; init; }

    /// <summary>
    /// What to show. Falls back to naming the hunk when no language driver supplied an
    /// enclosing declaration, rather than leaving a blank row or inventing a name.
    /// </summary>
    public string Display => Model.IsAnonymous
        ? $"hunk at line {Model.RenderedLineNumber}"
        : Model.Symbol;

    /// <summary>Marker echoing the kind, so the row does not rely on colour alone.</summary>
    public string KindGlyph => Model.Kind switch
    {
        SymbolChangeKind.Added => "+",
        SymbolChangeKind.Removed => "−",
        _ => "~",
    };

    public string KindClass => Model.Kind switch
    {
        SymbolChangeKind.Added => "added",
        SymbolChangeKind.Removed => "removed",
        _ => "modified",
    };

    public string CountLabel => (Model.Added, Model.Removed) switch
    {
        (0, var r) => $"−{r}",
        (var a, 0) => $"+{a}",
        var (a, r) => $"+{a} −{r}",
    };

    /// <summary>What the change did, in words — see ChangeDescriber for the guarantees.</summary>
    public string Description => Model.Description;

    /// <summary>Only worth saying when more than one edit was folded into this row.</summary>
    public string HunkLabel => Model.HunkCount > 1 ? $"{Model.HunkCount} edits" : string.Empty;

    public bool HasHunkLabel => Model.HunkCount > 1;

    public int RenderedLineNumber => Model.RenderedLineNumber;
}
