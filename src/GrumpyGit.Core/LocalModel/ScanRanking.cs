namespace GrumpyGit.Core.LocalModel;

/// <summary>
/// How a scanned changeset is ordered: worst first, not alphabetically and not by size.
///
/// A pure function, kept out of the viewmodel because it is the whole editorial claim the
/// AI view makes — "these are the diffs that matter" — and that claim should be assertable
/// in a test rather than inferred from a <c>ThenByDescending</c> chain in the middle of a
/// list-building method.
///
/// Deliberately not "ask the model to rank them". The model already gave a verdict and a
/// list of issues per file; turning those into an order is arithmetic, and spending an
/// inference on arithmetic would be slower, less predictable, and no better.
/// </summary>
public static class ScanRanking
{
    /// <summary>Churn stops discriminating long before this; the cap keeps it from drowning risk.</summary>
    private const int ChurnCeiling = 9_999;

    private const int IssueCeiling = 99;

    public static int RiskWeight(ReviewRisk risk) => risk switch
    {
        ReviewRisk.Danger => 2,
        ReviewRisk.Caution => 1,
        _ => 0,
    };

    /// <summary>
    /// Higher sorts first. The tiers never blend: any dangerous file outranks every cautious
    /// one, and any file carrying an issue outranks every file that carries none, however
    /// large. Churn only breaks ties among files the model had nothing to say about — it is
    /// a proxy for "worth a glance", not for risk.
    /// </summary>
    public static long Score(ReviewRisk risk, int issueCount, int added, int removed) =>
        RiskWeight(risk) * 1_000_000L
        + Math.Clamp(issueCount, 0, IssueCeiling) * 10_000L
        + Math.Clamp(added + removed, 0, ChurnCeiling);

    /// <summary>
    /// Whether the row earns its description in the list rather than a single line. The
    /// point of the view is that most files are noise: a rename, a using directive, a
    /// version bump. Those collapse.
    /// </summary>
    public static bool IsNotable(ReviewRisk risk, int issueCount) =>
        risk != ReviewRisk.None || issueCount > 0;
}
