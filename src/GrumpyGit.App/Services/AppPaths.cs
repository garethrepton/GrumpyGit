using System;
using System.IO;

namespace GrumpyGit.App.Services;

/// <summary>
/// Where the app keeps per-user data, and the one-time move from the old brand.
///
/// The product was renamed from "GrumpyGit" to "Grumpy". The data directory name
/// follows the brand, but the directory holds things the user cannot regenerate —
/// review notes they typed, which files they have already reviewed, their recent
/// and open repositories. Simply pointing at a new path would silently orphan all
/// of it and look like data loss, so the old directory is moved across on first
/// use.
/// </summary>
public static class AppPaths
{
    private const string CurrentFolderName = "Grumpy";
    private const string LegacyFolderName = "GrumpyGit";

    private static readonly Lazy<string> RootPath = new(ResolveRoot);

    /// <summary>%LOCALAPPDATA%\Grumpy — created on first access, migrating if needed.</summary>
    public static string Root => RootPath.Value;

    public static string ReviewStateDir => Path.Combine(Root, "review-state");

    public static string ReviewNotesDir => Path.Combine(Root, "review-notes");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>
    /// The working directory a CLI review module is started in, and it is deliberately an
    /// empty folder of ours rather than the repository being reviewed.
    ///
    /// Those agents read instructions out of the directory they start in — AGENTS.md,
    /// CLAUDE.md, .github/copilot-instructions.md, per-project settings — so starting one
    /// inside a clone from a stranger would hand that repository's text to a tool we
    /// launched on the user's behalf. Same shape of problem as git's repository-local
    /// diff.external, and the same answer: do not stand there in the first place.
    /// </summary>
    public static string AgentWorkDir => Path.Combine(Root, "agent");

    private static string ResolveRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var current = Path.Combine(localAppData, CurrentFolderName);
        var legacy = Path.Combine(localAppData, LegacyFolderName);

        TryMigrateLegacy(legacy, current);

        return current;
    }

    /// <summary>
    /// Moves the pre-rename directory across, once.
    ///
    /// Only runs when the new directory does not exist yet — if both are present the
    /// user has already been running the renamed build, and overwriting current data
    /// with stale data would be worse than leaving the old folder orphaned. Every
    /// failure is swallowed deliberately: losing the migration costs the user their
    /// review notes, but throwing here would stop the app starting at all.
    /// </summary>
    private static void TryMigrateLegacy(string legacy, string current)
    {
        try
        {
            if (Directory.Exists(current) || !Directory.Exists(legacy))
                return;

            Directory.Move(legacy, current);
        }
        catch
        {
            // A cross-volume move, a locked file, or a permissions problem. Fall back
            // to a copy so the data is at least reachable from the new location.
            try
            {
                if (!Directory.Exists(current) && Directory.Exists(legacy))
                    CopyDirectory(legacy, current);
            }
            catch
            {
                // Give up quietly — the app must still start with fresh defaults.
            }
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);

        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }
}
