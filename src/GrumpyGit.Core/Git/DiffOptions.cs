namespace GrumpyGit.Core.Git;

/// <summary>
/// How a diff should be computed. Passed to every diff-producing method so the
/// viewer's toggles reach git itself rather than being faked by post-filtering
/// (which would desynchronise line numbers from the real file).
/// </summary>
public sealed record DiffOptions
{
    /// <summary>
    /// Number of unchanged context lines around each change (<c>-U</c>).
    /// <see cref="FullFileContext"/> requests effectively the whole file.
    /// </summary>
    public int ContextLines { get; init; } = 3;

    /// <summary>Ignore changes that only alter whitespace (<c>-w</c>).</summary>
    public bool IgnoreWhitespace { get; init; }

    /// <summary>Ignore changes whose lines are all blank (<c>--ignore-blank-lines</c>).</summary>
    public bool IgnoreBlankLines { get; init; }

    /// <summary>
    /// Context value that makes git emit the entire file as one hunk. Git has no
    /// "whole file" flag, so a context larger than any realistic source file is the
    /// documented way to get full-file output.
    /// </summary>
    public const int FullFileContext = 1_000_000;

    public bool IsFullFile => ContextLines >= FullFileContext;

    /// <summary>
    /// True when the produced patch is safe to feed back to <c>git apply</c>.
    ///
    /// Whitespace-insensitive diffs deliberately omit real differences, so a patch
    /// built from one will not reconstruct the file and <c>git apply --cached</c>
    /// will either fail or corrupt the index. Staging must be blocked in that mode.
    /// Full-file context is just a larger <c>-U</c>, which stays applicable.
    /// </summary>
    public bool SupportsPatchStaging => !IgnoreWhitespace && !IgnoreBlankLines;

    public static readonly DiffOptions Default = new();

    /// <summary>Applies the flags to a CliWrap argument builder.</summary>
    public void Apply(CliWrap.Builders.ArgumentsBuilder args)
    {
        args.Add($"-U{ContextLines}");

        if (IgnoreWhitespace)
            args.Add("-w");

        if (IgnoreBlankLines)
            args.Add("--ignore-blank-lines");
    }
}
