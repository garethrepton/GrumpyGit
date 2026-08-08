using System.Security.Cryptography;

namespace GrumpyGit.Core.LocalModel;

/// <summary>Bytes so far against the published total, for a progress bar.</summary>
public sealed record DownloadProgress(long BytesReceived, long TotalBytes)
{
    public double Fraction => TotalBytes <= 0 ? 0 : Math.Clamp((double)BytesReceived / TotalBytes, 0, 1);

    public string Label =>
        $"{BytesReceived / 1024d / 1024d:0} MB of {TotalBytes / 1024d / 1024d:0} MB";
}

/// <summary>
/// Fetches a model file from <see cref="ModelCatalogue"/>.
///
/// <strong>This is the only outbound network call in the application, and the only
/// HttpClient.</strong> Everything else talks to the outside world through
/// <c>git.exe</c>. It exists because the alternative — telling the user to go and find a
/// GGUF themselves — made a good feature unusable; it was added deliberately and on
/// request, not by drift. See <c>Scans/2026-08-07-model-download.html</c>.
///
/// Three rules keep it honest:
/// <list type="bullet">
///   <item>It only ever fetches a URL from the catalogue. No user input reaches it.</item>
///   <item>It runs only when the user presses the button. Nothing here is automatic.</item>
///   <item>The result is verified against a published SHA-256 before it is accepted.</item>
/// </list>
/// </summary>
public sealed class ModelDownloader
{
    private readonly HttpClient _http;

    /// <param name="http">
    /// Injected so tests can drive this without a network. The default is a plain client
    /// with no cookies, no credentials and no default headers to leak.
    /// </param>
    public ModelDownloader(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// Downloads every part of <paramref name="option"/> into <paramref name="directory"/>
    /// and returns the path llama.cpp should be pointed at — the first part.
    ///
    /// Each part is written to a <c>.part</c> file and moved into place only after its hash
    /// matches, so an interrupted or corrupted download can never be picked up as a usable
    /// model on the next start. Parts already present are left alone, which is what makes a
    /// cancelled multi-file download resumable at the granularity that matters: a
    /// forty-gigabyte model does not start again from zero because the fourth shard failed.
    /// </summary>
    public async Task<string> DownloadAsync(
        ModelOption option,
        string directory,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(option);
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Directory must not be empty.", nameof(directory));

        Directory.CreateDirectory(directory);

        var total = option.SizeBytes;
        long completed = 0;

        foreach (var part in option.Parts)
        {
            var finalPath = Path.Combine(directory, part.FileName);

            if (File.Exists(finalPath))
            {
                completed += part.SizeBytes;
                progress?.Report(new DownloadProgress(completed, total));
                continue;
            }

            await DownloadPartAsync(part, finalPath, completed, total, progress, ct).ConfigureAwait(false);
            completed += part.SizeBytes;
        }

        return Path.Combine(directory, option.FileName);
    }

    /// <param name="alreadyDone">
    /// Bytes finished before this part, so the progress bar tracks the whole model rather
    /// than restarting at each shard.
    /// </param>
    private async Task DownloadPartAsync(
        ModelPart part,
        string finalPath,
        long alreadyDone,
        long total,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var partPath = finalPath + ".part";

        try
        {
            using var response = await _http
                .GetAsync(part.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = new FileStream(
                             partPath, FileMode.Create, FileAccess.Write, FileShare.None,
                             bufferSize: 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                long received = 0;
                int read;

                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    progress?.Report(new DownloadProgress(alreadyDone + received, total));
                }
            }

            var actual = await Sha256Async(partPath, ct).ConfigureAwait(false);
            if (!actual.Equals(part.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"{part.FileName} does not match its published checksum, so it has not been kept.");

            File.Move(partPath, finalPath, overwrite: true);
        }
        catch
        {
            // A partial or wrong file is never left behind — the next attempt starts clean,
            // and nothing on disk can be mistaken for a verified model.
            TryDelete(partPath);
            throw;
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1 << 16, useAsync: true);

        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort — a stray .part is harmless; it is never loaded.
        }
    }
}
