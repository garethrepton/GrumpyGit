using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.LocalModel;

/// <summary>
/// One contiguous run of changed lines — a single edit, as a reader would point at it.
/// </summary>
/// <param name="Number">
/// 1-based across the whole file. This is the number the model is given and the number it
/// answers with, which is why it is computed in one place and never recounted.
/// </param>
/// <param name="HeaderLine">The <c>@@</c> header of the enclosing hunk, for context.</param>
/// <param name="StartRenderedLine">
/// Where this block begins in the rendered diff document, so a note about it can be drawn
/// against it in the editor.
/// </param>
public sealed record ChangeBlock(
    int Number,
    string HeaderLine,
    int StartRenderedLine,
    IReadOnlyList<DiffLine> Lines)
{
    public int Added => Lines.Count(l => l.Type == DiffLineType.Added);

    public int Removed => Lines.Count(l => l.Type == DiffLineType.Removed);

    /// <summary>
    /// Which pass of the model this change is shown in, or -1 if it is shown in none.
    ///
    /// A file too large for one prompt is reviewed in several, rather than truncated at the
    /// budget and the rest left undescribed. Only a file past
    /// <see cref="DiffNotebook.MaxChunks"/> loses changes entirely, and at that size the
    /// review is not worth having anyway.
    /// </summary>
    public int Chunk { get; init; }

    /// <summary>
    /// Whether this change is put in front of the model at all. Kept distinct from "has no
    /// note" because the two deserve very different amounts of the reader's trust.
    /// </summary>
    public bool WasSentToModel => Chunk >= 0;
}

/// <summary>
/// One change with everything the model said about it, ready to be drawn as a section.
/// </summary>
public sealed record ReviewedChange(
    ChangeBlock Block,
    string Note,
    IReadOnlyList<ReviewIssue> Issues,
    ChangeConcern? Concern = null)
{
    public bool HasConcern => Concern is not null;
    public int Number => Block.Number;
    public string HeaderLine => Block.HeaderLine;
    public IReadOnlyList<DiffLine> Lines => Block.Lines;
    public int Added => Block.Added;
    public int Removed => Block.Removed;
    public bool WasSentToModel => Block.WasSentToModel;

    public bool HasNote => Note.Length > 0;
    public bool HasIssues => Issues.Count > 0;
}

/// <summary>
/// Splits a diff into individual changes, and pairs each with what the model said about it.
///
/// <see cref="Split"/> is the single definition of "one change" in the application, and
/// that is the point of it being here rather than in the view. The same numbering has to
/// hold in three places — the prompt that asks the model to describe change 7, the parser
/// that reads its answer, and the notebook that draws it — and three implementations of
/// "contiguous run of changed lines" would agree right up until the file where they did
/// not, at which point a description would appear above the wrong code.
///
/// A hunk is git's unit, chosen so a patch applies; it can hold half a dozen unrelated
/// edits separated by context. A reader's unit is the edit. So the block is what gets a
/// description, and a hunk with ten separate changes produces ten sections.
/// </summary>
public static class DiffNotebook
{
    /// <summary>
    /// How much untouched code has to sit between two edits before they count as separate
    /// changes.
    ///
    /// One line apart is almost always one edit — a renamed variable used on both sides of
    /// a line that did not change, a condition and the statement two lines below it. Every
    /// such pair split in two costs a section on screen and, worse, a description: the
    /// model is asked for a line per change and has a fixed budget, so inventing changes
    /// spends it on nothing and leaves real ones with no reading at all.
    /// </summary>
    private const int ContextLinesThatSeparate = 2;

    /// <summary>
    /// Every run of added or removed lines, numbered across the file.
    ///
    /// A run ends when <see cref="ContextLinesThatSeparate"/> or more unchanged lines
    /// follow it. Added and removed lines never end each other: a rewritten line is a
    /// removal followed by an addition, and calling that two changes would be counting the
    /// diff format rather than the edit.
    /// </summary>
    public static IReadOnlyList<ChangeBlock> Split(ParsedDiff? diff)
    {
        if (diff is null || diff.Hunks.Count == 0)
            return [];

        var blocks = new List<ChangeBlock>();
        var number = 0;

        foreach (var hunk in diff.Hunks)
        {
            var run = new List<DiffLine>();
            var gap = new List<DiffLine>();

            foreach (var line in hunk.Lines)
            {
                if (line.Type is DiffLineType.Added or DiffLineType.Removed)
                {
                    // A short gap turns out to have been interior to one change after all,
                    // so it joins the run rather than being dropped — the reader needs the
                    // line between two edits to see why they are one.
                    if (run.Count > 0)
                        run.AddRange(gap);

                    gap.Clear();
                    run.Add(line);
                    continue;
                }

                if (run.Count == 0)
                    continue;

                gap.Add(line);

                if (gap.Count >= ContextLinesThatSeparate)
                {
                    blocks.Add(BlockOf(++number, hunk, run));
                    run = [];
                    gap.Clear();
                }
            }

            if (run.Count > 0)
                blocks.Add(BlockOf(++number, hunk, run));
        }

        return AssignChunks(blocks);
    }

    private static ChangeBlock BlockOf(int number, DiffHunk hunk, List<DiffLine> run) =>
        new(number, hunk.HeaderLine, run[0].RenderedLineNumber, run);

    /// <summary>
    /// How many passes a file may be reviewed in.
    ///
    /// Each is a separate inference, so this is a ceiling on wall-clock rather than on
    /// correctness: six passes of a CPU-bound small model is already minutes. A file with
    /// more changes than this fits is one whose review nobody waits for.
    /// </summary>
    public const int MaxChunks = 6;

    /// <summary>
    /// Packs changes into prompt-sized passes.
    ///
    /// Decided here rather than while rendering the prompt so that the prompt, the parser
    /// and the view agree on which pass each change belongs to — the same reason the
    /// numbering lives here. The cost is estimated from the line contents rather than
    /// measured on the rendered text, which keeps the two files from having to know each
    /// other's formatting; a few characters either way does not matter to a budget that is
    /// itself a round number.
    /// </summary>
    private static List<ChangeBlock> AssignChunks(List<ChangeBlock> blocks)
    {
        var chunk = 0;
        var spent = 0;

        for (var i = 0; i < blocks.Count; i++)
        {
            // Line number, marker and newline, roughly.
            var cost = blocks[i].HeaderLine.Length + 10
                       + blocks[i].Lines.Sum(l => l.Content.Length + 8);

            // A change larger than a whole budget still gets a pass of its own rather than
            // being dropped — it is truncated by the context, which is bad, but silence
            // about the biggest change in a file is worse.
            if (spent > 0 && spent + cost > DiffReviewPrompt.DiffCharacterBudget)
            {
                chunk++;
                spent = 0;
            }

            blocks[i] = blocks[i] with { Chunk = chunk < MaxChunks ? chunk : -1 };
            spent += cost;
        }

        return blocks;
    }

    /// <summary>How many passes this diff needs. Zero when there is nothing to review.</summary>
    public static int ChunkCount(ParsedDiff? diff)
    {
        var blocks = Split(diff);
        return blocks.Count == 0 ? 0 : blocks.Max(b => b.Chunk) + 1;
    }

    /// <summary>Every change, with its note and any issues anchored inside it.</summary>
    public static IReadOnlyList<ReviewedChange> Build(
        ParsedDiff? diff,
        IReadOnlyList<ChangeNote>? notes = null,
        IReadOnlyList<ReviewIssue>? issues = null,
        IReadOnlyList<ChangeConcern>? concerns = null)
    {
        var noteByNumber = (notes ?? [])
            .GroupBy(n => n.ChangeNumber)
            .ToDictionary(g => g.Key, g => g.First().Text);

        var concernByNumber = (concerns ?? [])
            .GroupBy(c => c.ChangeNumber)
            .ToDictionary(g => g.Key, g => g.First());

        return Split(diff)
            .Select(block => new ReviewedChange(
                block,
                noteByNumber.GetValueOrDefault(block.Number, string.Empty),
                IssuesIn(block, issues),
                concernByNumber.GetValueOrDefault(block.Number)))
            .ToList();
    }

    /// <summary>
    /// Issues belonging to one change.
    ///
    /// Matched on the line the issue is anchored to rather than on any number the model
    /// gave, because it never gives one for an issue — and because an issue that could not
    /// be anchored has to land somewhere honest. That is nowhere: an unanchored issue stays
    /// in the panel above the diff, attributed to the file rather than to a change it may
    /// have nothing to do with.
    /// </summary>
    private static IReadOnlyList<ReviewIssue> IssuesIn(ChangeBlock block, IReadOnlyList<ReviewIssue>? issues)
    {
        if (issues is null || issues.Count == 0)
            return [];

        var lines = block.Lines
            .Where(l => l.RenderedLineNumber > 0)
            .Select(l => l.RenderedLineNumber)
            .ToHashSet();

        return issues
            .Where(i => i.IsAnchored && lines.Contains(i.RenderedLine))
            .ToList();
    }
}
