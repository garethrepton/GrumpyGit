using System.Diagnostics;
using FluentAssertions;
using GrumpyGit.Core.Git;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Tests;

/// <summary>
/// Integration tests for hunk-level staging using real temp git repos.
/// These tests create actual git repos, modify files, and verify that
/// patch-based staging with <c>git apply --cached</c> works correctly.
/// </summary>
public class GitServiceHunkStagingTests : IDisposable
{
    private readonly string _repoPath;
    private readonly GitService _git = new();

    public GitServiceHunkStagingTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), "grumpygit-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_repoPath);
        RunGit("init");
        RunGit("config user.email test@test.com");
        RunGit("config user.name Test");
    }

    public void Dispose()
    {
        try
        {
            // Remove read-only attributes before deleting (git objects)
            foreach (var fi in new DirectoryInfo(_repoPath).EnumerateFiles("*", SearchOption.AllDirectories))
                fi.Attributes = FileAttributes.Normal;
            Directory.Delete(_repoPath, recursive: true);
        }
        catch { }
    }

    private void RunGit(string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = _repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        var proc = Process.Start(psi)!;
        proc.WaitForExit(10000);
        if (proc.ExitCode != 0)
            throw new Exception($"git {args} failed: {proc.StandardError.ReadToEnd()}");
    }

    private void WriteFile(string name, string content)
    {
        File.WriteAllText(Path.Combine(_repoPath, name), content);
    }

    private string ReadStagedContent(string fileName)
    {
        var psi = new ProcessStartInfo("git", $"show :{fileName}")
        {
            WorkingDirectory = _repoPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        var proc = Process.Start(psi)!;
        var output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(10000);
        return output;
    }

    [Fact]
    public async Task StageHunk_StagesOneHunkOfTwo()
    {
        // Setup: create a file with enough lines between changes to produce two hunks.
        // Git's default context is 3 lines, so we need >6 unchanged lines between changes.
        var lines = new List<string>();
        for (int i = 1; i <= 20; i++) lines.Add($"line{i}");
        WriteFile("test.txt", string.Join("\n", lines) + "\n");
        RunGit("add -- test.txt");
        RunGit("commit -m initial");

        // Modify line 2 and line 18 (far enough apart for two hunks)
        lines[1] = "LINE2";
        lines[17] = "LINE18";
        WriteFile("test.txt", string.Join("\n", lines) + "\n");

        // Get the diff
        var diffOutput = await _git.GetUnstagedDiffAsync(_repoPath, "test.txt");
        var parsed = UnifiedDiffParser.Parse(diffOutput);

        parsed.Hunks.Should().HaveCount(2, "modifying lines 2 and 18 (far apart) should create two hunks");

        // Stage only the first hunk
        var patch = PatchBuilder.BuildFromHunks(parsed.FileHeaderLines, new[] { parsed.Hunks[0] });
        await _git.StageHunkAsync(_repoPath, patch);

        // Verify: the staged content should have LINE2 but still have line18
        var staged = ReadStagedContent("test.txt");
        staged.Should().Contain("LINE2");
        staged.Should().Contain("line18"); // second hunk not staged
        staged.Should().NotContain("LINE18");
    }

    [Fact]
    public async Task UnstageHunk_UnstagesOneHunk()
    {
        // Setup: create file, commit, modify, stage everything, then unstage one hunk
        WriteFile("test.txt", "aaa\nbbb\nccc\nddd\neee\nfff\nggg\nhhh\n");
        RunGit("add -- test.txt");
        RunGit("commit -m initial");

        WriteFile("test.txt", "aaa\nBBB\nccc\nddd\neee\nfff\nGGG\nhhh\n");
        RunGit("add -- test.txt");

        // Get the staged diff
        var diffOutput = await _git.GetStagedDiffAsync(_repoPath, "test.txt");
        var parsed = UnifiedDiffParser.Parse(diffOutput);

        parsed.Hunks.Should().HaveCountGreaterThanOrEqualTo(1);

        // Unstage the first hunk
        var patch = PatchBuilder.BuildFromHunks(parsed.FileHeaderLines, new[] { parsed.Hunks[0] });
        await _git.UnstageHunkAsync(_repoPath, patch);

        // The first hunk's change should no longer be staged
        var stagedAfter = ReadStagedContent("test.txt");
        stagedAfter.Should().Contain("bbb"); // first hunk unstaged - original content
    }

    [Fact]
    public async Task StageHunk_WorkingTreeNotModified()
    {
        // Ensure staging a hunk does not modify the working tree
        WriteFile("test.txt", "a\nb\nc\n");
        RunGit("add -- test.txt");
        RunGit("commit -m initial");

        WriteFile("test.txt", "a\nB\nc\n");
        var workingContent = File.ReadAllText(Path.Combine(_repoPath, "test.txt"));

        var diffOutput = await _git.GetUnstagedDiffAsync(_repoPath, "test.txt");
        var parsed = UnifiedDiffParser.Parse(diffOutput);

        var patch = PatchBuilder.BuildFromHunks(parsed.FileHeaderLines, new[] { parsed.Hunks[0] });
        await _git.StageHunkAsync(_repoPath, patch);

        // Working tree should be unchanged
        var afterContent = File.ReadAllText(Path.Combine(_repoPath, "test.txt"));
        afterContent.Should().Be(workingContent);
    }

    [Fact]
    public async Task IntentToAdd_AllowsPartialStagingOfUntrackedFile()
    {
        // Untracked files need intent-to-add before partial staging
        WriteFile("new.txt", "line1\nline2\nline3\n");

        await _git.IntentToAddAsync(_repoPath, "new.txt");

        // Now we should be able to get a diff for it
        var diffOutput = await _git.GetUnstagedDiffAsync(_repoPath, "new.txt");
        diffOutput.Should().NotBeEmpty();

        var parsed = UnifiedDiffParser.Parse(diffOutput);
        parsed.Hunks.Should().NotBeEmpty();
    }

    [Fact]
    public async Task StageSelectedLines_StagesOnlyChosenLines()
    {
        WriteFile("test.txt", "line1\nline2\nline3\n");
        RunGit("add -- test.txt");
        RunGit("commit -m initial");

        WriteFile("test.txt", "LINE1\nLINE2\nline3\n");

        var diffOutput = await _git.GetUnstagedDiffAsync(_repoPath, "test.txt");
        var parsed = UnifiedDiffParser.Parse(diffOutput);

        parsed.Hunks.Should().HaveCount(1);
        var hunk = parsed.Hunks[0];

        // Find the index of the first removed/added pair and select only that
        var selectedIndices = new HashSet<int>();
        for (int i = 0; i < hunk.Lines.Count; i++)
        {
            if (hunk.Lines[i].Type == DiffLineType.Removed && hunk.Lines[i].Content == "line1")
                selectedIndices.Add(i);
            if (hunk.Lines[i].Type == DiffLineType.Added && hunk.Lines[i].Content == "LINE1")
                selectedIndices.Add(i);
        }

        selectedIndices.Should().HaveCount(2, "should find one removed and one added line for 'line1'");

        // Debug: dump hunk lines
        var linesDump = string.Join("\n", hunk.Lines.Select((l, idx) =>
            $"  [{idx}] {l.Type} '{l.Content}' old={l.OldLineNumber} new={l.NewLineNumber}"));

        var patch = PatchBuilder.BuildFromSelectedLines(parsed.FileHeaderLines, hunk, selectedIndices);
        patch.Should().NotBeEmpty($"Patch should not be empty.\nHunk lines:\n{linesDump}\nSelected: {string.Join(",", selectedIndices)}");

        try
        {
            await _git.StageHunkAsync(_repoPath, patch);
        }
        catch (GitException ex)
        {
            throw new Exception($"git apply failed.\nStderr: {ex.GitOutput}\nRaw diff:\n{diffOutput}\nHunk lines:\n{linesDump}\nPatch:\n{patch}", ex);
        }

        var staged = ReadStagedContent("test.txt");
        staged.Should().Contain("LINE1"); // first line staged
        staged.Should().Contain("line2"); // second line not staged (still original)
    }
}
