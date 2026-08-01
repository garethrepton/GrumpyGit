using GrumpyGit.Core.Ai;

namespace GrumpyGit.Core.Tests.Ai;

public class ReviewRiskAssessorTests
{
    private static ReviewRisk Risk(string path, string change = "Modified", int added = 1, int removed = 0) =>
        ReviewRiskAssessor.Assess(path, change, added, removed).Risk;

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".gitlab-ci.yml")]
    [InlineData("Jenkinsfile")]
    [InlineData("azure-pipelines.yml")]
    public void CiConfiguration_IsAlwaysHighRisk_RegardlessOfSize(string path)
    {
        Assert.Equal(ReviewRisk.High, Risk(path));
    }

    [Theory]
    [InlineData("package.json")]
    [InlineData("src/app/appsettings.json")]
    [InlineData("Dockerfile")]
    [InlineData("requirements.txt")]
    [InlineData("Cargo.toml")]
    public void DependencyAndConfigManifests_AreHighRisk(string path)
    {
        Assert.Equal(ReviewRisk.High, Risk(path));
    }

    [Theory]
    [InlineData("src/Auth/TokenService.cs")]
    [InlineData("app/login/handler.py")]
    [InlineData("lib/crypto/hash.go")]
    [InlineData("db/migrations/001_init.sql")]
    public void SecuritySensitivePaths_AreHighRisk(string path)
    {
        Assert.Equal(ReviewRisk.High, Risk(path));
    }

    [Fact]
    public void Deletions_AreHighRisk_BecauseTheyAreEasyToSkimPast()
    {
        Assert.Equal(ReviewRisk.High, Risk("src/Util/Helper.cs", change: "Deleted"));
    }

    [Fact]
    public void VeryLargeChange_IsHighRisk()
    {
        Assert.Equal(ReviewRisk.High, Risk("src/Util/Helper.cs", added: 250, removed: 100));
    }

    [Fact]
    public void ModerateChange_IsMediumRisk()
    {
        Assert.Equal(ReviewRisk.Medium, Risk("src/Util/Helper.cs", added: 50, removed: 20));
    }

    [Fact]
    public void SmallChangeInOrdinaryPath_IsLowRisk()
    {
        Assert.Equal(ReviewRisk.Low, Risk("src/Util/Helper.cs", added: 3, removed: 1));
    }

    [Fact]
    public void WindowsPathSeparators_AreHandled()
    {
        Assert.Equal(ReviewRisk.High, Risk(@"src\Auth\TokenService.cs"));
    }

    [Fact]
    public void PathCasing_DoesNotAffectDetection()
    {
        Assert.Equal(ReviewRisk.High, Risk("SRC/AUTH/Token.cs"));
        Assert.Equal(ReviewRisk.High, Risk("PACKAGE.JSON"));
    }

    [Fact]
    public void EveryAssessment_CarriesAHumanReadableReason()
    {
        var assessment = ReviewRiskAssessor.Assess("src/Auth/Token.cs", "Modified", 5, 2);

        Assert.False(string.IsNullOrWhiteSpace(assessment.Reason));
        Assert.Contains("auth", assessment.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CiConfiguration_OutranksChurnBasedRules()
    {
        // A one-line CI edit must not be demoted to "low" just because it is small.
        var assessment = ReviewRiskAssessor.Assess(".github/workflows/ci.yml", "Modified", 1, 0);

        Assert.Equal(ReviewRisk.High, assessment.Risk);
        Assert.Contains("CI", assessment.Reason);
    }
}
