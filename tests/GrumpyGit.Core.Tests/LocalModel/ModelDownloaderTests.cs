using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using GrumpyGit.Core.LocalModel;

namespace GrumpyGit.Core.Tests.LocalModel;

/// <summary>
/// The downloader, driven by a stubbed handler — no test here touches the network.
///
/// What is worth asserting is not that bytes arrive but what happens when they are the
/// wrong bytes: this is the one place the application accepts a file from outside the
/// machine and then hands it to native code.
/// </summary>
public class ModelDownloaderTests : IDisposable
{
    private readonly string _dir;

    public ModelDownloaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"grumpygit-dl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private sealed class StubHandler(byte[] payload, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(payload),
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>Answers each URL with its own bytes, for the sharded case.</summary>
    private sealed class MapHandler(Dictionary<string, byte[]> byUrl) : HttpMessageHandler
    {
        public readonly List<string> Requested = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(byUrl[url]),
            });
        }
    }

    private static string Sha256Of(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static ModelOption OptionFor(byte[] payload, string? sha = null) =>
        ModelOption.Single("Test model", "https://example.invalid/model.gguf", "model.gguf",
            payload.Length, sha ?? Sha256Of(payload), "a test");

    private ModelDownloader DownloaderFor(byte[] payload, HttpStatusCode status = HttpStatusCode.OK) =>
        new(new HttpClient(new StubHandler(payload, status)));

    [Fact]
    public async Task AVerifiedDownloadLandsInPlace()
    {
        var payload = "pretend weights"u8.ToArray();
        var option = OptionFor(payload);

        var path = await DownloaderFor(payload).DownloadAsync(option, _dir);

        path.Should().Be(Path.Combine(_dir, "model.gguf"));
        File.ReadAllBytes(path).Should().Equal(payload);
    }

    [Fact]
    public async Task AChecksumMismatchIsRejectedAndNothingIsKept()
    {
        var payload = "pretend weights"u8.ToArray();

        // The published hash of something else entirely — a substituted or truncated file.
        var option = OptionFor(payload, Sha256Of("different bytes"u8.ToArray()));

        var act = () => DownloaderFor(payload).DownloadAsync(option, _dir);

        await act.Should().ThrowAsync<InvalidOperationException>();
        Directory.GetFiles(_dir).Should().BeEmpty("neither the file nor its .part may survive");
    }

    [Fact]
    public async Task AFailedRequestLeavesNothingBehind()
    {
        var payload = "irrelevant"u8.ToArray();
        var option = OptionFor(payload);

        var act = () => DownloaderFor(payload, HttpStatusCode.NotFound).DownloadAsync(option, _dir);

        await act.Should().ThrowAsync<HttpRequestException>();
        Directory.GetFiles(_dir).Should().BeEmpty();
    }

    [Fact]
    public async Task AnAlreadyPresentModelIsNotFetchedAgain()
    {
        var payload = "pretend weights"u8.ToArray();
        var option = OptionFor(payload);
        var handler = new StubHandler(payload);
        var downloader = new ModelDownloader(new HttpClient(handler));

        await File.WriteAllBytesAsync(Path.Combine(_dir, "model.gguf"), payload);
        await downloader.DownloadAsync(option, _dir);

        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task ProgressIsReportedAgainstThePublishedTotal()
    {
        var payload = new byte[4096];
        Random.Shared.NextBytes(payload);
        var option = OptionFor(payload);
        var seen = new List<DownloadProgress>();

        await DownloaderFor(payload).DownloadAsync(option, _dir, new Progress<DownloadProgress>(seen.Add));

        await Task.Delay(50);
        seen.Should().NotBeEmpty();
        seen[^1].BytesReceived.Should().Be(payload.Length);
        seen[^1].Fraction.Should().Be(1);
    }

    [Fact]
    public async Task CancellationLeavesNoPartialFile()
    {
        var payload = new byte[1 << 20];
        var option = OptionFor(payload);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => DownloaderFor(payload).DownloadAsync(option, _dir, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        Directory.GetFiles(_dir).Should().BeEmpty();
    }

    [Fact]
    public async Task AShardedModelFetchesEveryPartAndReturnsTheFirst()
    {
        var parts = Enumerable.Range(1, 3)
            .Select(i => (Url: $"https://example.invalid/shard{i}.gguf",
                          Name: $"shard{i}.gguf",
                          Bytes: System.Text.Encoding.UTF8.GetBytes($"part {i}")))
            .ToList();

        var option = new ModelOption(
            "Sharded",
            parts.Select(p => new ModelPart(p.Url, p.Name, p.Bytes.Length, Sha256Of(p.Bytes))).ToList(),
            "a test");

        var handler = new MapHandler(parts.ToDictionary(p => p.Url, p => p.Bytes));

        var path = await new ModelDownloader(new HttpClient(handler)).DownloadAsync(option, _dir);

        path.Should().Be(Path.Combine(_dir, "shard1.gguf"), "llama.cpp is given the first shard");
        handler.Requested.Should().HaveCount(3);
        Directory.GetFiles(_dir).Should().HaveCount(3);
    }

    [Fact]
    public async Task AShardedDownloadKeepsThePartsItAlreadyVerified()
    {
        // The point of the resume: a forty-gigabyte model does not start again from zero
        // because the last shard failed.
        var good = "part 1"u8.ToArray();
        var bad = "part 2"u8.ToArray();

        var option = new ModelOption(
            "Sharded",
            [
                new ModelPart("https://example.invalid/a.gguf", "a.gguf", good.Length, Sha256Of(good)),
                new ModelPart("https://example.invalid/b.gguf", "b.gguf", bad.Length, Sha256Of("something else"u8.ToArray())),
            ],
            "a test");

        var handler = new MapHandler(new Dictionary<string, byte[]>
        {
            ["https://example.invalid/a.gguf"] = good,
            ["https://example.invalid/b.gguf"] = bad,
        });

        var act = () => new ModelDownloader(new HttpClient(handler)).DownloadAsync(option, _dir);

        await act.Should().ThrowAsync<InvalidOperationException>();
        Directory.GetFiles(_dir).Should().ContainSingle().Which.Should().EndWith("a.gguf");
    }

    [Fact]
    public void TheCatalogueOnlyPointsAtOneHost()
    {
        // The outbound surface is meant to be exactly this. A future entry pointing
        // somewhere else is a decision to be made deliberately, not one that slips in.
        ModelCatalogue.All.SelectMany(m => m.Parts)
            .Should().OnlyContain(p => p.Url.StartsWith("https://huggingface.co/"));
    }

    [Fact]
    public void EveryCataloguePartCarriesAChecksum()
    {
        ModelCatalogue.All.SelectMany(m => m.Parts)
            .Should().OnlyContain(p => p.Sha256.Length == 64 && p.SizeBytes > 0);
    }

    [Fact]
    public void EveryPartsUrlEndsWithTheNameItIsSavedAs()
    {
        // The file on disk has to be the file the hash was published for. A mismatch here
        // would verify one download and load another.
        ModelCatalogue.All.SelectMany(m => m.Parts)
            .Should().OnlyContain(p => p.Url.EndsWith("/" + p.FileName));
    }

    [Fact]
    public void TheCatalogueReadsAsALadder()
    {
        // Smallest first. The list is a choice about how much machine to spend, so it
        // should be ordered by that rather than by when an entry was added.
        ModelCatalogue.All.Select(m => m.SizeBytes).Should().BeInAscendingOrder();
    }

    [Fact]
    public void NoTwoPartsShareAFileName()
    {
        // They all land in one directory, so a collision would have one model silently
        // standing in for another — and the "already present" check would hide it.
        ModelCatalogue.All.SelectMany(m => m.Parts).Select(p => p.FileName)
            .Should().OnlyHaveUniqueItems();
    }
}
