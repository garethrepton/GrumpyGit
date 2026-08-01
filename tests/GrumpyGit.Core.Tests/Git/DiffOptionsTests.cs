using CliWrap.Builders;
using FluentAssertions;
using GrumpyGit.Core.Git;

namespace GrumpyGit.Core.Tests.Git;

public class DiffOptionsTests
{
    private static string Render(DiffOptions options)
    {
        var builder = new ArgumentsBuilder();
        options.Apply(builder);
        return builder.Build();
    }

    [Fact]
    public void Default_RequestsThreeLinesOfContext()
    {
        Render(DiffOptions.Default).Should().Contain("-U3");
    }

    [Fact]
    public void Default_AddsNoWhitespaceFlags()
    {
        var rendered = Render(DiffOptions.Default);

        rendered.Should().NotContain("-w");
        rendered.Should().NotContain("--ignore-blank-lines");
    }

    [Fact]
    public void IgnoreWhitespace_EmitsTheFlag()
    {
        Render(new DiffOptions { IgnoreWhitespace = true }).Should().Contain("-w");
    }

    [Fact]
    public void IgnoreBlankLines_EmitsTheFlag()
    {
        Render(new DiffOptions { IgnoreBlankLines = true }).Should().Contain("--ignore-blank-lines");
    }

    [Fact]
    public void FullFileContext_RequestsAContextLargerThanAnyRealFile()
    {
        var options = new DiffOptions { ContextLines = DiffOptions.FullFileContext };

        options.IsFullFile.Should().BeTrue();
        Render(options).Should().Contain($"-U{DiffOptions.FullFileContext}");
    }

    [Fact]
    public void PatchStaging_IsAllowedForPlainAndFullFileDiffs()
    {
        // Both are just different -U values, so the patch still applies.
        DiffOptions.Default.SupportsPatchStaging.Should().BeTrue();
        new DiffOptions { ContextLines = DiffOptions.FullFileContext }
            .SupportsPatchStaging.Should().BeTrue();
    }

    [Fact]
    public void PatchStaging_IsBlockedWhenWhitespaceIsIgnored()
    {
        // A -w patch omits real differences, so applying it would fail or corrupt
        // the index. Staging must be disabled rather than attempted.
        new DiffOptions { IgnoreWhitespace = true }.SupportsPatchStaging.Should().BeFalse();
        new DiffOptions { IgnoreBlankLines = true }.SupportsPatchStaging.Should().BeFalse();
    }

    [Fact]
    public void ZeroContext_IsRepresentable()
    {
        Render(new DiffOptions { ContextLines = 0 }).Should().Contain("-U0");
    }
}
