using System.Text.RegularExpressions;
using CliWrap;

namespace GrumpyGit.Core.Agents;

/// <summary>
/// The single place this application is allowed to spawn a review agent from — the
/// <see cref="Git.GitProcess"/> of the module system, and hardened for the same reasons.
///
/// Two rules, both of which exist because the thing being handed to the child process is
/// the user's source code and the thing running the child is a git client pointed at
/// repositories it did not write.
///
/// <para><strong>1. A real executable image, never a shell.</strong> <see cref="Resolve"/>
/// searches PATH for <c>name.exe</c> and nothing else. It deliberately refuses the
/// <c>.cmd</c> and <c>.bat</c> shims that <c>npm install -g</c> leaves behind, because
/// Windows runs those through <c>cmd.exe</c>, and <c>cmd.exe</c> re-parses the command line
/// after CreateProcess has quoted it. A diff is full of <c>"</c>, <c>&amp;</c>, <c>|</c> and
/// <c>%</c>; passing one through that second parse is arbitrary command execution from
/// repository content, which is exactly what commandment 5 forbids. Escaping for cmd is a
/// known-hard problem with its own CVE, and the correct amount of it to write here is none.
/// Both vendors publish a native build, so the answer to a user with a shim is a sentence
/// telling them to install it.</para>
///
/// <para><strong>2. Never the user's repository as the working directory.</strong> These
/// agents read instruction files out of the directory they start in — <c>AGENTS.md</c>,
/// <c>CLAUDE.md</c>, <c>.github/copilot-instructions.md</c>, per-project settings. A clone
/// from a stranger carrying one of those would be handing prompt text to a tool we launched
/// on the user's behalf, which is the same shape of problem as git's repository-local
/// <c>diff.external</c>. So the child starts in an empty directory this application owns,
/// and the diff reaches it as a prompt rather than as a checkout.</para>
/// </summary>
public static class AgentProcess
{
    /// <summary>
    /// Executable extensions this will launch. Only images the kernel loads directly: no
    /// script, no shim, nothing that reaches an interpreter with a command line of its own.
    /// </summary>
    private static readonly string[] ExecutableExtensions = [".exe", ".com"];

    /// <summary>
    /// Extensions that exist but will not be used, so <see cref="Resolve"/> can tell a user
    /// "found, but not in a form I will run" instead of "not found".
    /// </summary>
    private static readonly string[] ShimExtensions = [".cmd", ".bat", ".ps1"];

    /// <summary>
    /// Full path to <paramref name="command"/> on PATH, or null.
    ///
    /// <paramref name="command"/> is always a bare constant from
    /// <see cref="ReviewModuleCatalogue"/> — never a value from settings, a repository or an
    /// environment variable — and the check below keeps it that way even if a later caller
    /// forgets: anything carrying a separator is refused rather than resolved.
    /// </summary>
    public static string? Resolve(string command) => ResolveIn(PathDirectories(), command);

    /// <summary>
    /// True when the command exists on PATH only as a script shim. Drives the one error
    /// message worth writing: the tool is installed, and this is what to do about it.
    /// </summary>
    public static bool ResolvesToShimOnly(string command) =>
        ResolvesToShimOnlyIn(PathDirectories(), command);

    /// <summary>
    /// <see cref="Resolve"/> over a given directory list — the whole of the policy, with the
    /// environment lifted out. Separate because "which extensions will this launch" is the
    /// rule worth testing, and reading PATH is not.
    /// </summary>
    public static string? ResolveIn(IEnumerable<string> directories, string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        RequireBareName(command);

        foreach (var directory in directories)
        {
            foreach (var extension in ExecutableExtensions)
            {
                var candidate = Path.Combine(directory, command + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    /// <summary><see cref="ResolvesToShimOnly"/> over a given directory list.</summary>
    public static bool ResolvesToShimOnlyIn(IEnumerable<string> directories, string command)
    {
        var list = directories as IReadOnlyCollection<string> ?? directories.ToList();

        if (ResolveIn(list, command) is not null)
            return false;

        return list.Any(directory =>
            ShimExtensions.Any(extension => File.Exists(Path.Combine(directory, command + extension))));
    }

    /// <summary>
    /// Refuses anything that is not a bare command name.
    ///
    /// Every name reaching here is a compile-time constant from
    /// <see cref="ReviewModuleCatalogue"/>, so this cannot fire today — it is here so that a
    /// future caller passing something from a settings file or a repository cannot turn PATH
    /// resolution into "launch this arbitrary path", which is the same guard
    /// <see cref="LocalModel.ModelStore"/> keeps over deletion.
    /// </summary>
    private static void RequireBareName(string command)
    {
        if (command.AsSpan().IndexOfAny("/\\:\"") >= 0)
            throw new ArgumentException("Only a bare command name may be resolved.", nameof(command));
    }

    private static IEnumerable<string> PathDirectories()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            yield break;

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = entry.Trim('"').Trim();
            if (trimmed.Length == 0)
                continue;

            // A malformed PATH entry is a bad character away from throwing inside
            // Path.Combine on every probe. Skipping it is the whole handling required.
            string full;
            try { full = Path.GetFullPath(trimmed); }
            catch { continue; }

            yield return full;
        }
    }

    /// <summary>
    /// Begins an agent command. Always use this rather than wrapping the executable
    /// directly, so the working-directory and environment hardening cannot be omitted.
    /// </summary>
    /// <param name="executablePath">A path from <see cref="Resolve"/>.</param>
    /// <param name="workingDirectory">
    /// An empty directory this application owns. Never the repository — see the type remarks.
    /// </param>
    public static Command Start(string executablePath, string workingDirectory) =>
        Cli.Wrap(executablePath)
            .WithWorkingDirectory(workingDirectory)
            .WithEnvironmentVariables(new Dictionary<string, string?>
            {
                // Escape sequences in the middle of a review are noise the parser then has
                // to strip. Both variables are honoured by the two CLIs and by most others.
                ["NO_COLOR"] = "1",
                ["FORCE_COLOR"] = "0",
                ["TERM"] = "dumb",

                // Nothing here is a terminal session the user is watching, so anything that
                // would try to open one, page output, or wait for a keypress is a hang.
                ["CI"] = "1",
                ["PAGER"] = "cat",
            })
            .WithValidation(CommandResultValidation.None);

    private static readonly Regex AnsiEscape = new(
        @"\x1B(?:[@-Z\\-_]|\[[0-?]*[ -/]*[@-~])", RegexOptions.Compiled);

    /// <summary>
    /// Strips terminal escape sequences from a reply. Belt and braces next to the
    /// environment above: a CLI that decides it is on a terminal anyway would otherwise
    /// feed cursor movements to a parser looking for "SUMMARY:".
    /// </summary>
    public static string Clean(string text) => AnsiEscape.Replace(text, string.Empty);
}
