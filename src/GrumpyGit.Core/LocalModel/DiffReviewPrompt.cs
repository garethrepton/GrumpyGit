using System.Text;
using GrumpyGit.Core.Models;
using GrumpyGit.Core.Agents;

namespace GrumpyGit.Core.LocalModel;

/// <summary>
/// Turns a parsed diff into the prompt for one file review.
///
/// A pure function over the diff, so the interesting decisions — what to include, what to
/// drop first, how to say a file was truncated — are testable without a model, and the
/// same prompt is produced twice for the same diff. That determinism is what makes the
/// review cache sound.
/// </summary>
public static class DiffReviewPrompt
{
    /// <summary>
    /// Roughly 1,500 tokens of diff at ~4 characters a token, leaving room for the answer
    /// inside a small model's practical window. This is a budget rather than a limit
    /// because a truncated review is still useful, whereas a prompt that overflows the
    /// context silently loses the *start* of the diff — the part with the file header.
    /// </summary>
    public const int DiffCharacterBudget = 12000;

    /// <summary>
    /// Bumped whenever the wording below changes. It is part of the cache key, so an
    /// edit here invalidates reviews written by the previous prompt instead of leaving
    /// two generations of answer mixed together in one session.
    /// </summary>
    public const int Version = 5;

    /// <summary>
    /// A line-oriented reply format, not JSON. A 1.5B model asked for JSON produces
    /// *nearly* valid JSON often enough to be maddening — a trailing comma, a smart quote,
    /// prose wrapped around the object. Line prefixes degrade instead of failing: a
    /// malformed line is dropped and the rest of the review survives.
    ///
    /// The four labels are deliberately unlike ordinary prose, so a model that starts
    /// narrating does not accidentally produce something the parser accepts.
    ///
    /// The trailing <c>/no_think</c> is Qwen3's documented switch for turning its reasoning
    /// pass off. It matters more here than it looks: the token ceiling is a few hundred, and
    /// a hybrid model left to think spends all of them working out and emits no answer at
    /// all. Models that do not know the switch — every Qwen2.5-Coder in the catalogue —
    /// read it as one more instruction and ignore it.
    /// </summary>
    private const string SystemInstruction =
        """
        You review code diffs for a git client. The user sends one file's changes, each
        numbered, with the new file's line numbers shown.

        Reply using only these lines, in this order, and write nothing else:

        SUMMARY: one or two sentences on what this change does.
        RISK: none, caution or danger.
        ISSUE <line>: a specific problem at that line number.
        CHANGE <number>: what that numbered change does, in under twelve words.
        TOUCHES <number>: one word, then what it reaches, in under eight words.

        Give one CHANGE line for every change you were shown. Give an ISSUE line only for a
        real problem you can see in the diff — a dropped guard, an inverted condition, an
        off-by-one, a resource left open, a widened permission, a value that can be null.
        If you see none, write no ISSUE lines at all. Never invent a line number; only use
        numbers shown in the diff. Say RISK: danger only when the change can lose data,
        remove a safety check, or expose something it should not.

        Give a TOUCHES line for a change that reaches outside itself. Start it with exactly
        one of these words:

        files — reads, writes, moves or deletes a file or directory, or builds a path
        network — opens a socket, makes a request, or names a URL or host
        process — starts a program, a shell or a command line
        credentials — handles a password, token, key, or anything authenticating
        data-loss — can destroy or overwrite work that cannot be recovered

        A change that does none of these gets no TOUCHES line. Judge only what the change
        itself does — not what the file around it might do. Do not guess: a TOUCHES line you
        are unsure of is worse than none, because it is read as a reason to stop and look.

        /no_think
        """;

    /// <param name="chunk">
    /// Which pass of a large file to build. Changes keep their numbering across passes, so
    /// the model answering "CHANGE 23" in the third pass still means the twenty-third change
    /// in the file.
    /// </param>
    public static ModelPrompt Build(
        string path, ParsedDiff diff, FileChangeSummary? summary = null, int chunk = 0)
    {
        ArgumentNullException.ThrowIfNull(diff);

        var user = new StringBuilder();
        user.Append("File: ").AppendLine(path);

        var chunks = DiffNotebook.ChunkCount(diff);
        if (chunks > 1)
            // Said plainly so the model does not describe the file as if it had seen all of
            // it. It is being shown a part, and a summary claiming otherwise is worse than
            // a summary that admits the scope.
            user.Append("Part ").Append(chunk + 1).Append(" of ").Append(chunks)
                .AppendLine(" — describe only the changes below.");

        if (summary is not null && summary.Symbols.Count > 0)
        {
            // The symbol account is already computed for the panel beside the diff, comes
            // from git's own hunk headers, and costs a handful of tokens. It gives the
            // model the structure a raw hunk body does not carry.
            user.Append("Touched: ")
                .AppendLine(string.Join(", ", summary.Symbols
                    .Where(s => !s.IsAnonymous)
                    .Take(12)
                    .Select(s => $"{s.Symbol} (+{s.Added} −{s.Removed})")));
        }

        user.AppendLine();

        var dropped = 0;

        // Numbered and packed into passes by DiffNotebook, not decided here. The view draws
        // a section per change using the same numbering, so the model saying "CHANGE 7" and
        // the reader seeing change 7 depend on there being exactly one definition of what a
        // change is and one answer to which pass it belongs to.
        foreach (var block in DiffNotebook.Split(diff))
        {
            if (block.Chunk < 0)
            {
                dropped++;
                continue;
            }

            if (block.Chunk == chunk)
                user.Append(RenderChange(block));
        }

        if (dropped > 0 && chunk == chunks - 1)
            user.AppendLine()
                .Append("(")
                .Append(dropped)
                .AppendLine(" further change(s) omitted — this file is larger than the review budget.)");

        return new ModelPrompt(SystemInstruction, user.ToString());
    }

    /// <summary>
    /// Changed lines with their hunk header and the new file's line numbers. Context lines
    /// are dropped: they are the bulk of a diff's characters and the least informative per
    /// token — the header already names the enclosing declaration.
    ///
    /// Line numbers are shown because they are how an issue gets anchored to a place in
    /// the file. Without them the model can only say "the null check", and the UI has
    /// nowhere to point.
    /// </summary>
    private static string RenderChange(ChangeBlock block)
    {
        var text = new StringBuilder();
        text.Append("CHANGE ").Append(block.Number).Append(' ').AppendLine(block.HeaderLine);

        foreach (var line in block.Lines)
        {
            var marker = line.Type == DiffLineType.Added ? '+' : '-';

            // A removed line has no line number in the new file; showing its old number
            // would invite an issue anchored to a line that no longer exists.
            var number = line.Type == DiffLineType.Added ? line.NewLineNumber : -1;
            if (number > 0)
                text.Append(number).Append(' ');

            text.Append(marker).AppendLine(line.Content);
        }

        return text.ToString();
    }
}
