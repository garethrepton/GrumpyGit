using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Git;

/// <summary>
/// Constructs valid unified diff patch strings from selected hunks or lines,
/// suitable for piping to <c>git apply --cached</c>.
/// </summary>
public static class PatchBuilder
{
    /// <summary>
    /// Builds a patch containing the specified complete hunks.
    /// </summary>
    public static string BuildFromHunks(
        IReadOnlyList<string> fileHeaderLines,
        IReadOnlyList<DiffHunk> selectedHunks)
    {
        if (selectedHunks.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();

        foreach (var header in fileHeaderLines)
            sb.Append(header).Append('\n');

        foreach (var hunk in selectedHunks)
        {
            sb.Append(hunk.HeaderLine).Append('\n');

            foreach (var line in hunk.Lines)
            {
                switch (line.Type)
                {
                    case DiffLineType.Context:
                        sb.Append(' ').Append(line.Content).Append('\n');
                        break;
                    case DiffLineType.Added:
                        sb.Append('+').Append(line.Content).Append('\n');
                        break;
                    case DiffLineType.Removed:
                        sb.Append('-').Append(line.Content).Append('\n');
                        break;
                    case DiffLineType.NoNewlineMarker:
                        sb.Append(line.Content).Append('\n');
                        break;
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds a patch containing only the selected lines from a single hunk.
    /// For staging (forward apply):
    ///   - Context lines: always included.
    ///   - Selected removed lines: included as '-'.
    ///   - Unselected removed lines: converted to context lines (' ' prefix).
    ///   - Selected added lines: included as '+'.
    ///   - Unselected added lines: omitted entirely.
    /// Line counts in the @@ header are recalculated.
    /// </summary>
    /// <param name="fileHeaderLines">The file header lines for patch reconstruction.</param>
    /// <param name="hunk">The hunk containing the lines.</param>
    /// <param name="selectedLineIndices">0-based indices into <paramref name="hunk"/>.Lines
    /// identifying which added/removed lines are selected.</param>
    /// <returns>A valid patch string, or empty string if no changes would result.</returns>
    public static string BuildFromSelectedLines(
        IReadOnlyList<string> fileHeaderLines,
        DiffHunk hunk,
        IReadOnlySet<int> selectedLineIndices)
        => BuildFromSelectedLines(fileHeaderLines, hunk, selectedLineIndices, forReverseApply: false);

    /// <summary>
    /// Builds a partial-hunk patch for either direction.
    ///
    /// The treatment of UNSELECTED lines has to mirror when the patch will be applied
    /// with <c>--reverse</c>, because git verifies the patch against the side it is
    /// starting from:
    ///
    ///   Forward (staging, worktree → index): the pre-image is the indexed file.
    ///     unselected removed → context (still present in the pre-image)
    ///     unselected added   → omitted  (not present in the pre-image)
    ///
    ///   Reverse (unstaging, index → worktree): git inverts the patch, so the side it
    ///   verifies against is the patch's POST-image, which is the index.
    ///     unselected added   → context (present in the index, must be preserved)
    ///     unselected removed → omitted (not present in the index)
    ///
    /// Using the forward transform for a reverse apply produces a patch whose context
    /// does not match the index. git would normally reject that — but only if its
    /// context check is enabled, so this previously combined with an unconditional
    /// <c>--unidiff-zero</c> to corrupt the index silently instead of failing.
    /// </summary>
    /// <param name="forReverseApply">True when the patch is destined for <c>git apply --reverse</c>.</param>
    public static string BuildFromSelectedLines(
        IReadOnlyList<string> fileHeaderLines,
        DiffHunk hunk,
        IReadOnlySet<int> selectedLineIndices,
        bool forReverseApply)
    {
        // Build the filtered line list
        var patchLines = new List<(char prefix, string content)>();
        bool hasChanges = false;

        for (int i = 0; i < hunk.Lines.Count; i++)
        {
            var line = hunk.Lines[i];
            bool isSelected = selectedLineIndices.Contains(i);

            switch (line.Type)
            {
                case DiffLineType.Context:
                    patchLines.Add((' ', line.Content));
                    break;

                case DiffLineType.Removed:
                    if (isSelected)
                    {
                        patchLines.Add(('-', line.Content));
                        hasChanges = true;
                    }
                    else if (!forReverseApply)
                    {
                        // Forward: still present in the pre-image, so keep as context.
                        patchLines.Add((' ', line.Content));
                    }
                    // Reverse: absent from the index — omit entirely.
                    break;

                case DiffLineType.Added:
                    if (isSelected)
                    {
                        patchLines.Add(('+', line.Content));
                        hasChanges = true;
                    }
                    else if (forReverseApply)
                    {
                        // Reverse: present in the index, so keep as context.
                        patchLines.Add((' ', line.Content));
                    }
                    // Forward: absent from the pre-image — omit entirely.
                    break;

                case DiffLineType.NoNewlineMarker:
                    // Include if the preceding line was included
                    if (patchLines.Count > 0)
                        patchLines.Add(('\\', line.Content));
                    break;
            }
        }

        if (!hasChanges)
            return string.Empty;

        // Recalculate counts
        int oldCount = patchLines.Count(p => p.prefix == ' ' || p.prefix == '-');
        int newCount = patchLines.Count(p => p.prefix == ' ' || p.prefix == '+');

        var sb = new StringBuilder();

        foreach (var header in fileHeaderLines)
            sb.Append(header).Append('\n');

        sb.Append($"@@ -{hunk.OldStart},{oldCount} +{hunk.NewStart},{newCount} @@").Append('\n');

        foreach (var (prefix, content) in patchLines)
        {
            if (prefix == '\\')
            {
                // NoNewlineMarker - emit the content directly (it starts with '\')
                sb.Append(content).Append('\n');
            }
            else
            {
                sb.Append(prefix).Append(content).Append('\n');
            }
        }

        return sb.ToString();
    }
}
