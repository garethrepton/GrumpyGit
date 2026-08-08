using FluentAssertions;
using GrumpyGit.Core.Agents;
using GrumpyGit.Core.LocalModel;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests.LocalModel;

/// <summary>
/// The review pipeline, exercised against a fake model. Nothing here loads weights: the
/// behaviour worth testing is the queueing, cancellation and caching around inference,
/// not the inference itself.
/// </summary>
public class DiffReviewServiceTests
{
    [Fact]
    public async Task AModelThatWillNotLoadReportsWhyRatherThanGoingQuiet()
    {
        // The failure this covers: a user downloads a model too big for the machine, and
        // gets an empty review panel with no way to tell that from "no model configured".
        var model = new FakeModel
        {
            LoadSucceeds = false,
            LoadError = "This model needs about 45.1 GB of memory and this machine has 31.1 GB.",
        };

        var review = await new DiffReviewService(model).ReviewAsync("a.cs", DiffWith("var x = 1;"), null);

        review.State.Should().Be(DiffReviewState.Failed);
        review.Text.Should().Contain("31.1 GB");
    }

    [Fact]
    public async Task NoModelConfiguredStillHidesThePanel()
    {
        // The other side of it: nothing went wrong, so nothing should be said.
        var review = await new DiffReviewService(new FakeModel { LoadSucceeds = false })
            .ReviewAsync("a.cs", DiffWith("var x = 1;"), null);

        review.State.Should().Be(DiffReviewState.Unavailable);
        review.Text.Should().BeEmpty();
    }

    // ── Fake ──────────────────────────────────────────────────────────────────

    private sealed class FakeModel : IReviewAgent
    {
        public ReviewModuleId Module => ReviewModuleId.Local;
        public bool IsConfigured => true;
        public bool IsReady { get; set; } = true;
        public bool LoadSucceeds { get; set; } = true;
        public string? LoadError { get; set; }
        public int Completions;
        public int Concurrent;
        public int MaxConcurrent;
        // The reply format the parser expects. A fake that answered in free prose would
        // exercise the failure path on every test rather than the one that tests it.
        public string Answer =
            "SUMMARY: Widens the bounds check.\nRISK: caution\nHUNK 1: relaxes the guard";

        public const string ExpectedSummary = "Widens the bounds check.";
        public TimeSpan Delay = TimeSpan.Zero;
        public Exception? Throw;
        public ModelPrompt? LastPrompt;

        public Task<bool> EnsureLoadedAsync(CancellationToken ct = default) => Task.FromResult(LoadSucceeds);

        public async Task<string> CompleteAsync(
            ModelPrompt prompt, ReviewOptions options, IProgress<string>? partial = null,
            CancellationToken ct = default)
        {
            var now = Interlocked.Increment(ref Concurrent);
            InterlockedMax(ref MaxConcurrent, now);
            try
            {
                Interlocked.Increment(ref Completions);
                LastPrompt = prompt;

                if (Delay > TimeSpan.Zero)
                    await Task.Delay(Delay, ct);
                ct.ThrowIfCancellationRequested();

                if (Throw is not null)
                    throw Throw;

                partial?.Report(Answer);
                return Answer;
            }
            finally
            {
                Interlocked.Decrement(ref Concurrent);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value)
                Interlocked.CompareExchange(ref target, value, seen);
        }
    }

    private static ParsedDiff DiffWith(params string[] addedLines)
    {
        var hunk = new DiffHunk
        {
            Index = 0,
            HeaderLine = "@@ -1,3 +1,4 @@ private void Guard()",
            Lines = addedLines
                .Select(l => new DiffLine { Type = DiffLineType.Added, Content = l })
                .ToList(),
        };

        return new ParsedDiff("old", "new", [], [], [], hunks: [hunk]);
    }

    // ── Availability ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AnUnloadableModel_LeavesTheFeatureOffRatherThanFailing()
    {
        var model = new FakeModel { LoadSucceeds = false };
        var service = new DiffReviewService(model);

        var review = await service.ReviewAsync("a.cs", DiffWith("x"));

        review.State.Should().Be(DiffReviewState.Unavailable);
        model.Completions.Should().Be(0);
    }

    [Fact]
    public async Task ADiffWithNoHunks_IsNotWorthAsking()
    {
        var model = new FakeModel();
        var service = new DiffReviewService(model);

        var review = await service.ReviewAsync("a.cs", new ParsedDiff("", "", [], [], []));

        review.State.Should().Be(DiffReviewState.Unavailable);
        model.Completions.Should().Be(0);
    }

    [Fact]
    public async Task AWholesaleRewriteIsDeclinedRatherThanReviewed()
    {
        var model = new FakeModel();
        var service = new DiffReviewService(model);
        var huge = DiffWith(Enumerable.Range(0, DiffReviewService.MaxChangedLines + 1)
            .Select(i => $"line {i}").ToArray());

        var review = await service.ReviewAsync("big.cs", huge);

        review.State.Should().Be(DiffReviewState.TooLarge);
        model.Completions.Should().Be(0, "the point is not to spend a minute of CPU on it");
    }

    [Fact]
    public async Task ADiffJustUnderTheLimitIsStillReviewed()
    {
        var model = new FakeModel();
        var service = new DiffReviewService(model);
        var big = DiffWith(Enumerable.Range(0, DiffReviewService.MaxChangedLines)
            .Select(i => $"line {i}").ToArray());

        var review = await service.ReviewAsync("big.cs", big);

        review.State.Should().Be(DiffReviewState.Complete);
    }

    // ── Caching ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheSameDiffTwice_AsksTheModelOnce()
    {
        var model = new FakeModel();
        var service = new DiffReviewService(model);
        var diff = DiffWith("var x = 1;");

        var first = await service.ReviewAsync("a.cs", diff);
        var second = await service.ReviewAsync("a.cs", diff);

        first.Result.Summary.Should().Be(FakeModel.ExpectedSummary);
        second.Result.Summary.Should().Be(FakeModel.ExpectedSummary);
        model.Completions.Should().Be(1);
    }

    [Fact]
    public async Task TheSystemInstructionReachesTheModel()
    {
        var model = new FakeModel();
        var service = new DiffReviewService(model);

        await service.ReviewAsync("a.cs", DiffWith("var x = 1;"));

        model.LastPrompt!.System.Should().NotBeEmpty();
        model.LastPrompt.User.Should().Contain("a.cs");
    }

    [Fact]
    public async Task ADifferentFileWithTheSameContent_IsADifferentReview()
    {
        var model = new FakeModel();
        var service = new DiffReviewService(model);

        await service.ReviewAsync("a.cs", DiffWith("var x = 1;"));
        await service.ReviewAsync("b.cs", DiffWith("var x = 1;"));

        model.Completions.Should().Be(2);
    }

    [Fact]
    public async Task ACachedReview_IsAvailableWithoutAsking()
    {
        var model = new FakeModel();
        var service = new DiffReviewService(model);
        var diff = DiffWith("var x = 1;");

        service.TryGetCached("a.cs", diff).Should().BeNull();
        await service.ReviewAsync("a.cs", diff);

        service.TryGetCached("a.cs", diff)!.Summary.Should().Be(FakeModel.ExpectedSummary);
    }

    [Fact]
    public async Task TheCacheStopsGrowing()
    {
        var model = new FakeModel();
        var service = new DiffReviewService(model, cacheCapacity: 2);

        var first = DiffWith("one");
        await service.ReviewAsync("a.cs", first);
        await service.ReviewAsync("b.cs", DiffWith("two"));
        await service.ReviewAsync("c.cs", DiffWith("three"));

        service.TryGetCached("a.cs", first).Should().BeNull("the oldest entry is evicted");
        service.TryGetCached("c.cs", DiffWith("three")).Should().NotBeNull();
    }

    // ── Serialisation and cancellation ────────────────────────────────────────

    [Fact]
    public async Task ConcurrentRequests_ReachTheModelOneAtATime()
    {
        var model = new FakeModel { Delay = TimeSpan.FromMilliseconds(40) };
        var service = new DiffReviewService(model);

        await Task.WhenAll(
            service.ReviewAsync("a.cs", DiffWith("one")),
            service.ReviewAsync("b.cs", DiffWith("two")),
            service.ReviewAsync("c.cs", DiffWith("three")));

        model.Completions.Should().Be(3);
        model.MaxConcurrent.Should().Be(1, "a model context is not re-entrant");
    }

    [Fact]
    public async Task ARequestCancelledWhileQueued_NeverReachesTheModel()
    {
        var model = new FakeModel { Delay = TimeSpan.FromMilliseconds(300) };
        var service = new DiffReviewService(model);

        var holding = service.ReviewAsync("a.cs", DiffWith("one"));

        using var cts = new CancellationTokenSource();
        var queued = service.ReviewAsync("b.cs", DiffWith("two"), ct: cts.Token);
        cts.Cancel();

        (await queued).State.Should().Be(DiffReviewState.Cancelled);
        (await holding).State.Should().Be(DiffReviewState.Complete);
        model.Completions.Should().Be(1);
    }

    [Fact]
    public async Task ARequestCancelledMidGeneration_ReportsCancelledNotFailed()
    {
        var model = new FakeModel { Delay = TimeSpan.FromSeconds(5) };
        var service = new DiffReviewService(model);

        using var cts = new CancellationTokenSource();
        var running = service.ReviewAsync("a.cs", DiffWith("one"), ct: cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        (await running).State.Should().Be(DiffReviewState.Cancelled);
    }

    [Fact]
    public async Task ACancelledReview_IsNotCached()
    {
        var model = new FakeModel { Delay = TimeSpan.FromSeconds(5) };
        var service = new DiffReviewService(model);
        var diff = DiffWith("one");

        using var cts = new CancellationTokenSource();
        var running = service.ReviewAsync("a.cs", diff, ct: cts.Token);
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));
        await running;

        service.TryGetCached("a.cs", diff).Should().BeNull();
    }

    // ── Failure ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AModelThatThrows_FailsWithoutTakingTheAppWithIt()
    {
        var model = new FakeModel { Throw = new InvalidOperationException("out of memory") };
        var service = new DiffReviewService(model);

        var review = await service.ReviewAsync("a.cs", DiffWith("one"));

        review.State.Should().Be(DiffReviewState.Failed);
        review.Text.Should().Be("out of memory");
    }

    [Fact]
    public async Task AFailedReview_IsNotCached()
    {
        var model = new FakeModel { Throw = new InvalidOperationException("boom") };
        var service = new DiffReviewService(model);
        var diff = DiffWith("one");

        await service.ReviewAsync("a.cs", diff);
        model.Throw = null;
        var second = await service.ReviewAsync("a.cs", diff);

        second.State.Should().Be(DiffReviewState.Complete);
        model.Completions.Should().Be(2);
    }

    [Fact]
    public async Task AnEmptyAnswer_CountsAsAFailure()
    {
        var model = new FakeModel { Answer = "   " };
        var service = new DiffReviewService(model);

        var review = await service.ReviewAsync("a.cs", DiffWith("one"));

        review.State.Should().Be(DiffReviewState.Failed);
    }

    [Fact]
    public async Task AReplyThatIgnoresTheFormat_CountsAsAFailure()
    {
        // Free prose parses to nothing. Showing an empty panel would read as "this diff is
        // unremarkable" rather than "the model did not answer the question".
        var model = new FakeModel { Answer = "Sure! Here is my analysis of your code..." };
        var service = new DiffReviewService(model);

        var review = await service.ReviewAsync("a.cs", DiffWith("one"));

        review.State.Should().Be(DiffReviewState.Failed);
        service.TryGetCached("a.cs", DiffWith("one")).Should().BeNull();
    }

    // ── Progress ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task PartialTextIsReported_SoTheAnswerCanBeShownArriving()
    {
        var model = new FakeModel();
        var service = new DiffReviewService(model);
        var seen = new List<string>();

        await service.ReviewAsync("a.cs", DiffWith("one"), partial: new Progress<string>(seen.Add));

        // Progress<T> posts asynchronously; the review itself is the assertion that
        // matters, so this only checks the channel is wired, not the timing.
        await Task.Delay(50);
        seen.Should().ContainSingle().Which.Should().Be(model.Answer);
    }
}
