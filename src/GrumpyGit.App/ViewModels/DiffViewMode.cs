namespace GrumpyGit.App.ViewModels;

/// <summary>
/// How the diff is presented. These are alternative readings of the same
/// <see cref="GrumpyGit.Core.Models.ParsedDiff"/>, not different diffs — switching modes
/// never re-runs git.
/// </summary>
public enum DiffViewMode
{
    /// <summary>Old and new in two synchronised panes.</summary>
    SideBySide,

    /// <summary>
    /// One column: the file as it now stands, with removed lines left in place as
    /// dimmed, struck-through ghosts above their replacements.
    /// </summary>
    Ghost,

    /// <summary>
    /// One section per hunk, each headed by the model's reading of it — the diff arranged
    /// the way a notebook arranges prose and cells.
    ///
    /// The only mode that is not a rendering of the diff alone: with no model, or before
    /// the review lands, it is the same hunks with nothing above them. That is deliberate.
    /// A mode that vanished when the model was absent would move under the user, and the
    /// sectioned layout is worth something on its own.
    /// </summary>
    Notebook,
}
