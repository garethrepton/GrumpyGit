using System.Text;
using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Git;

/// <summary>
/// Renders a review of a <see cref="PullRequestPreview"/> as markdown, ready to paste
/// wherever the pull request is eventually raised.
///
/// Author names and email addresses are deliberately left out. A summary is written to be
/// pasted somewhere else, and a git history is full of real people's contact details
/// (commandment 9); commit hashes and subjects identify the work without carrying them.
/// </summary>
public static class PullRequestSummaryBuilder
{
    public static string Build(PullRequestPreview preview, IReadOnlyList<ReviewedFile> files)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(files);

        var sb = new StringBuilder();

        sb.Append("# ").Append(preview.SourceBranch).Append(" → ").AppendLine(preview.TargetBranch);
        sb.AppendLine();

        sb.Append(Count(preview.Commits.Count, "commit"))
          .Append(" · ")
          .Append(Count(files.Count, "file"))
          .Append(" · +").Append(files.Sum(f => f.LinesAdded))
          .Append(" −").AppendLine(files.Sum(f => f.LinesRemoved).ToString());
        sb.AppendLine();

        sb.Append("Diffed from merge base `").Append(Short(preview.MergeBaseHash)).AppendLine("`.");
        sb.AppendLine();

        AppendMergeVerdict(sb, preview.Merge);

        var reviewed = files.Count(f => f.IsReviewed);
        sb.Append("**Reviewed:** ").Append(reviewed).Append('/').Append(files.Count).AppendLine(" files");
        sb.AppendLine();

        if (preview.Commits.Count > 0)
        {
            sb.AppendLine("## Commits");
            sb.AppendLine();
            foreach (var commit in preview.Commits)
                sb.Append("- `").Append(Short(commit.Hash)).Append("` ").AppendLine(commit.Subject);
            sb.AppendLine();
        }

        if (files.Count > 0)
        {
            sb.AppendLine("## Files");
            sb.AppendLine();
            foreach (var file in files)
            {
                sb.Append(file.IsReviewed ? "- [x] `" : "- [ ] `")
                  .Append(file.Path)
                  .Append("` (+").Append(file.LinesAdded)
                  .Append(" −").Append(file.LinesRemoved).AppendLine(")");

                AppendNote(sb, file.Note);
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static void AppendMergeVerdict(StringBuilder sb, MergePreview merge)
    {
        switch (merge.Outcome)
        {
            case MergeOutcome.Clean:
                sb.AppendLine("**Merges cleanly.**");
                break;

            case MergeOutcome.Conflicts:
                sb.Append("**Would not merge cleanly** — ")
                  .Append(Count(merge.ConflictingPaths.Count, "conflicting file"))
                  .AppendLine(":");
                foreach (var path in merge.ConflictingPaths)
                    sb.Append("- `").Append(path).AppendLine("`");
                break;

            default:
                sb.AppendLine("**Merge check unavailable** — this git could not simulate the merge.");
                break;
        }

        sb.AppendLine();
    }

    /// <summary>
    /// Notes are free text and routinely span lines. Each line is indented so it stays
    /// inside its list item rather than terminating the list at the first newline.
    /// </summary>
    private static void AppendNote(StringBuilder sb, string note)
    {
        if (string.IsNullOrWhiteSpace(note)) return;

        foreach (var line in note.Replace("\r\n", "\n").Split('\n'))
            sb.Append("  > ").AppendLine(line);
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static string Short(string hash) => hash.Length > 7 ? hash[..7] : hash;
}
