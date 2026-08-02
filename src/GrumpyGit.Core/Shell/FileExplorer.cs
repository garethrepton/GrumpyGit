using System.Diagnostics;

namespace GrumpyGit.Core.Shell;

/// <summary>
/// The single place this application is allowed to hand a path to the Windows shell.
///
/// Deliberately spawns <c>explorer.exe</c> with the directory as an <em>argument</em>
/// rather than the obvious <c>UseShellExecute = true</c> on the path itself, so the image
/// this process launches is always explorer and never a handler chosen by whatever the
/// target turned out to be.
///
/// That alone does not settle it: explorer given a path to a <em>file</em> shell-executes
/// it — that is the documented way to launch a program de-elevated — so a directory
/// swapped for an executable between the check below and the launch would still run.
/// The argument therefore carries a trailing separator, which only a container can
/// resolve as.
///
/// Known limitation: explorer splits its own command line on commas, so a directory whose
/// name contains one browses to the wrong folder. Nothing is executed and no other path is
/// reachable, so this is left as a display quirk rather than engineered around.
/// </summary>
public static class FileExplorer
{
    /// <summary>
    /// Opens <paramref name="directory"/> in a new Explorer window.
    /// </summary>
    /// <exception cref="ArgumentException">Path is empty, relative, or not a directory.</exception>
    /// <exception cref="PlatformNotSupportedException">Not running on Windows.</exception>
    public static void OpenDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Opening a directory is supported on Windows only.");

        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Directory must not be empty.", nameof(directory));
        if (!Path.IsPathRooted(directory))
            throw new ArgumentException("Directory must be an absolute path.", nameof(directory));
        if (!Directory.Exists(directory))
            throw new ArgumentException("Directory does not exist.", nameof(directory));

        // Absolute path, not the bare name: an unqualified executable resolves against the
        // process's current directory ahead of PATH, which for a git client is frequently
        // a checkout — so a planted explorer.exe would win.
        var explorer = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "explorer.exe");

        var info = new ProcessStartInfo(explorer) { UseShellExecute = false };
        info.ArgumentList.Add(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar);

        using var _ = Process.Start(info);
    }
}
