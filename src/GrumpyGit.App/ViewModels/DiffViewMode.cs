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
}
