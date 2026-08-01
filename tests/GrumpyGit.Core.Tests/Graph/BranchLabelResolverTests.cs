using FluentAssertions;
using GrumpyGit.Core.Graph;

namespace GrumpyGit.Core.Tests.Graph;

public class BranchLabelResolverTests
{
    // ── Ref decorations ───────────────────────────────────────────────────────

    [Fact]
    public void HeadArrow_ResolvesToTheCheckedOutBranch()
    {
        BranchLabelResolver.FromRefNames(["HEAD -> master", "origin/master"])
            .Should().Be("master");
    }

    [Fact]
    public void LocalBranch_WinsOverRemoteTracking()
    {
        // The user thinks in terms of the local branch they are on.
        BranchLabelResolver.FromRefNames(["origin/feature", "feature"])
            .Should().Be("feature");
    }

    [Fact]
    public void RemoteOnly_IsUsedWhenNoLocalBranchExists()
    {
        BranchLabelResolver.FromRefNames(["origin/release/2.0"])
            .Should().Be("origin/release/2.0");
    }

    [Fact]
    public void Tags_AreNeverTreatedAsBranches()
    {
        // A tag marks a point in history, not a line of development.
        BranchLabelResolver.FromRefNames(["tag: v1.2.0"]).Should().BeNull();
    }

    [Fact]
    public void TagAlongsideBranch_StillYieldsTheBranch()
    {
        BranchLabelResolver.FromRefNames(["tag: v1.2.0", "develop"])
            .Should().Be("develop");
    }

    [Fact]
    public void DetachedHead_YieldsNothing()
    {
        BranchLabelResolver.FromRefNames(["HEAD"]).Should().BeNull();
    }

    [Fact]
    public void NoDecorations_YieldNothing()
    {
        BranchLabelResolver.FromRefNames([]).Should().BeNull();
        BranchLabelResolver.FromRefNames(null).Should().BeNull();
    }

    // ── Merge subjects (recovering deleted branches) ──────────────────────────

    [Fact]
    public void MergeBranchSubject_RecoversTheBranchName()
    {
        BranchLabelResolver.FromMergeSubject("Merge branch 'feature/login'")
            .Should().Be("feature/login");
    }

    [Fact]
    public void MergeBranchIntoSubject_RecoversTheSourceBranch()
    {
        BranchLabelResolver.FromMergeSubject("Merge branch 'hotfix' into develop")
            .Should().Be("hotfix");
    }

    [Fact]
    public void MergeRemoteTrackingSubject_IsRecognised()
    {
        BranchLabelResolver.FromMergeSubject("Merge remote-tracking branch 'origin/main'")
            .Should().Be("origin/main");
    }

    [Fact]
    public void MergePullRequestSubject_StripsTheOwnerPrefix()
    {
        BranchLabelResolver.FromMergeSubject("Merge pull request #42 from acme/feature-x")
            .Should().Be("feature-x");
    }

    [Fact]
    public void OrdinaryCommitSubject_IsNotMistakenForAMerge()
    {
        BranchLabelResolver.FromMergeSubject("fix: correct the merge behaviour").Should().BeNull();
        BranchLabelResolver.FromMergeSubject("").Should().BeNull();
        BranchLabelResolver.FromMergeSubject(null).Should().BeNull();
    }
}
