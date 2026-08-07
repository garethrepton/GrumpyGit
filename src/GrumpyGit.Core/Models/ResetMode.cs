namespace GrumpyGit.Core.Models;

/// <summary>
/// Which of git reset's three modes to use. Named rather than passed as a string so a
/// caller cannot smuggle another flag in through the mode argument.
/// </summary>
public enum ResetMode
{
    /// <summary>Moves the branch only; index and working tree keep the changes staged.</summary>
    Soft,

    /// <summary>Moves the branch and resets the index; working tree keeps the changes.</summary>
    Mixed,

    /// <summary>Moves the branch and discards everything after it. Unrecoverable.</summary>
    Hard,
}
