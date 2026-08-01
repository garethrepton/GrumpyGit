namespace GrumpyGit.Core.Ai;

/// <summary>How much scrutiny a file in an AI session probably deserves.</summary>
public enum ReviewRisk
{
    Low = 0,
    Medium = 1,
    High = 2,
}

/// <param name="Risk">The scrutiny hint.</param>
/// <param name="Reason">Plain-English justification, shown to the user on hover.</param>
public readonly record struct RiskAssessment(ReviewRisk Risk, string Reason);

/// <summary>
/// Ranks the files an agent changed by how much human attention they warrant.
///
/// This orders the reviewer's attention; it never certifies anything. A "low" result
/// means "nothing here raised a flag", NOT "this change is safe" — the whole point of
/// reviewing agent output is that correctness cannot be inferred from file location or
/// diff size. Everything here is a positional/size heuristic, deliberately so: it is
/// cheap, explainable, and never silently wrong in a way that hides a real problem,
/// because every file is still listed and still has to be ticked off.
/// </summary>
public static class ReviewRiskAssessor
{
    /// <summary>Paths whose contents govern what runs automatically on every push.</summary>
    private static readonly string[] CiPathMarkers =
    [
        ".github/workflows", ".gitlab-ci", "azure-pipelines", "jenkinsfile", ".circleci",
    ];

    /// <summary>Files that decide what code gets pulled in, or how the app is configured.</summary>
    private static readonly string[] SensitiveFileNames =
    [
        ".env", "dockerfile", "web.config", "app.config", "appsettings.json",
        "package.json", "packages.config", "requirements.txt", "go.mod", "pom.xml",
        "gemfile", "cargo.toml", ".npmrc",
    ];

    /// <summary>Path fragments suggesting security- or data-sensitive territory.</summary>
    private static readonly string[] SensitivePathMarkers =
    [
        "auth", "login", "password", "credential", "secret", "token", "crypto",
        "security", "permission", "session", "payment", "billing", "migration", "admin",
    ];

    private const int LargeChurnThreshold = 300;
    private const int ModerateChurnThreshold = 60;

    /// <param name="filePath">Repo-relative path, either slash style.</param>
    /// <param name="changeType">Status word, e.g. "Added", "Deleted", "Modified".</param>
    /// <param name="linesAdded">Added line count from --numstat.</param>
    /// <param name="linesRemoved">Removed line count from --numstat.</param>
    public static RiskAssessment Assess(
        string filePath, string changeType, int linesAdded, int linesRemoved)
    {
        var normalised = (filePath ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
        var fileName = NameOf(normalised);
        var churn = linesAdded + linesRemoved;

        foreach (var marker in CiPathMarkers)
        {
            if (normalised.Contains(marker, StringComparison.Ordinal))
                return new RiskAssessment(ReviewRisk.High,
                    "Changes CI configuration — controls what runs on every push.");
        }

        foreach (var f in SensitiveFileNames)
        {
            if (fileName == f || fileName.EndsWith(f, StringComparison.Ordinal))
                return new RiskAssessment(ReviewRisk.High,
                    "Configuration or dependency manifest — affects what code is pulled in and how the app is configured.");
        }

        foreach (var marker in SensitivePathMarkers)
        {
            if (normalised.Contains(marker, StringComparison.Ordinal))
                return new RiskAssessment(ReviewRisk.High,
                    $"Path mentions '{marker}' — security- or data-sensitive area.");
        }

        // A deletion is easy to skim past and expensive to undo after merge.
        if (!string.IsNullOrEmpty(changeType) && changeType.StartsWith('D'))
            return new RiskAssessment(ReviewRisk.High,
                "File was deleted — easy to miss, hard to undo after merge.");

        if (churn >= LargeChurnThreshold)
            return new RiskAssessment(ReviewRisk.High,
                $"Large change ({churn} lines) — high chance of unintended edits.");

        if (churn >= ModerateChurnThreshold)
            return new RiskAssessment(ReviewRisk.Medium, $"Moderate change ({churn} lines).");

        return new RiskAssessment(ReviewRisk.Low, "Small change in a non-sensitive path.");
    }

    private static string NameOf(string normalisedPath)
    {
        var idx = normalisedPath.LastIndexOf('/');
        return idx >= 0 ? normalisedPath[(idx + 1)..] : normalisedPath;
    }
}
