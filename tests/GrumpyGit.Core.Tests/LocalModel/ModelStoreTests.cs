using FluentAssertions;
using GrumpyGit.Core.LocalModel;

namespace GrumpyGit.Core.Tests.LocalModel;

/// <summary>
/// The store, which is the only thing in the application that deletes a file.
///
/// What is worth asserting is the boundary: it removes what it downloaded, it leaves a
/// user's own model alone, and a half-finished sharded download reads as incomplete rather
/// than as a model that will load.
/// </summary>
public class ModelStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly ModelStore _store;

    public ModelStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"grumpygit-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _store = new ModelStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static ModelOption Sharded(params string[] names) =>
        new("Sharded",
            names.Select(n => new ModelPart($"https://example.invalid/{n}", n, 4, new string('a', 64))).ToList(),
            "a test");

    private void Write(string name, int bytes = 4) =>
        File.WriteAllBytes(Path.Combine(_dir, name), new byte[bytes]);

    [Fact]
    public void AModelIsInstalledOnlyWhenEveryPartIsPresent()
    {
        var option = Sharded("a.gguf", "b.gguf");

        Write("a.gguf");
        _store.IsInstalled(option).Should().BeFalse();
        _store.IsPartiallyInstalled(option).Should().BeTrue();

        Write("b.gguf");
        _store.IsInstalled(option).Should().BeTrue();
        _store.IsPartiallyInstalled(option).Should().BeFalse();
    }

    [Fact]
    public void DeleteRemovesEveryPartAndReportsWhatItFreed()
    {
        var option = Sharded("a.gguf", "b.gguf");
        Write("a.gguf", 100);
        Write("b.gguf", 50);

        _store.Delete(option).Should().Be(150);
        Directory.GetFiles(_dir).Should().BeEmpty();
    }

    [Fact]
    public void DeleteClearsTheLeftoverPartFileOfAnInterruptedDownload()
    {
        var option = Sharded("a.gguf");
        Write("a.gguf.part", 20);

        _store.Delete(option).Should().Be(20);
        Directory.GetFiles(_dir).Should().BeEmpty();
    }

    [Fact]
    public void DeletingAModelThatIsNotThereIsNotAnError()
    {
        _store.Delete(Sharded("absent.gguf")).Should().Be(0);
    }

    [Fact]
    public void NothingOutsideTheStoreCanBeDeleted()
    {
        // The user's own GGUF is not ours to tidy, and a name that climbs out of the
        // directory is the way that would happen by accident.
        var outside = Path.Combine(_dir, "..", $"grumpygit-outsider-{Guid.NewGuid():N}.gguf");
        File.WriteAllBytes(outside, new byte[8]);

        try
        {
            var escaping = Sharded(Path.Combine("..", Path.GetFileName(outside)));

            var act = () => _store.Delete(escaping);

            act.Should().Throw<InvalidOperationException>();
            File.Exists(outside).Should().BeTrue();
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void AnAbsolutePathIsRefusedRatherThanFollowed()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), $"grumpygit-abs-{Guid.NewGuid():N}.gguf");
        File.WriteAllBytes(elsewhere, new byte[8]);

        try
        {
            var act = () => _store.Delete(Sharded(elsewhere));

            act.Should().Throw<InvalidOperationException>();
            File.Exists(elsewhere).Should().BeTrue();
        }
        finally
        {
            File.Delete(elsewhere);
        }
    }

    [Fact]
    public void PathForNamesTheFirstPart()
    {
        // llama.cpp opens the first shard and finds the rest itself.
        _store.PathFor(Sharded("a.gguf", "b.gguf"))
            .Should().Be(Path.Combine(_dir, "a.gguf"));
    }
}
