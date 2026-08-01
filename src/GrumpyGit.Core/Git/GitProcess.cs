using CliWrap;

namespace GrumpyGit.Core.Git;

/// <summary>
/// The single place this application is allowed to spawn <c>git.exe</c> from.
///
/// Git honours a <em>repository-local</em> <c>.git/config</c>, which means an untrusted
/// clone can nominate commands for git to run on the user's behalf. For a visual git
/// client that is a passive-browsing RCE vector — no action beyond opening the repo or
/// clicking a file is needed:
/// <list type="bullet">
///   <item><c>diff.external</c> / <c>diff.&lt;driver&gt;.textconv</c> — runs when any diff is displayed</item>
///   <item><c>core.fsmonitor</c> — runs on <c>git status</c>, i.e. simply opening a repo tab</item>
///   <item><c>core.pager</c> — runs on any command git considers paged</item>
/// </list>
///
/// <see cref="Start"/> forces each of those to a harmless value on every invocation.
/// The overrides travel as GIT_CONFIG_COUNT/KEY/VALUE (git ≥ 2.31) rather than <c>-c</c>
/// flags so they apply uniformly without every call site having to order arguments
/// correctly — a rule that would eventually be forgotten across 50 call sites.
///
/// Note this cannot cover per-path <c>textconv</c> drivers selected through
/// <c>.gitattributes</c>, because those are keyed by driver name and cannot be disabled
/// by a single config key. Diff-family commands therefore also pass
/// <c>--no-ext-diff</c> / <c>--no-textconv</c> explicitly.
///
/// Deliberately NOT overridden: <c>core.hooksPath</c> and hook execution generally.
/// Hooks run only on explicit user actions (commit, checkout, merge, rebase) and
/// suppressing them would make this client silently diverge from the git CLI.
/// </summary>
public static class GitProcess
{
    private static readonly IReadOnlyDictionary<string, string?> HardenedEnv =
        new Dictionary<string, string?>
        {
            ["GIT_CONFIG_COUNT"] = "3",
            ["GIT_CONFIG_KEY_0"] = "diff.external",
            ["GIT_CONFIG_VALUE_0"] = "",
            ["GIT_CONFIG_KEY_1"] = "core.fsmonitor",
            ["GIT_CONFIG_VALUE_1"] = "",
            ["GIT_CONFIG_KEY_2"] = "core.pager",
            ["GIT_CONFIG_VALUE_2"] = "cat",
        };

    /// <summary>
    /// As <see cref="HardenedEnv"/>, plus default language diff drivers so hunk headers
    /// carry the enclosing symbol. Kept separate from the base set, and applied only by
    /// <see cref="StartForDiff"/>, because gitattributes also govern <c>text</c>,
    /// <c>eol</c> and <c>filter</c>: forcing an attributes file onto <em>every</em>
    /// command could change how content is checked out or committed for a user who has
    /// a global attributes file of their own. Read-only diff rendering is the only place
    /// the benefit is worth that surface.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string?> DiffEnv = BuildDiffEnv();

    private static Dictionary<string, string?> BuildDiffEnv()
    {
        var env = new Dictionary<string, string?>(HardenedEnv);

        var attributes = GitDiffAttributes.Path;
        if (attributes is null)
            return env;   // Could not write the defaults; plain hardened env still works.

        env["GIT_CONFIG_COUNT"] = "4";
        env["GIT_CONFIG_KEY_3"] = "core.attributesFile";
        env["GIT_CONFIG_VALUE_3"] = attributes;
        return env;
    }

    /// <summary>
    /// Begins a hardened <c>git</c> command. Always use this rather than wrapping the
    /// git executable directly, so untrusted-repo hardening cannot be omitted.
    /// </summary>
    public static Command Start() =>
        Cli.Wrap("git").WithEnvironmentVariables(HardenedEnv);

    /// <summary>
    /// A hardened command that additionally supplies default language diff drivers.
    /// Use for diff-family commands whose hunk headers are read for symbol names; a
    /// repository's own <c>.gitattributes</c> still wins over these defaults.
    /// </summary>
    public static Command StartForDiff() =>
        Cli.Wrap("git").WithEnvironmentVariables(DiffEnv);
}
