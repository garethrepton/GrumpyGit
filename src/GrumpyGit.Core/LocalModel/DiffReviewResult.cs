namespace GrumpyGit.Core.LocalModel;

/// <summary>How much care this change wants, as the model reads it.</summary>
public enum ReviewRisk
{
    /// <summary>Nothing stood out.</summary>
    None,

    /// <summary>Worth a second look — behaviour changed in a way a reader could miss.</summary>
    Caution,

    /// <summary>Something here can bite: data loss, a dropped guard, a widened permission.</summary>
    Danger,
}

/// <summary>
/// One problem the model believes it found.
/// </summary>
/// <param name="SourceLine">
/// Line number in the new version of the file, as the model reported it. Kept even when it
/// cannot be mapped, so the text still reads sensibly.
/// </param>
/// <param name="RenderedLine">
/// The same place in the rendered diff document, or 0 when the reported line is not one the
/// diff actually shows — a small model will occasionally cite a line that is not in front
/// of it, and an issue anchored nowhere is better than one anchored wrongly.
/// </param>
public sealed record ReviewIssue(int SourceLine, int RenderedLine, string Text)
{
    public bool IsAnchored => RenderedLine > 0;
}

/// <summary>A single line about one hunk, drawn as a callout above it.</summary>
/// <param name="ChangeNumber">1-based, matching the numbering the prompt gave the model.</param>
/// <param name="RenderedLine">Line of that hunk's <c>@@</c> header in the rendered diff.</param>
public sealed record ChangeNote(int ChangeNumber, int RenderedLine, string Text);

/// <summary>
/// What a change reaches outside itself.
///
/// Deliberately the categories this repository's own commandments are written around —
/// the filesystem, the network, launching a process, anything credential-adjacent, and
/// anything that can lose data. A reviewer skimming twenty sections wants to stop at those
/// five and can skim the rest.
/// </summary>
public enum ConcernKind
{
    None,
    Files,
    Network,
    Process,
    Credentials,
    DataLoss,
}

/// <summary>
/// The model's claim that one change touches something worth stopping at.
///
/// Distinct from <see cref="ReviewIssue"/> on purpose. An issue says the code is wrong; a
/// concern says the code is <em>consequential</em>, which is a different question and often
/// true of code that is perfectly correct. Merging them would make a section flagged for
/// writing a file look like a section flagged for a bug, and a reader who learns that the
/// flags are half false stops reading all of them.
/// </summary>
public sealed record ChangeConcern(int ChangeNumber, ConcernKind Kind, string Text)
{
    /// <summary>Short, upper-case, for a badge beside the section heading.</summary>
    public string Label => Kind switch
    {
        ConcernKind.Files => "FILES",
        ConcernKind.Network => "NETWORK",
        ConcernKind.Process => "PROCESS",
        ConcernKind.Credentials => "CREDENTIALS",
        ConcernKind.DataLoss => "DATA LOSS",
        _ => string.Empty,
    };

    /// <summary>
    /// The two that are never routine here. The rest are worth a glance; these are worth
    /// stopping for, and the badge is coloured accordingly.
    /// </summary>
    public bool IsSevere => Kind is ConcernKind.Credentials or ConcernKind.DataLoss;
}

/// <summary>
/// The model's reading of one file's diff: what it does, whether to be careful, what looks
/// wrong, and a line for each hunk.
///
/// All of it comes from a single completion. Asking per hunk would mean N+1 inferences for
/// a file with N hunks, which on a CPU-bound small model is the difference between a
/// review that arrives while you read and one that arrives after you have moved on.
/// </summary>
public sealed record DiffReviewResult(
    string Summary,
    ReviewRisk Risk,
    IReadOnlyList<ReviewIssue> Issues,
    IReadOnlyList<ChangeNote> ChangeNotes,
    IReadOnlyList<ChangeConcern> Concerns)
{
    public static readonly DiffReviewResult Empty =
        new(string.Empty, ReviewRisk.None, [], [], []);

    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);

    public bool HasIssues => Issues.Count > 0;
}
