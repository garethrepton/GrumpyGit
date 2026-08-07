using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Git;

/// <summary>
/// Reads the output of <c>git merge-tree --write-tree --name-only -z</c>.
///
/// The NUL-delimited layout is: the merged tree's OID, then one entry per conflicting
/// path, then an empty entry, then informational messages. A clean merge emits only the
/// OID. The informational block is deliberately discarded — it repeats the paths in
/// English and would put repository content into the UI verbatim.
/// </summary>
public static class MergeTreeParser
{
    /// <summary>
    /// Git's exit code carries the verdict and the output carries the detail, so both are
    /// needed: exit 0 is a clean merge, 1 is conflicts, anything else means git could not
    /// answer (notably a git older than 2.38, which has no <c>--write-tree</c>).
    /// </summary>
    public static MergePreview Parse(string output, int exitCode)
    {
        if (exitCode == 0)
            return new MergePreview(MergeOutcome.Clean, []);

        if (exitCode != 1)
            return MergePreview.Unknown;

        var fields = output.Split('\0');

        // fields[0] is the tree OID; conflicting paths run until the empty entry that
        // closes the section. Without that stop the informational messages, which are
        // also NUL-separated, would be listed as if they were file names.
        var paths = new List<string>();
        for (var i = 1; i < fields.Length; i++)
        {
            if (fields[i].Length == 0) break;
            paths.Add(fields[i]);
        }

        // Exit code 1 with no named path still means "would not merge cleanly"; saying
        // "clean" there would be the one wrong answer this can give.
        return new MergePreview(MergeOutcome.Conflicts, paths);
    }
}
