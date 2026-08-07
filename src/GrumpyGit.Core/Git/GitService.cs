using CliWrap;
using CliWrap.Buffered;
using GrumpyGit.Core.Models;
using System.Linq;

namespace GrumpyGit.Core.Git;

public class GitService : IGitService
{
    /// <summary>
    /// Every git invocation in this class goes through <see cref="GitProcess.Start"/>,
    /// which neutralises repo-local config that would otherwise let an untrusted
    /// repository choose commands for git to execute. See <see cref="GitProcess"/>.
    /// </summary>
    private static Command GitCmd() => GitProcess.Start();

    /// <summary>
    /// As <see cref="GitCmd"/>, plus default language diff drivers so hunk headers carry
    /// the enclosing declaration. Used by the diff-producing commands only — see
    /// <see cref="GitProcess.StartForDiff"/> for why it is not the default everywhere.
    /// </summary>
    private static Command GitDiffCmd() => GitProcess.StartForDiff();

    // -------------------------------------------------------------------------
    // Input validation helpers
    // -------------------------------------------------------------------------

    private static void ValidateRepoPath(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
            throw new ArgumentException("Repository path must not be empty.", nameof(repoPath));
        if (!Path.IsPathRooted(repoPath))
            throw new ArgumentException($"Repository path must be an absolute path: '{repoPath}'", nameof(repoPath));
        if (!Directory.Exists(repoPath))
            throw new ArgumentException($"Repository path does not exist: '{repoPath}'", nameof(repoPath));
    }

    private static readonly System.Text.RegularExpressions.Regex HexHashPattern =
        new(@"^[0-9a-fA-F]{4,64}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static void ValidateHash(string hash, string paramName)
    {
        if (string.IsNullOrWhiteSpace(hash))
            throw new ArgumentException("Commit hash must not be empty.", paramName);
        if (!HexHashPattern.IsMatch(hash))
            throw new ArgumentException($"Invalid commit hash format: '{hash}'. Expected 4-64 hex characters.", paramName);
    }

    private static readonly System.Text.RegularExpressions.Regex RemoteNamePattern =
        new(@"^[A-Za-z0-9._\-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex BranchNamePattern =
        new(@"^[A-Za-z0-9._\-/]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static void ValidateRemote(string remote)
    {
        if (string.IsNullOrWhiteSpace(remote))
            throw new ArgumentException("Remote name must not be empty.", nameof(remote));
        if (remote.StartsWith('-'))
            throw new ArgumentException($"Remote name must not start with '-': '{remote}'", nameof(remote));
        if (!RemoteNamePattern.IsMatch(remote))
            throw new ArgumentException($"Invalid remote name: '{remote}'", nameof(remote));
    }

    private static void ValidateBranch(string branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
            throw new ArgumentException("Branch name must not be empty.", nameof(branch));
        if (branch.StartsWith('-'))
            throw new ArgumentException($"Branch name must not start with '-': '{branch}'", nameof(branch));
        if (!BranchNamePattern.IsMatch(branch))
            throw new ArgumentException($"Invalid branch name: '{branch}'", nameof(branch));
    }

    private static void ValidateFilePath(string repoPath, string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path must not be empty.", nameof(filePath));

        // Reject absolute paths — file paths in git must be relative to the repo root
        if (Path.IsPathRooted(filePath))
            throw new ArgumentException($"File path must be relative to the repository root: '{filePath}'", nameof(filePath));

        // Reject path traversal — resolve and confirm it stays within the repo
        var fullPath = Path.GetFullPath(Path.Combine(repoPath, filePath));
        var repoFullPath = Path.GetFullPath(repoPath);
        if (!fullPath.StartsWith(repoFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !fullPath.Equals(repoFullPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"File path escapes the repository root: '{filePath}'", nameof(filePath));
    }

    // -------------------------------------------------------------------------
    // Commit graph
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fields separated by NUL (%x00), records terminated by RS (%x1E).
    /// Using RS as a record terminator (not separator) means trailing
    /// whitespace / newlines don't pollute the last field.
    ///
    /// Co-authored-by trailers are pulled out explicitly (US, %x1F, between multiple
    /// values) because they are how AI coding agents attribute themselves — the human
    /// stays the author, the agent is added as co-author. Subject stays last so a
    /// subject containing a delimiter can be rejoined without corrupting other fields.
    ///
    /// Shared by every command whose output <see cref="ParseCommitGraph"/> reads; the two
    /// must change together.
    /// </summary>
    private const string CommitGraphFormat =
        "%H%x00%P%x00%an%x00%ae%x00%ai%x00%D%x00%cn%x00%ce%x00" +
        "%(trailers:key=Co-authored-by,valueonly,separator=%x1F)%x00%s%x1E";

    public async Task<IReadOnlyList<CommitNode>> GetCommitGraphAsync(
        string repoPath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("log")
                .Add("--all")
                .Add($"--format={CommitGraphFormat}")
                .Add("--topo-order"))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git log failed", result.ExitCode, result.StandardError);

        return ParseCommitGraph(result.StandardOutput);
    }

    private static IReadOnlyList<CommitNode> ParseCommitGraph(string output)
    {
        var nodes = new List<CommitNode>();

        // Records are terminated by \x1E (ASCII 30, Record Separator).
        // Split on RS and discard empty entries produced by trailing RS or blank output.
        var records = output.Split('\x1E', StringSplitOptions.RemoveEmptyEntries);

        foreach (var record in records)
        {
            // Trim any stray newlines that git may emit before/after the RS.
            var trimmed = record.Trim('\n', '\r');
            if (string.IsNullOrEmpty(trimmed))
                continue;

            // Fields: Hash, Parents, AuthorName, AuthorEmail, AuthorDate, Decorations,
            //         CommitterName, CommitterEmail, CoAuthorTrailers, Subject
            const int fieldCount = 10;
            var fields = trimmed.Split('\x00');
            if (fields.Length < fieldCount)
                continue;

            var hash = fields[0];
            var parentHashes = string.IsNullOrEmpty(fields[1])
                ? []
                : fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var authorName = fields[2];
            var authorEmail = fields[3];

            if (!DateTimeOffset.TryParse(fields[4], out var authorDate))
                authorDate = DateTimeOffset.MinValue;

            var refNames = ParseRefNames(fields[5]);

            var committerName = fields[6];
            var committerEmail = fields[7];

            var coAuthors = string.IsNullOrEmpty(fields[8])
                ? []
                : fields[8].Split('\x1F', StringSplitOptions.RemoveEmptyEntries)
                           .Select(v => v.Trim())
                           .Where(v => v.Length > 0)
                           .ToArray();

            // Subject is the last field; any %x00 inside it (unusual but possible)
            // would have been split — rejoin the remainder.
            var subject = fields.Length == fieldCount
                ? fields[fieldCount - 1]
                : string.Join('\x00', fields[(fieldCount - 1)..]);

            nodes.Add(new CommitNode(hash, parentHashes, authorName, authorEmail, authorDate, subject, refNames)
            {
                CoAuthors = coAuthors,
                CommitterName = committerName,
                CommitterEmail = committerEmail,
            });
        }

        return nodes;
    }

    /// <summary>
    /// Parses the %D decoration string (e.g. "HEAD -> main, origin/main, tag: v1.0")
    /// into an array of individual ref name tokens.
    /// </summary>
    private static string[] ParseRefNames(string decorations)
    {
        if (string.IsNullOrWhiteSpace(decorations))
            return [];

        // Split on ", " and trim each token.
        return decorations
            .Split(", ", StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .ToArray();
    }

    // -------------------------------------------------------------------------
    // Files changed in a commit
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<FileChange>> GetFilesChangedInCommitAsync(
        string repoPath, string commitHash, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateHash(commitHash, nameof(commitHash));

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("diff-tree")
                .Add("--no-ext-diff")
                .Add("--root")          // show all files for the initial commit (no parent)
                .Add("--no-commit-id")
                .Add("-r")
                .Add("--name-status")
                .Add("-z")
                .Add(commitHash))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git diff-tree failed", result.ExitCode, result.StandardError);

        return ParseDiffTreeOutput(result.StandardOutput);
    }

    private static IReadOnlyList<FileChange> ParseDiffTreeOutput(string output)
    {
        if (string.IsNullOrEmpty(output))
            return [];

        var changes = new List<FileChange>();

        // With -z, tokens are separated by NUL.  The output is NOT newline-separated;
        // the entire output is a flat NUL-delimited sequence:
        //   status NUL path NUL           (for A/M/D/T/...)
        //   Rnn NUL old-path NUL new-path NUL   (for renames/copies)
        // There may be a trailing NUL which produces an empty trailing token.
        var tokens = output.Split('\0');
        int i = 0;

        while (i < tokens.Length)
        {
            var status = tokens[i].Trim();

            if (string.IsNullOrEmpty(status))
            {
                i++;
                continue;
            }

            var statusChar = status[0]; // 'A', 'M', 'D', 'R', 'C', …

            if ((statusChar == 'R' || statusChar == 'C') && i + 2 < tokens.Length)
            {
                // Rename / Copy: status NUL old-path NUL new-path
                var oldPath = tokens[i + 1];
                var newPath = tokens[i + 2];
                var fileStatus = statusChar == 'R' ? FileChangeStatus.Renamed : FileChangeStatus.Copied;
                changes.Add(new FileChange(newPath, oldPath, fileStatus));
                i += 3;
            }
            else if (i + 1 < tokens.Length)
            {
                // Normal: status NUL path
                var path = tokens[i + 1];
                var fileStatus = MapDiffTreeStatus(statusChar);
                changes.Add(new FileChange(path, string.Empty, fileStatus));
                i += 2;
            }
            else
            {
                // Malformed / trailing token — skip
                i++;
            }
        }

        return changes;
    }

    private static FileChangeStatus MapDiffTreeStatus(char c) => c switch
    {
        'A' => FileChangeStatus.Added,
        'D' => FileChangeStatus.Deleted,
        'M' => FileChangeStatus.Modified,
        'R' => FileChangeStatus.Renamed,
        'C' => FileChangeStatus.Copied,
        _   => FileChangeStatus.Modified   // T (type change), U, X, B — treat as modified
    };

    // -------------------------------------------------------------------------
    // Working-tree status  (porcelain v2)
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<FileChange>> GetWorkingTreeStatusAsync(
        string repoPath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("status")
                .Add("--porcelain=v2")
                .Add("--untracked-files=all")
                .Add("-z"))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git status failed", result.ExitCode, result.StandardError);

        return ParsePorcelainV2(result.StandardOutput);
    }

    private static IReadOnlyList<FileChange> ParsePorcelainV2(string output)
    {
        if (string.IsNullOrEmpty(output))
            return [];

        var changes = new List<FileChange>();

        // With -z the NUL byte terminates each entry.  For rename entries (type "2"),
        // the renamed-from path is a SECOND NUL-terminated token immediately following
        // the first, separated from the main entry by a NUL.
        //
        // We split on NUL and process the resulting token array.
        var tokens = output.Split('\0');
        int i = 0;

        while (i < tokens.Length)
        {
            var entry = tokens[i];

            if (string.IsNullOrEmpty(entry))
            {
                i++;
                continue;
            }

            if (entry.StartsWith("1 "))
            {
                // Ordinary changed entry
                // Format: 1 XY sub mH mI mW hH hI path
                var parts = entry.Split(' ', 9);   // max 9 tokens; path is last
                if (parts.Length < 9)
                {
                    i++;
                    continue;
                }

                var xy = parts[1];                 // two-char XY status field
                var path = parts[8];
                var stagedChar   = xy.Length > 0 ? xy[0] : '.';
                var unstagedChar = xy.Length > 1 ? xy[1] : '.';

                // Emit a FileChange for staged changes (index differs from HEAD)
                if (stagedChar != '.')
                    changes.Add(new FileChange(path, string.Empty, MapPorcelainStatus(stagedChar), IsStaged: true));

                // Emit a FileChange for unstaged changes (worktree differs from index)
                if (unstagedChar != '.')
                    changes.Add(new FileChange(path, string.Empty, MapPorcelainStatus(unstagedChar), IsStaged: false));

                i++;
            }
            else if (entry.StartsWith("2 "))
            {
                // Renamed / copied entry
                // Format: 2 XY sub mH mI mW hH hI X score path\0origPath
                // The origPath arrives as the NEXT NUL-terminated token.
                var parts = entry.Split(' ', 10);
                if (parts.Length < 10)
                {
                    i++;
                    continue;
                }

                var xy = parts[1];
                var newPath = parts[9];

                // The original path is in the very next NUL token
                var origPath = (i + 1 < tokens.Length) ? tokens[i + 1] : string.Empty;

                var stagedChar   = xy.Length > 0 ? xy[0] : '.';
                var unstagedChar = xy.Length > 1 ? xy[1] : '.';

                if (stagedChar != '.')
                    changes.Add(new FileChange(newPath, origPath, MapPorcelainStatus(stagedChar), IsStaged: true));

                if (unstagedChar != '.')
                    changes.Add(new FileChange(newPath, origPath, MapPorcelainStatus(unstagedChar), IsStaged: false));

                i += 2;   // skip the entry token AND the origPath token
            }
            else if (entry.StartsWith("? "))
            {
                // Untracked file: "? path"
                var path = entry[2..];
                changes.Add(new FileChange(path, string.Empty, FileChangeStatus.Untracked));
                i++;
            }
            else if (entry.StartsWith("u "))
            {
                // Unmerged (conflicted) entry
                // Format: u XY sub m1 m2 m3 mW h1 h2 h3 path
                var parts = entry.Split(' ', 11);
                if (parts.Length >= 11)
                {
                    var path = parts[10];
                    changes.Add(new FileChange(path, string.Empty, FileChangeStatus.Conflicted, IsStaged: false));
                }
                i++;
            }
            else
            {
                // Header lines (# branch.oid etc.) or unknown — skip
                i++;
            }
        }

        return changes;
    }

    private static FileChangeStatus MapPorcelainStatus(char c) => c switch
    {
        'A' => FileChangeStatus.Added,
        'M' => FileChangeStatus.Modified,
        'D' => FileChangeStatus.Deleted,
        'R' => FileChangeStatus.Renamed,
        'C' => FileChangeStatus.Copied,
        '?' => FileChangeStatus.Untracked,
        _   => FileChangeStatus.Modified
    };

    // -------------------------------------------------------------------------
    // Diff methods
    // -------------------------------------------------------------------------

    public async Task<string> GetFileDiffAsync(
        string repoPath, string commitHash, string filePath, CancellationToken ct = default)
        => await GetFileDiffAsync(repoPath, commitHash, filePath, DiffOptions.Default, ct);

    public async Task<string> GetFileDiffAsync(
        string repoPath, string commitHash, string filePath, DiffOptions options,
        CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateHash(commitHash, nameof(commitHash));
        ValidateFilePath(repoPath, filePath);

        // Use diff-tree -p so the initial commit (no parent) works via --root.
        // diff <hash>^ fails with exit 128 when there is no parent.
        var result = await GitDiffCmd()
            .WithArguments(args =>
            {
                args
                    .Add("diff-tree")
                    .Add("-p")
                    .Add("--no-ext-diff")
                    .Add("--no-textconv")
                    .Add("--root")
                    .Add("--no-commit-id")
                    .Add("-r");
                options.Apply(args);
                args
                    .Add(commitHash)
                    .Add("--")
                    .Add(filePath);
            })
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git diff-tree -p failed", result.ExitCode, result.StandardError);

        return result.StandardOutput;
    }

    public async Task<string> GetUnstagedDiffAsync(
        string repoPath, string filePath, CancellationToken ct = default)
        => await GetUnstagedDiffAsync(repoPath, filePath, DiffOptions.Default, ct);

    public async Task<string> GetUnstagedDiffAsync(
        string repoPath, string filePath, DiffOptions options, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateFilePath(repoPath, filePath);

        var result = await GitDiffCmd()
            .WithArguments(args =>
            {
                args
                    .Add("diff")
                    .Add("--no-ext-diff")
                    .Add("--no-textconv");
                options.Apply(args);
                args
                    .Add("--")
                    .Add(filePath);
            })
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode > 1)
            throw new GitException("git diff (unstaged) failed", result.ExitCode, result.StandardError);

        return result.StandardOutput;
    }

    public async Task<string> GetStagedDiffAsync(
        string repoPath, string filePath, CancellationToken ct = default)
        => await GetStagedDiffAsync(repoPath, filePath, DiffOptions.Default, ct);

    public async Task<string> GetStagedDiffAsync(
        string repoPath, string filePath, DiffOptions options, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateFilePath(repoPath, filePath);

        var result = await GitDiffCmd()
            .WithArguments(args =>
            {
                args
                    .Add("diff")
                    .Add("--cached")
                    .Add("--no-ext-diff")
                    .Add("--no-textconv");
                options.Apply(args);
                args
                    .Add("--")
                    .Add(filePath);
            })
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode > 1)
            throw new GitException("git diff --cached failed", result.ExitCode, result.StandardError);

        return result.StandardOutput;
    }

    public async Task<string> GetCommitRangeDiffAsync(
        string repoPath, string fromHash, string toHash, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateHash(fromHash, nameof(fromHash));
        ValidateHash(toHash, nameof(toHash));

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("diff")
                .Add("--no-ext-diff")
                .Add("--no-textconv")
                .Add(fromHash)
                .Add(toHash))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode > 1)
            throw new GitException("git diff (range) failed", result.ExitCode, result.StandardError);

        return result.StandardOutput;
    }

    /// <summary>
    /// Net file list between two commits. Replaces callers that used
    /// <see cref="RunCommandAsync"/> with an interpolated argument string, so hashes
    /// go through <c>ValidateHash</c> and are passed as discrete argv entries.
    /// </summary>
    public async Task<IReadOnlyList<FileChange>> GetCommitRangeFileListAsync(
        string repoPath, string fromHash, string toHash, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateHash(fromHash, nameof(fromHash));
        ValidateHash(toHash, nameof(toHash));

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("diff")
                .Add("--no-ext-diff")
                .Add("--no-textconv")
                .Add("--name-status")
                .Add("-z")
                .Add(fromHash)
                .Add(toHash))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode > 1)
            throw new GitException("git diff --name-status failed", result.ExitCode, result.StandardError);

        return ParseNameStatusZ(result.StandardOutput);
    }

    /// <summary>
    /// Per-file added/removed line counts between two commits, from <c>--numstat</c>.
    /// Used to size and rank changes when reviewing an AI session.
    /// </summary>
    public async Task<Dictionary<string, (int Added, int Removed)>> GetCommitRangeStatsAsync(
        string repoPath, string fromHash, string toHash, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateHash(fromHash, nameof(fromHash));
        ValidateHash(toHash, nameof(toHash));

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("diff")
                .Add("--no-ext-diff")
                .Add("--no-textconv")
                .Add("--numstat")
                .Add(fromHash)
                .Add(toHash))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        return result.ExitCode > 1
            ? new Dictionary<string, (int, int)>(StringComparer.Ordinal)
            : ParseNumstat(result.StandardOutput);
    }

    /// <summary>
    /// Per-file added/removed line counts for the working tree.
    /// </summary>
    /// <param name="staged">True for the index (<c>--cached</c>), false for unstaged changes.</param>
    public async Task<Dictionary<string, (int Added, int Removed)>> GetWorkingTreeStatsAsync(
        string repoPath, bool staged, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);

        var result = await GitCmd()
            .WithArguments(args =>
            {
                args.Add("diff").Add("--no-ext-diff").Add("--no-textconv").Add("--numstat");
                if (staged) args.Add("--cached");
            })
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        return result.ExitCode > 1
            ? new Dictionary<string, (int, int)>(StringComparer.Ordinal)
            : ParseNumstat(result.StandardOutput);
    }

    /// <summary>Per-file added/removed line counts for a single commit.</summary>
    public async Task<Dictionary<string, (int Added, int Removed)>> GetCommitStatsAsync(
        string repoPath, string commitHash, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateHash(commitHash, nameof(commitHash));

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("diff-tree")
                .Add("--no-ext-diff")
                .Add("--no-textconv")
                .Add("--numstat")
                .Add("--root")
                .Add("--no-commit-id")
                .Add("-r")
                .Add(commitHash))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        return result.ExitCode > 1
            ? new Dictionary<string, (int, int)>(StringComparer.Ordinal)
            : ParseNumstat(result.StandardOutput);
    }

    /// <summary>
    /// Parses <c>--numstat</c> output: <c>added\tremoved\tpath</c>.
    ///
    /// Binary files report "-" for both counts. Those are deliberately omitted rather
    /// than recorded as 0/0, so callers can tell "no line stats available" apart from
    /// "changed by zero lines".
    /// </summary>
    private static Dictionary<string, (int Added, int Removed)> ParseNumstat(string output)
    {
        var stats = new Dictionary<string, (int Added, int Removed)>(StringComparer.Ordinal);

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 3) continue;

            if (!int.TryParse(parts[0], out var added) || !int.TryParse(parts[1], out var removed))
                continue; // binary

            stats[parts[2]] = (added, removed);
        }

        return stats;
    }

    /// <summary>
    /// Raw bytes of a file as it existed at a given revision.
    ///
    /// Binary-safe: the output is piped straight to a stream rather than going through
    /// <c>ExecuteBufferedAsync</c>, which decodes stdout as text and would silently
    /// mangle every byte that is not valid in the current encoding — fatal for images.
    /// </summary>
    /// <param name="rev">
    /// Any revision git accepts for <c>rev:path</c> — a commit hash, <c>HEAD</c>, or an
    /// index stage such as <c>:0</c>. Hashes are validated; the small set of symbolic
    /// forms the app uses is allow-listed.
    /// </param>
    /// <returns>The blob contents, or an empty array when the path does not exist at
    /// that revision (a file that was added, or deleted, has no "before" or "after").</returns>
    public async Task<byte[]> GetFileBlobAsync(
        string repoPath, string rev, string filePath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateFilePath(repoPath, filePath);
        ValidateRevision(rev);

        using var buffer = new MemoryStream();

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("show")
                .Add($"{rev}:{filePath}"))
            .WithWorkingDirectory(repoPath)
            .WithStandardOutputPipe(PipeTarget.ToStream(buffer))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(ct);

        // A missing path at that revision is an expected outcome, not an error: it is
        // exactly what an added or deleted file looks like from one side.
        return result.ExitCode == 0 ? buffer.ToArray() : [];
    }

    private static readonly HashSet<string> AllowedSymbolicRevisions =
        new(StringComparer.Ordinal) { "HEAD", "HEAD^", "HEAD~1", ":0", ":1", ":2", ":3" };

    /// <summary>
    /// Accepts a commit hash or one of the few symbolic revisions the app uses.
    ///
    /// Without this, a caller could pass something like <c>--upload-pack=...</c> and
    /// have it land in the argument list. Everything else in this class validates its
    /// refs; this keeps that property intact for blob reads.
    /// </summary>
    private static void ValidateRevision(string rev)
    {
        if (string.IsNullOrWhiteSpace(rev))
            throw new ArgumentException("Revision must not be empty.", nameof(rev));

        if (AllowedSymbolicRevisions.Contains(rev))
            return;

        // Allow a hash, optionally with a single ^ or ~1 suffix for "the parent of".
        var bare = rev.EndsWith("^", StringComparison.Ordinal) ? rev[..^1]
                 : rev.EndsWith("~1", StringComparison.Ordinal) ? rev[..^2]
                 : rev;

        if (!HexHashPattern.IsMatch(bare))
            throw new ArgumentException($"Invalid revision: '{rev}'", nameof(rev));
    }

    /// <summary>Net diff for a single file between two commits.</summary>
    public async Task<string> GetCommitRangeFileDiffAsync(
        string repoPath, string fromHash, string toHash, string filePath, CancellationToken ct = default)
        => await GetCommitRangeFileDiffAsync(repoPath, fromHash, toHash, filePath, DiffOptions.Default, ct);

    public async Task<string> GetCommitRangeFileDiffAsync(
        string repoPath, string fromHash, string toHash, string filePath, DiffOptions options,
        CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateHash(fromHash, nameof(fromHash));
        ValidateHash(toHash, nameof(toHash));
        ValidateFilePath(repoPath, filePath);

        var result = await GitCmd()
            .WithArguments(args =>
            {
                args
                    .Add("diff")
                    .Add("--no-ext-diff")
                    .Add("--no-textconv");
                options.Apply(args);
                args
                    .Add(fromHash)
                    .Add(toHash)
                    .Add("--")
                    .Add(filePath);
            })
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode > 1)
            throw new GitException("git diff (range file) failed", result.ExitCode, result.StandardError);

        return result.StandardOutput;
    }

    /// <summary>
    /// Parses NUL-delimited <c>--name-status -z</c> output. Rename and copy entries
    /// span three tokens (status, old path, new path); everything else spans two.
    /// </summary>
    private static IReadOnlyList<FileChange> ParseNameStatusZ(string output)
    {
        var changes = new List<FileChange>();
        if (string.IsNullOrEmpty(output))
            return changes;

        var tokens = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < tokens.Length;)
        {
            var status = tokens[i++];
            if (i >= tokens.Length) break;

            // R100 / C75 carry a similarity score and a second path.
            var isRenameOrCopy = status.Length > 0 && (status[0] == 'R' || status[0] == 'C');

            if (isRenameOrCopy)
            {
                if (i + 1 >= tokens.Length) break;
                var oldPath = tokens[i++];
                var newPath = tokens[i++];
                changes.Add(new FileChange(
                    newPath,
                    oldPath,
                    status[0] == 'R' ? FileChangeStatus.Renamed : FileChangeStatus.Copied));
            }
            else
            {
                var path = tokens[i++];
                changes.Add(new FileChange(path, string.Empty, MapNameStatus(status)));
            }
        }

        return changes;
    }

    private static FileChangeStatus MapNameStatus(string status) => status.Length == 0
        ? FileChangeStatus.Modified
        : status[0] switch
        {
            'A' => FileChangeStatus.Added,
            'D' => FileChangeStatus.Deleted,
            'M' => FileChangeStatus.Modified,
            'R' => FileChangeStatus.Renamed,
            'C' => FileChangeStatus.Copied,
            'U' => FileChangeStatus.Conflicted,
            _ => FileChangeStatus.Modified,
        };

    // -------------------------------------------------------------------------
    // Staging / unstaging
    // -------------------------------------------------------------------------

    public async Task StageFileAsync(
        string repoPath, string filePath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateFilePath(repoPath, filePath);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("add")
                .Add("--")
                .Add(filePath))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git add failed", result.ExitCode, result.StandardError);
    }

    public async Task UnstageFileAsync(
        string repoPath, string filePath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateFilePath(repoPath, filePath);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("restore")
                .Add("--staged")
                .Add("--")
                .Add(filePath))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git restore --staged failed", result.ExitCode, result.StandardError);
    }

    // -------------------------------------------------------------------------
    // Commit
    // -------------------------------------------------------------------------

    public async Task<string> CommitAsync(
        string repoPath, string message, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Commit message must not be empty or whitespace.", nameof(message));

        var commitResult = await GitCmd()
            .WithArguments(args => args
                .Add("commit")
                .Add("-m")
                .Add(message))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (commitResult.ExitCode != 0)
            throw new GitException("git commit failed", commitResult.ExitCode, commitResult.StandardError);

        // Resolve the hash of the newly created commit.
        var revResult = await GitCmd()
            .WithArguments(args => args
                .Add("rev-parse")
                .Add("HEAD"))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (revResult.ExitCode != 0)
            throw new GitException("git rev-parse HEAD failed", revResult.ExitCode, revResult.StandardError);

        return revResult.StandardOutput.Trim();
    }

    // -------------------------------------------------------------------------
    // Push / Pull
    // -------------------------------------------------------------------------

    public async Task PushAsync(
        string repoPath, string remote = "origin", string? branch = null, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateRemote(remote);
        if (!string.IsNullOrEmpty(branch))
            ValidateBranch(branch);

        var result = await GitCmd()
            .WithArguments(args =>
            {
                // --follow-tags carries annotated tags reachable from the commits being
                // pushed. Without it, creating a tag and then pressing Push publishes the
                // commits and silently leaves the tag behind — which reads as "push did
                // nothing" and, for a tag-triggered CI release, means no build ever runs.
                // It only sends annotated tags, and never tags outside what is pushed, so
                // it cannot publish unrelated local tags.
                args.Add("push").Add("--follow-tags").Add(remote);
                if (!string.IsNullOrEmpty(branch))
                    args.Add(branch);
            })
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git push failed", result.ExitCode, result.StandardError);
    }

    public async Task PullAsync(
        string repoPath, string remote = "origin", string? branch = null, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateRemote(remote);
        if (!string.IsNullOrEmpty(branch))
            ValidateBranch(branch);

        var result = await GitCmd()
            .WithArguments(args =>
            {
                args.Add("pull").Add(remote);
                if (!string.IsNullOrEmpty(branch))
                    args.Add(branch);
            })
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git pull failed", result.ExitCode, result.StandardError);
    }

    // -------------------------------------------------------------------------
    // Branch info
    // -------------------------------------------------------------------------

    public async Task<string> GetCurrentBranchAsync(string repoPath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);

        // symbolic-ref succeeds on a normal branch, fails in detached HEAD
        var symResult = await GitCmd()
            .WithArguments(args => args.Add("symbolic-ref").Add("--short").Add("HEAD"))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (symResult.ExitCode == 0)
            return symResult.StandardOutput.Trim();

        // Detached HEAD — fall back to short hash
        var hashResult = await GitCmd()
            .WithArguments(args => args.Add("rev-parse").Add("--short").Add("HEAD"))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        return hashResult.ExitCode == 0
            ? $"(detached) {hashResult.StandardOutput.Trim()}"
            : "unknown";
    }

    // -------------------------------------------------------------------------
    // Branch management
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<string>> GetBranchesAsync(
        string repoPath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("branch")
                .Add("--list")
                .Add("--format=%(refname:short)"))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git branch --list failed", result.ExitCode, result.StandardError);

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .ToList();
    }

    public async Task CreateBranchAsync(
        string repoPath, string branchName, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateBranch(branchName);

        // `switch -c` checks the new branch out, which would move a worktree off the
        // branch it exists to hold. See the worktree section for why this is refused.
        await EnsureNotLinkedWorktreeAsync(repoPath, branchName, ct);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("switch")
                .Add("-c")
                .Add(branchName))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git switch -c failed", result.ExitCode, result.StandardError);
    }

    public async Task CheckoutBranchAsync(
        string repoPath, string branchName, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateBranch(branchName);

        await EnsureNotLinkedWorktreeAsync(repoPath, branchName, ct);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("switch")
                .Add(branchName))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git switch failed", result.ExitCode, result.StandardError);
    }

    public async Task MergeBranchAsync(
        string repoPath, string branchName, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateBranch(branchName);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("merge")
                .Add(branchName))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git merge failed", result.ExitCode, result.StandardError);
    }

    public async Task<string> GetRemoteUrlAsync(
        string repoPath, string remote = "origin", CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateRemote(remote);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("remote")
                .Add("get-url")
                .Add(remote))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        // Non-zero means no remote configured — not an error for the UI
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : string.Empty;
    }

    // -------------------------------------------------------------------------
    // Stash
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<string>> GetStashListAsync(
        string repoPath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);

        var result = await GitCmd()
            .WithArguments(args => args.Add("stash").Add("list"))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            return [];

        return result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .ToList();
    }

    public async Task StashAsync(
        string repoPath, string? message = null, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);

        var result = await GitCmd()
            .WithArguments(args =>
            {
                args.Add("stash").Add("push");
                if (!string.IsNullOrWhiteSpace(message))
                    args.Add("-m").Add(message);
            })
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git stash push failed", result.ExitCode, result.StandardError);
    }

    public async Task StashPopAsync(
        string repoPath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);

        var result = await GitCmd()
            .WithArguments(args => args.Add("stash").Add("pop"))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git stash pop failed", result.ExitCode, result.StandardError);
    }

    // -------------------------------------------------------------------------
    // Discard changes
    // -------------------------------------------------------------------------

    public async Task DiscardFileChangesAsync(
        string repoPath, string filePath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateFilePath(repoPath, filePath);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("restore")
                .Add("--")
                .Add(filePath))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git restore failed", result.ExitCode, result.StandardError);
    }

    public async Task RemoveUntrackedFileAsync(
        string repoPath, string filePath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateFilePath(repoPath, filePath);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("clean")
                .Add("-f")
                .Add("--")
                .Add(filePath))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git clean failed", result.ExitCode, result.StandardError);
    }

    // -------------------------------------------------------------------------
    // Hunk-level staging
    // -------------------------------------------------------------------------

    public async Task StageHunkAsync(
        string repoPath, string patchContent, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        if (string.IsNullOrEmpty(patchContent))
            throw new ArgumentException("Patch content must not be empty.", nameof(patchContent));

        var result = await GitCmd()
            .WithArguments(args =>
            {
                args.Add("apply").Add("--cached");
                AddUnidiffZeroIfRequired(args, patchContent);
            })
            .WithStandardInputPipe(PipeSource.FromString(patchContent))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git apply --cached failed", result.ExitCode, result.StandardError);
    }

    /// <summary>
    /// Adds <c>--unidiff-zero</c> only when the patch genuinely has no context lines.
    ///
    /// That flag disables git's context verification, which is the one thing stopping a
    /// hunk from being applied at the wrong offset when the file changed between the
    /// diff being rendered and the user clicking stage (an editor autosave or
    /// format-on-save is enough). It is required for zero-context patches — git cannot
    /// verify what isn't there — but passing it unconditionally throws the safety check
    /// away for every normal patch too, turning a clean rejection into silent index
    /// corruption.
    /// </summary>
    private static void AddUnidiffZeroIfRequired(
        CliWrap.Builders.ArgumentsBuilder args, string patchContent)
    {
        if (!HasContextLines(patchContent))
            args.Add("--unidiff-zero");
    }

    /// <summary>
    /// True when the patch body contains at least one context line, i.e. a line
    /// starting with a space. Header lines ("--- ", "+++ ", "@@ ", "diff ", "index ")
    /// are skipped so they cannot be mistaken for content.
    /// </summary>
    private static bool HasContextLines(string patchContent)
    {
        foreach (var line in patchContent.Split('\n'))
        {
            if (line.Length == 0) continue;
            if (line.StartsWith("--- ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("+++ ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("@@", StringComparison.Ordinal)) continue;
            if (line.StartsWith("diff ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("index ", StringComparison.Ordinal)) continue;
            if (line.StartsWith("new file", StringComparison.Ordinal)) continue;
            if (line.StartsWith("deleted file", StringComparison.Ordinal)) continue;

            if (line[0] == ' ') return true;
        }

        return false;
    }

    public async Task UnstageHunkAsync(
        string repoPath, string patchContent, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        if (string.IsNullOrEmpty(patchContent))
            throw new ArgumentException("Patch content must not be empty.", nameof(patchContent));

        var result = await GitCmd()
            .WithArguments(args =>
            {
                args.Add("apply").Add("--cached").Add("--reverse");
                AddUnidiffZeroIfRequired(args, patchContent);
            })
            .WithStandardInputPipe(PipeSource.FromString(patchContent))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git apply --cached --reverse failed", result.ExitCode, result.StandardError);
    }

    public async Task IntentToAddAsync(
        string repoPath, string filePath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateFilePath(repoPath, filePath);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("add")
                .Add("-N")
                .Add("--")
                .Add(filePath))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git add -N failed", result.ExitCode, result.StandardError);
    }

    // -------------------------------------------------------------------------
    // Undo / Revert
    // -------------------------------------------------------------------------

    public async Task UndoLastCommitAsync(
        string repoPath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("reset")
                .Add("--soft")
                .Add("HEAD~1"))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git reset --soft HEAD~1 failed", result.ExitCode, result.StandardError);
    }

    public async Task RevertCommitAsync(
        string repoPath, string commitHash, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateHash(commitHash, nameof(commitHash));

        var parentCount = await GetParentCountAsync(repoPath, commitHash, ct);

        var result = await GitCmd()
            .WithArguments(args =>
            {
                args.Add("revert").Add("--no-edit");
                if (parentCount > 1)
                    args.Add("-m").Add("1");
                args.Add(commitHash);
            })
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git revert failed", result.ExitCode, result.StandardError);
    }

    public async Task<int> GetParentCountAsync(
        string repoPath, string commitHash, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateHash(commitHash, nameof(commitHash));

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("rev-list")
                .Add("--parents")
                .Add("-1")
                .Add(commitHash))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git rev-list --parents failed", result.ExitCode, result.StandardError);

        // Output is: <hash> <parent1> <parent2> ...
        var parts = result.StandardOutput.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length - 1; // subtract 1 for the commit itself
    }

    public async Task<bool> IsWorkingTreeCleanAsync(
        string repoPath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("status")
                .Add("--porcelain=v2"))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git status failed", result.ExitCode, result.StandardError);

        return string.IsNullOrWhiteSpace(result.StandardOutput);
    }

    // -------------------------------------------------------------------------
    // Tag management
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<TagInfo>> GetTagsAsync(
        string repoPath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);

        const string format = "%(refname:short)%x00%(objectname:short)%x00%(creatordate:iso)%x00%(subject)%x1E";

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("tag")
                .Add("--list")
                .Add($"--format={format}"))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git tag --list failed", result.ExitCode, result.StandardError);

        var tags = new List<TagInfo>();
        var records = result.StandardOutput.Split('\x1E', StringSplitOptions.RemoveEmptyEntries);

        foreach (var record in records)
        {
            var trimmed = record.Trim('\n', '\r');
            if (string.IsNullOrEmpty(trimmed))
                continue;

            var fields = trimmed.Split('\x00');
            if (fields.Length < 4)
                continue;

            var name = fields[0];
            var shortHash = fields[1];

            if (!DateTimeOffset.TryParse(fields[2], out var createdDate))
                createdDate = DateTimeOffset.MinValue;

            var message = fields.Length == 4
                ? fields[3]
                : string.Join('\x00', fields[3..]);

            tags.Add(new TagInfo(name, shortHash, createdDate, message));
        }

        return tags;
    }

    public async Task CreateTagAsync(
        string repoPath, string tagName, string? message = null, string? commitHash = null, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);

        if (string.IsNullOrWhiteSpace(tagName))
            throw new ArgumentException("Tag name must not be empty.", nameof(tagName));
        if (tagName.StartsWith('-'))
            throw new ArgumentException($"Tag name must not start with '-': '{tagName}'", nameof(tagName));
        if (!BranchNamePattern.IsMatch(tagName))
            throw new ArgumentException($"Invalid tag name: '{tagName}'", nameof(tagName));

        if (!string.IsNullOrEmpty(commitHash))
            ValidateHash(commitHash, nameof(commitHash));

        var result = await GitCmd()
            .WithArguments(args =>
            {
                args.Add("tag");
                if (!string.IsNullOrWhiteSpace(message))
                    args.Add("-a").Add(tagName).Add("-m").Add(message);
                else
                    args.Add(tagName);
                if (!string.IsNullOrEmpty(commitHash))
                    args.Add(commitHash);
            })
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git tag failed", result.ExitCode, result.StandardError);
    }

    public async Task DeleteTagAsync(
        string repoPath, string tagName, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);

        if (string.IsNullOrWhiteSpace(tagName))
            throw new ArgumentException("Tag name must not be empty.", nameof(tagName));
        if (tagName.StartsWith('-'))
            throw new ArgumentException($"Tag name must not start with '-': '{tagName}'", nameof(tagName));
        if (!BranchNamePattern.IsMatch(tagName))
            throw new ArgumentException($"Invalid tag name: '{tagName}'", nameof(tagName));

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("tag")
                .Add("-d")
                .Add(tagName))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git tag -d failed", result.ExitCode, result.StandardError);
    }

    public async Task PushTagAsync(
        string repoPath, string tagName, string remote = "origin", CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateRemote(remote);

        if (string.IsNullOrWhiteSpace(tagName))
            throw new ArgumentException("Tag name must not be empty.", nameof(tagName));
        if (tagName.StartsWith('-'))
            throw new ArgumentException($"Tag name must not start with '-': '{tagName}'", nameof(tagName));
        if (!BranchNamePattern.IsMatch(tagName))
            throw new ArgumentException($"Invalid tag name: '{tagName}'", nameof(tagName));

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("push")
                .Add(remote)
                .Add(tagName))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git push (tag) failed", result.ExitCode, result.StandardError);
    }

    /// <summary>
    /// Hashes of commits that exist on a local branch but on no remote-tracking branch.
    ///
    /// Uses <c>--branches --not --remotes</c> rather than <c>@{u}..HEAD</c> because the
    /// graph shows every branch at once: an answer scoped to the checked-out branch
    /// would mislabel rows belonging to the others. A repository with no remotes has
    /// nothing to compare against, so every commit comes back unpushed — callers that
    /// render this should suppress the indicator when there is no remote rather than
    /// flagging the entire history.
    /// </summary>
    public async Task<IReadOnlySet<string>> GetUnpushedCommitsAsync(
        string repoPath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("rev-list")
                .Add("--branches")
                .Add("--not")
                .Add("--remotes"))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        var unpushed = new HashSet<string>(StringComparer.Ordinal);

        // Degrade to "everything looks pushed" rather than failing the repo load. This
        // is a decoration on the graph, not something worth blocking a repo open over.
        if (result.ExitCode != 0)
            return unpushed;

        foreach (var line in result.StandardOutput.Split('\n'))
        {
            var hash = line.Trim();
            if (hash.Length > 0)
                unpushed.Add(hash);
        }

        return unpushed;
    }

    // -------------------------------------------------------------------------
    // Blame
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<BlameLine>> GetBlameAsync(
        string repoPath, string filePath, string? commitHash = null, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateFilePath(repoPath, filePath);

        if (!string.IsNullOrEmpty(commitHash))
            ValidateHash(commitHash, nameof(commitHash));

        var result = await GitCmd()
            .WithArguments(args =>
            {
                // --no-textconv: blame otherwise runs the repo's textconv driver
                args.Add("blame").Add("--porcelain").Add("--no-textconv");
                if (!string.IsNullOrEmpty(commitHash))
                    args.Add(commitHash);
                args.Add("--").Add(filePath);
            })
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git blame failed", result.ExitCode, result.StandardError);

        return ParseBlameOutput(result.StandardOutput);
    }

    private static IReadOnlyList<BlameLine> ParseBlameOutput(string output)
    {
        if (string.IsNullOrEmpty(output))
            return [];

        var lines = output.Split('\n');
        var blameLines = new List<BlameLine>();

        string currentHash = string.Empty;
        int currentLineNumber = 0;
        string authorName = string.Empty;
        long authorTime = 0;
        string authorTz = string.Empty;
        string summary = string.Empty;

        // Track per-commit metadata so we don't lose it on subsequent lines
        // that reference the same commit (porcelain only emits full metadata
        // on the first occurrence of each commit).
        var commitMeta = new Dictionary<string, (string Author, long Time, string Tz, string Summary)>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Porcelain header: <hash> <orig-line> <final-line> [<num-lines>]
            if (line.Length >= 40 && HexHashPattern.IsMatch(line[..40].Trim()))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3)
                {
                    currentHash = parts[0];
                    if (int.TryParse(parts[2], out var ln))
                        currentLineNumber = ln;

                    // Reset metadata — will be populated from subsequent lines or cache
                    authorName = string.Empty;
                    authorTime = 0;
                    authorTz = string.Empty;
                    summary = string.Empty;
                }
            }
            else if (line.StartsWith("author "))
            {
                authorName = line["author ".Length..];
                if (!string.IsNullOrEmpty(currentHash))
                    UpdateCommitMeta(commitMeta, currentHash, author: authorName);
            }
            else if (line.StartsWith("author-time "))
            {
                if (long.TryParse(line["author-time ".Length..], out var t))
                {
                    authorTime = t;
                    if (!string.IsNullOrEmpty(currentHash))
                        UpdateCommitMeta(commitMeta, currentHash, time: authorTime);
                }
            }
            else if (line.StartsWith("author-tz "))
            {
                authorTz = line["author-tz ".Length..];
                if (!string.IsNullOrEmpty(currentHash))
                    UpdateCommitMeta(commitMeta, currentHash, tz: authorTz);
            }
            else if (line.StartsWith("summary "))
            {
                summary = line["summary ".Length..];
                if (!string.IsNullOrEmpty(currentHash))
                    UpdateCommitMeta(commitMeta, currentHash, summary: summary);
            }
            else if (line.StartsWith("\t"))
            {
                // Content line — this completes the current blame entry
                var text = line[1..]; // strip leading tab

                // Resolve metadata: prefer inline values, fall back to cache
                var resolvedAuthor = authorName;
                var resolvedTime = authorTime;
                var resolvedTz = authorTz;
                var resolvedSummary = summary;

                if (!string.IsNullOrEmpty(currentHash) && commitMeta.TryGetValue(currentHash, out var cached))
                {
                    if (string.IsNullOrEmpty(resolvedAuthor)) resolvedAuthor = cached.Author;
                    if (resolvedTime == 0) resolvedTime = cached.Time;
                    if (string.IsNullOrEmpty(resolvedTz)) resolvedTz = cached.Tz;
                    if (string.IsNullOrEmpty(resolvedSummary)) resolvedSummary = cached.Summary;
                }

                var authorDate = DateTimeOffset.FromUnixTimeSeconds(resolvedTime);
                if (!string.IsNullOrEmpty(resolvedTz) && resolvedTz.Length >= 5)
                {
                    // Parse timezone offset like "+0200" or "-0530"
                    var sign = resolvedTz[0] == '-' ? -1 : 1;
                    if (int.TryParse(resolvedTz[1..3], out var hours) && int.TryParse(resolvedTz[3..5], out var minutes))
                        authorDate = authorDate.ToOffset(new TimeSpan(sign * hours, sign * minutes, 0));
                }

                blameLines.Add(new BlameLine(currentLineNumber, text, currentHash, resolvedAuthor, authorDate, resolvedSummary));
            }
        }

        return blameLines;
    }

    private static void UpdateCommitMeta(
        Dictionary<string, (string Author, long Time, string Tz, string Summary)> meta,
        string hash,
        string? author = null, long? time = null, string? tz = null, string? summary = null)
    {
        if (!meta.TryGetValue(hash, out var existing))
            existing = (string.Empty, 0, string.Empty, string.Empty);

        meta[hash] = (
            author ?? existing.Author,
            time ?? existing.Time,
            tz ?? existing.Tz,
            summary ?? existing.Summary
        );
    }

    // -------------------------------------------------------------------------
    // File history
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<CommitNode>> GetFileHistoryAsync(
        string repoPath, string filePath, int maxCount = 100, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateFilePath(repoPath, filePath);

        if (maxCount < 1)
            throw new ArgumentOutOfRangeException(nameof(maxCount), "maxCount must be at least 1.");

        const string format = "%H%x00%P%x00%an%x00%ae%x00%ai%x00%D%x00%s%x1E";

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("log")
                .Add("--follow")
                .Add($"-{maxCount}")
                .Add($"--format={format}")
                .Add("--")
                .Add(filePath))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git log --follow failed", result.ExitCode, result.StandardError);

        return ParseCommitGraph(result.StandardOutput);
    }

    // -------------------------------------------------------------------------
    // Search
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<CommitNode>> SearchCommitsAsync(
        string repoPath, string? query = null, string? author = null, int maxCount = 200, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);

        if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(author))
            throw new ArgumentException("At least one of query or author must be provided.");

        if (maxCount < 1)
            throw new ArgumentOutOfRangeException(nameof(maxCount), "maxCount must be at least 1.");

        const string format = "%H%x00%P%x00%an%x00%ae%x00%ai%x00%D%x00%s%x1E";

        var result = await GitCmd()
            .WithArguments(args =>
            {
                args.Add("log")
                    .Add("--all")
                    .Add($"-{maxCount}")
                    .Add($"--format={format}");
                if (!string.IsNullOrWhiteSpace(query))
                    args.Add($"--grep={query}");
                if (!string.IsNullOrWhiteSpace(author))
                    args.Add($"--author={author}");
            })
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git log (search) failed", result.ExitCode, result.StandardError);

        return ParseCommitGraph(result.StandardOutput);
    }

    // -------------------------------------------------------------------------
    // Conflict resolution
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<ConflictedFile>> GetConflictedFilesAsync(
        string repoPath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("status")
                .Add("--porcelain=v2")
                .Add("-z"))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git status failed", result.ExitCode, result.StandardError);

        var conflicts = new List<ConflictedFile>();
        var tokens = result.StandardOutput.Split('\0');

        foreach (var entry in tokens)
        {
            if (string.IsNullOrEmpty(entry) || !entry.StartsWith("u "))
                continue;

            // Format: u XY sub m1 m2 m3 mW h1 h2 h3 path
            var parts = entry.Split(' ', 11);
            if (parts.Length >= 11)
            {
                var xy = parts[1];
                var path = parts[10];
                conflicts.Add(new ConflictedFile(path, xy));
            }
        }

        return conflicts;
    }

    public async Task<string> GetConflictVersionAsync(
        string repoPath, string filePath, ConflictSide side, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateFilePath(repoPath, filePath);

        // :1: = base, :2: = ours, :3: = theirs
        var stageNumber = side switch
        {
            ConflictSide.Base => "1",
            ConflictSide.Ours => "2",
            ConflictSide.Theirs => "3",
            _ => throw new ArgumentOutOfRangeException(nameof(side))
        };

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("show")
                .Add($":{stageNumber}:{filePath}"))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            return string.Empty; // Version may not exist (e.g., file added on one side only)

        return result.StandardOutput;
    }

    public async Task ResolveConflictWithSideAsync(
        string repoPath, string filePath, ConflictSide side, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateFilePath(repoPath, filePath);

        if (side == ConflictSide.Base)
            throw new ArgumentException("Cannot resolve conflict with Base — use Ours or Theirs.", nameof(side));

        var sideArg = side == ConflictSide.Ours ? "--ours" : "--theirs";

        var checkoutResult = await GitCmd()
            .WithArguments(args => args
                .Add("checkout")
                .Add(sideArg)
                .Add("--")
                .Add(filePath))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (checkoutResult.ExitCode != 0)
            throw new GitException($"git checkout {sideArg} failed", checkoutResult.ExitCode, checkoutResult.StandardError);

        // Stage the resolved file
        await StageFileAsync(repoPath, filePath, ct);
    }

    public async Task MarkConflictResolvedAsync(
        string repoPath, string filePath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateFilePath(repoPath, filePath);

        await StageFileAsync(repoPath, filePath, ct);
    }

    public async Task AbortMergeAsync(
        string repoPath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("merge")
                .Add("--abort"))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git merge --abort failed", result.ExitCode, result.StandardError);
    }

    // -------------------------------------------------------------------------
    // Interactive Rebase
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<RebaseEntry>> GetRebaseCommitsAsync(
        string repoPath, string ontoCommit, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateHash(ontoCommit, nameof(ontoCommit));

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("log")
                .Add("--reverse")
                .Add("--format=%H %s")
                .Add($"{ontoCommit}..HEAD"))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git log (rebase commits) failed", result.ExitCode, result.StandardError);

        var entries = new List<RebaseEntry>();
        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 41) continue; // at least 40-char hash + space
            var hash = trimmed[..40];
            var subject = trimmed.Length > 41 ? trimmed[41..] : string.Empty;
            entries.Add(new RebaseEntry(hash, subject));
        }

        return entries;
    }

    public async Task ExecuteRebaseAsync(
        string repoPath, string ontoCommit, IReadOnlyList<RebaseAction> actions,
        CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateHash(ontoCommit, nameof(ontoCommit));

        if (actions.Count == 0)
            throw new ArgumentException("At least one rebase action must be provided.", nameof(actions));

        // Build the todo list content
        var todoLines = new List<string>();
        foreach (var action in actions)
        {
            var verb = action.Type switch
            {
                RebaseActionType.Pick => "pick",
                RebaseActionType.Reword => "reword",
                RebaseActionType.Squash => "squash",
                RebaseActionType.Fixup => "fixup",
                RebaseActionType.Drop => "drop",
                RebaseActionType.Edit => "edit",
                _ => "pick"
            };

            // The todo file is line-oriented, so a newline inside a subject would
            // inject an extra instruction line. git's %s normally folds newlines to
            // spaces, but don't rely on the producer staying that way — strip here,
            // where the injection would actually happen.
            var safeSubject = action.Subject
                .Replace('\r', ' ')
                .Replace('\n', ' ');

            // Defence in depth: the hash comes from GetRebaseCommitsAsync today, but
            // this method is public and must not trust its caller's hashes.
            ValidateHash(action.Hash, nameof(actions));

            todoLines.Add($"{verb} {action.Hash} {safeSubject}");
        }
        var todoContent = string.Join("\n", todoLines) + "\n";

        var todoFile = Path.Combine(Path.GetTempPath(), $"grumpygit-rebase-todo-{Guid.NewGuid():N}.txt");

        try
        {
            await File.WriteAllTextAsync(todoFile, todoContent, ct);

            // Git runs GIT_SEQUENCE_EDITOR through sh and appends the todo path as an
            // argument, so the whole editor is one copy command overwriting git's todo
            // with ours.
            //
            // It used to generate a .cmd doing `copy /Y "<todo>" %1`, which never worked:
            // sh cannot execute a .cmd, so it read the file as a shell script, choked on
            // `@copy`, and git aborted the rebase with "there was a problem with the
            // editor". Quoting %1 does not help — nothing in that path is cmd.
            //
            // Forward slashes and the quotes both matter: sh treats backslashes as escapes,
            // and the temp path contains a space whenever the account name does.
            var editor = $"cp \"{todoFile.Replace('\\', '/')}\"";

            var result = await GitCmd()
                .WithArguments(args => args
                    .Add("rebase")
                    .Add("-i")
                    .Add(ontoCommit))
                .WithEnvironmentVariables(env => env
                    .Set("GIT_SEQUENCE_EDITOR", editor))
                .WithWorkingDirectory(repoPath)
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync(ct);

            if (result.ExitCode != 0)
            {
                // Check if rebase is paused (conflict or edit) — that's not a fatal error
                var isInProgress = await IsRebaseInProgressAsync(repoPath, ct);
                if (!isInProgress)
                    throw new GitException("git rebase -i failed", result.ExitCode, result.StandardError);
            }
        }
        finally
        {
            try { File.Delete(todoFile); } catch { }
        }
    }

    public async Task ContinueRebaseAsync(
        string repoPath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("rebase")
                .Add("--continue"))
            .WithEnvironmentVariables(env => env
                .Set("GIT_EDITOR", "true"))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
        {
            var isInProgress = await IsRebaseInProgressAsync(repoPath, ct);
            if (!isInProgress)
                throw new GitException("git rebase --continue failed", result.ExitCode, result.StandardError);
        }
    }

    public async Task AbortRebaseAsync(
        string repoPath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("rebase")
                .Add("--abort"))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git rebase --abort failed", result.ExitCode, result.StandardError);
    }

    public async Task SkipRebaseAsync(
        string repoPath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("rebase")
                .Add("--skip"))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
        {
            var isInProgress = await IsRebaseInProgressAsync(repoPath, ct);
            if (!isInProgress)
                throw new GitException("git rebase --skip failed", result.ExitCode, result.StandardError);
        }
    }

    public Task<bool> IsRebaseInProgressAsync(
        string repoPath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);

        var rebaseMerge = Path.Combine(repoPath, ".git", "rebase-merge");
        var rebaseApply = Path.Combine(repoPath, ".git", "rebase-apply");

        return Task.FromResult(Directory.Exists(rebaseMerge) || Directory.Exists(rebaseApply));
    }

    // -------------------------------------------------------------------------
    // Worktrees
    //
    // A worktree here is always bound to one branch. Git enforces half of that on
    // its own — it refuses to check the same branch out twice — but it has no
    // mechanism to stop `git switch` inside a worktree afterwards. That second half
    // is enforced in this class: CheckoutBranchAsync and CreateBranchAsync both
    // refuse when the target path is a linked worktree, so no caller in the app can
    // move a worktree off the branch it was created for. The UI reflects the same
    // rule, but the guard lives here so it cannot be bypassed by a new call site.
    // -------------------------------------------------------------------------

    private static void ValidateWorktreePath(string worktreePath)
    {
        if (string.IsNullOrWhiteSpace(worktreePath))
            throw new ArgumentException("Worktree path must not be empty.", nameof(worktreePath));
        if (worktreePath.StartsWith('-'))
            throw new ArgumentException($"Worktree path must not start with '-': '{worktreePath}'", nameof(worktreePath));
        if (!Path.IsPathRooted(worktreePath))
            throw new ArgumentException($"Worktree path must be an absolute path: '{worktreePath}'", nameof(worktreePath));
    }

    /// <summary>A start point may be a branch name or a commit hash; nothing else.</summary>
    private static void ValidateStartPoint(string startPoint)
    {
        if (string.IsNullOrWhiteSpace(startPoint))
            throw new ArgumentException("Start point must not be empty.", nameof(startPoint));
        if (startPoint.StartsWith('-'))
            throw new ArgumentException($"Start point must not start with '-': '{startPoint}'", nameof(startPoint));
        if (!BranchNamePattern.IsMatch(startPoint) && !HexHashPattern.IsMatch(startPoint))
            throw new ArgumentException($"Invalid start point: '{startPoint}'", nameof(startPoint));
    }

    public async Task<IReadOnlyList<GitWorktree>> GetWorktreesAsync(
        string repoPath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("worktree")
                .Add("list")
                .Add("--porcelain"))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git worktree list failed", result.ExitCode, result.StandardError);

        return WorktreeListParser.Parse(result.StandardOutput);
    }

    /// <summary>
    /// True when <paramref name="repoPath"/> is a linked worktree rather than the
    /// repository's main working directory.
    ///
    /// Compares the worktree-specific git dir against the shared one: they are the same
    /// directory in the main worktree and differ (…/worktrees/&lt;name&gt;) in a linked one.
    /// <c>--path-format=absolute</c> keeps the two comparable — without it git answers
    /// one relatively and one absolutely.
    /// </summary>
    public async Task<bool> IsLinkedWorktreeAsync(
        string repoPath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("rev-parse")
                .Add("--path-format=absolute")
                .Add("--git-dir")
                .Add("--git-common-dir"))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        // Not a repository, or a git too old for --path-format: treat as "not linked"
        // rather than failing. The caller uses this to decide whether to lock branch
        // switching, and refusing to answer would lock a perfectly normal repo.
        if (result.ExitCode != 0)
            return false;

        var lines = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count < 2)
            return false;

        return !PathsEqual(lines[0], lines[1]);
    }

    private static bool PathsEqual(string a, string b)
    {
        static string Normalise(string p) =>
            Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(p.Replace('/', Path.DirectorySeparatorChar)));

        try
        {
            return string.Equals(Normalise(a), Normalise(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Creates a worktree at <paramref name="worktreePath"/> holding
    /// <paramref name="branchName"/>. With <paramref name="createBranch"/> the branch is
    /// created from <paramref name="startPoint"/> (HEAD when null); otherwise the branch
    /// must already exist and must not be checked out anywhere else.
    /// </summary>
    public async Task AddWorktreeAsync(
        string repoPath,
        string worktreePath,
        string branchName,
        bool createBranch = false,
        string? startPoint = null,
        CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateWorktreePath(worktreePath);
        ValidateBranch(branchName);
        if (startPoint is not null)
            ValidateStartPoint(startPoint);

        // git creates the leaf directory itself but not missing parents, and its error
        // for a missing parent is far less clear than this one.
        var parent = Path.GetDirectoryName(Path.GetFullPath(worktreePath));
        if (parent is not null && !Directory.Exists(parent))
            Directory.CreateDirectory(parent);

        if (Directory.Exists(worktreePath) &&
            Directory.EnumerateFileSystemEntries(worktreePath).Any())
        {
            throw new ArgumentException(
                $"Worktree path already exists and is not empty: '{worktreePath}'", nameof(worktreePath));
        }

        var result = await GitCmd()
            .WithArguments(args =>
            {
                args.Add("worktree").Add("add");

                if (createBranch)
                {
                    // -b makes the branch and checks it out into the new worktree in one step.
                    args.Add("-b").Add(branchName).Add(worktreePath);
                    if (startPoint is not null)
                        args.Add(startPoint);
                }
                else
                {
                    args.Add(worktreePath).Add(branchName);
                }
            })
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git worktree add failed", result.ExitCode, result.StandardError);
    }

    /// <summary>
    /// Removes the worktree at the given path. Refuses to remove the main worktree —
    /// that would be a request to delete the repository's own working directory.
    /// </summary>
    public async Task RemoveWorktreeAsync(
        string repoPath, string worktreePath, bool force = false, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateWorktreePath(worktreePath);

        var worktrees = await GetWorktreesAsync(repoPath, ct);
        var target = worktrees.FirstOrDefault(w => PathsEqual(w.Path, worktreePath))
            ?? throw new ArgumentException(
                $"No worktree registered at '{worktreePath}'.", nameof(worktreePath));

        if (target.IsMain)
            throw new InvalidOperationException(
                "The main working directory cannot be removed as a worktree.");

        var result = await GitCmd()
            .WithArguments(args =>
            {
                args.Add("worktree").Add("remove");
                if (force) args.Add("--force");
                args.Add(worktreePath);
            })
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git worktree remove failed", result.ExitCode, result.StandardError);
    }

    /// <summary>
    /// Removes the worktree holding <paramref name="branchName"/>. Worktrees are created
    /// per branch, so the branch is the identifier a user actually reasons about; this
    /// spares callers from tracking paths.
    /// </summary>
    public async Task RemoveWorktreeForBranchAsync(
        string repoPath, string branchName, bool force = false, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateBranch(branchName);

        var worktrees = await GetWorktreesAsync(repoPath, ct);
        var target = worktrees.FirstOrDefault(w =>
                         w.IsLinked &&
                         string.Equals(w.Branch, branchName, StringComparison.Ordinal))
            ?? throw new ArgumentException(
                $"No worktree is checked out to branch '{branchName}'.", nameof(branchName));

        await RemoveWorktreeAsync(repoPath, target.Path, force, ct);
    }

    /// <summary>
    /// Drops administrative entries for worktrees whose directories have gone missing.
    /// </summary>
    public async Task PruneWorktreesAsync(string repoPath, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("worktree")
                .Add("prune"))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git worktree prune failed", result.ExitCode, result.StandardError);
    }

    /// <summary>
    /// Throws when <paramref name="repoPath"/> is a linked worktree. Guards every code
    /// path that would move a worktree off the branch it was created for.
    /// </summary>
    private async Task EnsureNotLinkedWorktreeAsync(
        string repoPath, string attemptedBranch, CancellationToken ct)
    {
        if (!await IsLinkedWorktreeAsync(repoPath, ct))
            return;

        throw new InvalidOperationException(
            $"This worktree is bound to its branch and cannot switch to '{attemptedBranch}'. " +
            "Create a worktree for that branch instead, or use the repository's main working directory.");
    }

    // -------------------------------------------------------------------------
    // Pull request preview
    //
    // Everything here is read-only with respect to the checkout: no branch is switched,
    // no index is touched, and the working tree is never written. That is the whole
    // point — reviewing your own branch against a target must not disturb what you are
    // in the middle of.
    // -------------------------------------------------------------------------

    public async Task<string> GetBranchHeadAsync(
        string repoPath, string branch, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateBranch(branch);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("rev-parse")
                .Add("--verify")
                // --end-of-options stops git reading a later argument as a flag, which is
                // the remaining way a ref name could act as one after ValidateBranch.
                .Add("--end-of-options")
                .Add(branch))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException($"git rev-parse failed for '{branch}'", result.ExitCode, result.StandardError);

        return result.StandardOutput.Trim();
    }

    /// <summary>
    /// Common ancestor of two branches, or an empty string when they share no history.
    /// </summary>
    public async Task<string> GetMergeBaseAsync(
        string repoPath, string branchA, string branchB, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateBranch(branchA);
        ValidateBranch(branchB);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("merge-base")
                .Add("--end-of-options")
                .Add(branchA)
                .Add(branchB))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        // Exit 1 means "no common ancestor" — unrelated histories, which the caller
        // reports rather than treats as a failure.
        if (result.ExitCode == 1)
            return string.Empty;

        if (result.ExitCode != 0)
            throw new GitException("git merge-base failed", result.ExitCode, result.StandardError);

        return result.StandardOutput.Trim();
    }

    /// <summary>Commits reachable from <paramref name="toHash"/> but not <paramref name="fromHash"/>, newest first.</summary>
    public async Task<IReadOnlyList<CommitNode>> GetCommitsInRangeAsync(
        string repoPath, string fromHash, string toHash, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateHash(fromHash, nameof(fromHash));
        ValidateHash(toHash, nameof(toHash));

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("log")
                .Add($"--format={CommitGraphFormat}")
                // Both sides are validated hex, so the range is built from values that
                // cannot carry a flag or a shell metacharacter.
                .Add($"{fromHash}..{toHash}"))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        if (result.ExitCode != 0)
            throw new GitException("git log (range) failed", result.ExitCode, result.StandardError);

        return ParseCommitGraph(result.StandardOutput);
    }

    /// <summary>
    /// Merges <paramref name="sourceBranch"/> into <paramref name="targetBranch"/> in
    /// memory and reports what would conflict.
    ///
    /// <c>--write-tree</c> does add the merged result to the object database — unreachable
    /// objects that git's own gc collects — but it is the only way to get a real merge
    /// verdict without a checkout. The alternative, actually merging and rolling back,
    /// would trample the user's working tree to answer a question.
    /// </summary>
    public async Task<MergePreview> PreviewMergeAsync(
        string repoPath, string targetBranch, string sourceBranch, CancellationToken ct = default)
    {
        ValidateRepoPath(repoPath);
        ValidateBranch(targetBranch);
        ValidateBranch(sourceBranch);

        var result = await GitCmd()
            .WithArguments(args => args
                .Add("merge-tree")
                .Add("--write-tree")
                .Add("--name-only")
                .Add("-z")
                .Add("--end-of-options")
                .Add(targetBranch)
                .Add(sourceBranch))
            .WithWorkingDirectory(repoPath)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        return MergeTreeParser.Parse(result.StandardOutput, result.ExitCode);
    }

    // -------------------------------------------------------------------------
    // NOTE: RunCommandAsync (arbitrary git argument passthrough) was removed.
    //
    // It bypassed every validation helper in this class and naively split its
    // argument string on spaces, so any future caller wiring user text into it —
    // a command palette, an alias feature — would have handed over unrestricted
    // git argument injection (`-c core.fsmonitor=...`, `--upload-pack=...`).
    // Its only caller now uses GetCommitRangeFileListAsync, which validates both
    // hashes and passes discrete argv entries.
    //
    // If arbitrary git invocation is genuinely needed again, add it as a method
    // with an explicit subcommand allow-list that rejects any token starting
    // with '-', rather than restoring this.
    // -------------------------------------------------------------------------
}
