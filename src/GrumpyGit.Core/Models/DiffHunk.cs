namespace GrumpyGit.Core.Models;

public sealed class DiffHunk
{
    public int Index { get; init; }
    public int OldStart { get; init; }
    public int OldCount { get; init; }
    public int NewStart { get; init; }
    public int NewCount { get; init; }
    public string HeaderLine { get; init; } = string.Empty;
    public IReadOnlyList<DiffLine> Lines { get; init; } = [];

    /// <summary>
    /// 1-based line number in the rendered ParsedDiff editors where
    /// this hunk's @@ header appears. Set during parsing.
    /// </summary>
    public int RenderedLineNumber { get; set; }
}

public sealed class DiffLine
{
    public DiffLineType Type { get; init; }
    public string Content { get; init; } = string.Empty;
    public int OldLineNumber { get; init; } = -1;
    public int NewLineNumber { get; init; } = -1;
    public int RenderedLineNumber { get; set; }
}

public enum DiffLineType
{
    Context,
    Added,
    Removed,
    NoNewlineMarker
}
