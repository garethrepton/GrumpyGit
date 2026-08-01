using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CliWrap;
using CliWrap.Buffered;
using GrumpyGit.Core.Git;
using Octokit;

namespace GrumpyGit.App.Services;

public class GitHubService
{
    private GitHubClient? _client;
    private string? _cachedOwner;
    private string? _cachedRepo;

    // ── Credential retrieval via Git Credential Manager ─────────────────────

    private static async Task<string?> GetGitHubTokenAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await GitProcess.Start()
                .WithArguments(new[] { "credential", "fill" })
                .WithStandardInputPipe(PipeSource.FromString("protocol=https\nhost=github.com\n\n"))
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);

            if (result.ExitCode != 0)
                return null;

            foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("password=", StringComparison.OrdinalIgnoreCase))
                    return line.Substring("password=".Length).Trim();
            }
        }
        catch
        {
            // Git credential manager not available
        }

        return null;
    }

    // ── Remote URL parsing ──────────────────────────────────────────────────

    private static readonly Regex HttpsPattern =
        new(@"https?://github\.com/(?<owner>[^/]+)/(?<repo>[^/.]+?)(?:\.git)?/?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SshPattern =
        new(@"git@github\.com:(?<owner>[^/]+)/(?<repo>[^/.]+?)(?:\.git)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static (string owner, string repo)? ParseRemoteUrl(string remoteUrl)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl))
            return null;

        var match = HttpsPattern.Match(remoteUrl);
        if (!match.Success)
            match = SshPattern.Match(remoteUrl);

        if (!match.Success)
            return null;

        return (match.Groups["owner"].Value, match.Groups["repo"].Value);
    }

    private static async Task<string?> GetRemoteUrlAsync(string repoPath, CancellationToken ct = default)
    {
        var result = await GitProcess.Start()
            .WithArguments(new[] { "remote", "get-url", "origin" })
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    // ── Client initialization ───────────────────────────────────────────────

    private async Task<(GitHubClient client, string owner, string repo)> GetClientAsync(
        string repoPath, CancellationToken ct = default)
    {
        var remoteUrl = await GetRemoteUrlAsync(repoPath, ct);
        if (string.IsNullOrEmpty(remoteUrl))
            throw new InvalidOperationException("No remote 'origin' configured for this repository.");

        var parsed = ParseRemoteUrl(remoteUrl);
        if (parsed is null)
            throw new InvalidOperationException($"Remote URL is not a GitHub repository: {remoteUrl}");

        var (owner, repo) = parsed.Value;

        // Reuse client if owner/repo haven't changed
        if (_client is not null && _cachedOwner == owner && _cachedRepo == repo)
            return (_client, owner, repo);

        var token = await GetGitHubTokenAsync(ct);
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException(
                "No GitHub token found. Ensure Git Credential Manager is configured for github.com.");

        _client = new GitHubClient(new ProductHeaderValue("GrumpyGit"))
        {
            Credentials = new Octokit.Credentials(token)
        };
        _cachedOwner = owner;
        _cachedRepo = repo;

        return (_client, owner, repo);
    }

    // ── Public API ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<PullRequest>> GetPullRequestsAsync(
        string repoPath, CancellationToken ct = default)
    {
        var (client, owner, repo) = await GetClientAsync(repoPath, ct);

        var prs = await client.PullRequest.GetAllForRepository(owner, repo,
            new PullRequestRequest { State = ItemStateFilter.Open });

        return prs.ToList();
    }

    public async Task<PullRequest> CreatePullRequestAsync(
        string repoPath, string title, string body, string head, string baseBranch, bool isDraft,
        CancellationToken ct = default)
    {
        var (client, owner, repo) = await GetClientAsync(repoPath, ct);

        var newPr = new NewPullRequest(title, head, baseBranch)
        {
            Body = body,
            Draft = isDraft
        };

        return await client.PullRequest.Create(owner, repo, newPr);
    }

    public async Task<IReadOnlyList<Issue>> GetIssuesAsync(
        string repoPath, string? filter = null, CancellationToken ct = default)
    {
        var (client, owner, repo) = await GetClientAsync(repoPath, ct);

        var request = new RepositoryIssueRequest
        {
            State = ItemStateFilter.Open,
            Filter = IssueFilter.All
        };

        var issues = await client.Issue.GetAllForRepository(owner, repo, request);

        // Filter out pull requests (GitHub API returns PRs as issues)
        var result = issues.Where(i => i.PullRequest == null).ToList();

        if (!string.IsNullOrWhiteSpace(filter))
        {
            result = result
                .Where(i => i.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)
                             || i.Number.ToString() == filter)
                .ToList();
        }

        return result;
    }

    public async Task<IReadOnlyList<Issue>> GetIssuesByNumbersAsync(
        string repoPath, IEnumerable<int> issueNumbers, CancellationToken ct = default)
    {
        var (client, owner, repo) = await GetClientAsync(repoPath, ct);

        var results = new List<Issue>();
        foreach (var number in issueNumbers)
        {
            try
            {
                var issue = await client.Issue.Get(owner, repo, number);
                if (issue.PullRequest == null) // Only actual issues, not PRs
                    results.Add(issue);
            }
            catch (NotFoundException)
            {
                // Issue doesn't exist — skip
            }
        }

        return results;
    }

    public async Task<string> GetPullRequestDiffAsync(
        string repoPath, int prNumber, CancellationToken ct = default)
    {
        var (client, owner, repo) = await GetClientAsync(repoPath, ct);

        // Use the API to get the diff
        var files = await client.PullRequest.Files(owner, repo, prNumber);

        // Build a combined diff summary
        var lines = new List<string>();
        foreach (var file in files)
        {
            lines.Add($"--- a/{file.FileName}");
            lines.Add($"+++ b/{file.FileName}");
            lines.Add($"Status: {file.Status} | +{file.Additions} -{file.Deletions}");
            if (!string.IsNullOrEmpty(file.Patch))
                lines.Add(file.Patch);
            lines.Add("");
        }

        return string.Join('\n', lines);
    }

    // ── Issue reference parsing ─────────────────────────────────────────────

    private static readonly Regex IssueRefPattern =
        new(@"#(\d+)", RegexOptions.Compiled);

    public static IReadOnlyList<int> ParseIssueReferences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<int>();

        return IssueRefPattern.Matches(text)
            .Select(m => int.Parse(m.Groups[1].Value))
            .Distinct()
            .ToList();
    }
}
