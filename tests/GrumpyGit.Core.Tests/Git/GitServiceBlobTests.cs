using FluentAssertions;
using GrumpyGit.Core.Git;

namespace GrumpyGit.Core.Tests.Git;

/// <summary>
/// Blob reads must be byte-exact. Fetching image content through a text-decoding path
/// corrupts it silently — the bytes come back "successfully" but the picture will not
/// decode, so this is verified against real binary content rather than assumed.
/// </summary>
public class GitServiceBlobTests : IDisposable
{
    private readonly GitService _git = new();
    private readonly string _repoPath;

    public GitServiceBlobTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), $"grumpygit-blob-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoPath);
        RunGit("init -b main");
        RunGit("config user.email test@test.com");
        RunGit("config user.name TestUser");
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_repoPath, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_repoPath, true);
        }
        catch { }
    }

    private void RunGit(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git", args)
        {
            WorkingDirectory = _repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        System.Diagnostics.Process.Start(psi)!.WaitForExit(10_000);
    }

    /// <summary>Every byte value 0-255, which is what text decoding would destroy.</summary>
    private static byte[] AllByteValues()
    {
        var bytes = new byte[256];
        for (var i = 0; i < 256; i++) bytes[i] = (byte)i;
        return bytes;
    }

    /// <summary>A minimal but genuinely valid 1x1 PNG.</summary>
    private static byte[] TinyPng() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
        0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
        0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
        0x42, 0x60, 0x82,
    ];

    [Fact]
    public async Task Blob_RoundTripsEveryByteValue_Unchanged()
    {
        var original = AllByteValues();
        File.WriteAllBytes(Path.Combine(_repoPath, "bytes.bin"), original);
        RunGit("add bytes.bin");
        RunGit("commit -q -m add-binary");

        var head = (await _git.GetCommitGraphAsync(_repoPath))[0].Hash;
        var blob = await _git.GetFileBlobAsync(_repoPath, head, "bytes.bin");

        blob.Should().Equal(original, "a text-decoding read would mangle high bytes and NULs");
    }

    [Fact]
    public async Task Blob_PreservesPngSignatureAndLength()
    {
        var png = TinyPng();
        File.WriteAllBytes(Path.Combine(_repoPath, "tiny.png"), png);
        RunGit("add tiny.png");
        RunGit("commit -q -m add-png");

        var head = (await _git.GetCommitGraphAsync(_repoPath))[0].Hash;
        var blob = await _git.GetFileBlobAsync(_repoPath, head, "tiny.png");

        blob.Length.Should().Be(png.Length);
        blob.Take(8).Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
    }

    [Fact]
    public async Task Blob_ReadsThePreviousVersionViaParentRevision()
    {
        File.WriteAllBytes(Path.Combine(_repoPath, "img.png"), TinyPng());
        RunGit("add img.png");
        RunGit("commit -q -m first");

        var changed = TinyPng().Concat(new byte[] { 1, 2, 3 }).ToArray();
        File.WriteAllBytes(Path.Combine(_repoPath, "img.png"), changed);
        RunGit("add img.png");
        RunGit("commit -q -m second");

        var head = (await _git.GetCommitGraphAsync(_repoPath))[0].Hash;

        var after = await _git.GetFileBlobAsync(_repoPath, head, "img.png");
        var before = await _git.GetFileBlobAsync(_repoPath, head + "^", "img.png");

        after.Length.Should().Be(changed.Length);
        before.Length.Should().Be(TinyPng().Length, "the parent revision holds the original");
    }

    [Fact]
    public async Task MissingPathAtRevision_ReturnsEmpty_NotAnError()
    {
        // This is what an added file looks like from the "before" side — it must be a
        // normal empty result so the viewer can show "added", not throw.
        File.WriteAllText(Path.Combine(_repoPath, "a.txt"), "hello");
        RunGit("add a.txt");
        RunGit("commit -q -m first");

        var head = (await _git.GetCommitGraphAsync(_repoPath))[0].Hash;
        var blob = await _git.GetFileBlobAsync(_repoPath, head, "never-existed.png");

        blob.Should().BeEmpty();
    }

    [Fact]
    public async Task Revision_RejectsArgumentInjection()
    {
        File.WriteAllText(Path.Combine(_repoPath, "a.txt"), "x");
        RunGit("add a.txt");
        RunGit("commit -q -m first");

        var act = async () => await _git.GetFileBlobAsync(_repoPath, "--upload-pack=evil", "a.txt");

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Revision_AcceptsHashParentAndIndexStages()
    {
        File.WriteAllText(Path.Combine(_repoPath, "a.txt"), "x");
        RunGit("add a.txt");
        RunGit("commit -q -m first");

        var head = (await _git.GetCommitGraphAsync(_repoPath))[0].Hash;

        // None of these should throw; content is irrelevant here.
        await _git.GetFileBlobAsync(_repoPath, head, "a.txt");
        await _git.GetFileBlobAsync(_repoPath, "HEAD", "a.txt");
        await _git.GetFileBlobAsync(_repoPath, ":0", "a.txt");
    }
}
