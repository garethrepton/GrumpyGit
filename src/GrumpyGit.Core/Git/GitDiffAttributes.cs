namespace GrumpyGit.Core.Git;

/// <summary>
/// Default gitattributes that switch on git's built-in language diff drivers.
///
/// Those drivers are what put the enclosing symbol on a hunk header —
/// <c>@@ -154,6 +154,10 @@ private void OnLoaded(...)</c> instead of the bare line
/// range. The change summary reads that context to attribute each hunk to a function,
/// so without a driver it can only report "this file changed". Most repositories never
/// configure one, so shipping a default is the difference between the summary working
/// everywhere and working almost nowhere.
///
/// Supplied through <c>core.attributesFile</c>, which sits BELOW a repository's own
/// <c>.gitattributes</c> in git's precedence order. A repo that configures its own
/// drivers keeps them; this only fills the silence.
///
/// Safety: every driver named here is one of git's built-ins, each a bundled regex
/// rather than an external command, so this cannot introduce the execution vector that
/// <see cref="GitProcess"/> exists to close. Nothing here sets <c>text</c>,
/// <c>eol</c> or <c>filter</c>, so content handling is untouched — the only effect is
/// which regex git uses to label a hunk.
/// </summary>
public static class GitDiffAttributes
{
    // Extensions are mapped only where the mapping is unambiguous. ".m" is deliberately
    // absent: it is objective-c in one ecosystem and matlab in another, and guessing
    // wrong puts a misleading symbol name on every hunk of the file.
    private const string FileContent = """
        *.c      diff=cpp
        *.h      diff=cpp
        *.cc     diff=cpp
        *.cpp    diff=cpp
        *.cxx    diff=cpp
        *.hpp    diff=cpp
        *.cs     diff=csharp
        *.csx    diff=csharp
        *.java   diff=java
        *.kt     diff=kotlin
        *.kts    diff=kotlin
        *.py     diff=python
        *.rb     diff=ruby
        *.rs     diff=rust
        *.go     diff=golang
        *.php    diff=php
        *.pl     diff=perl
        *.pm     diff=perl
        *.ex     diff=elixir
        *.exs    diff=elixir
        *.sh     diff=bash
        *.bash   diff=bash
        *.pas    diff=pascal
        *.scm    diff=scheme
        *.tex    diff=tex
        *.css    diff=css
        *.scss   diff=css
        *.html   diff=html
        *.htm    diff=html
        *.md     diff=markdown
        """;

    private static readonly Lazy<string?> PathValue = new(Ensure);

    /// <summary>
    /// Absolute path to the defaults file, or null if it could not be written. Callers
    /// must treat null as "no defaults" and carry on — a missing symbol name degrades
    /// the summary, it does not break diffing.
    /// </summary>
    public static string? Path => PathValue.Value;

    private static string? Ensure()
    {
        try
        {
            // Resolved here rather than through the App's AppPaths so Core stays free of
            // a dependency on the UI layer. Same folder, deliberately.
            var root = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Grumpy");

            Directory.CreateDirectory(root);
            var path = System.IO.Path.Combine(root, "diff-attributes");

            // Rewrite only when the content differs, so upgrading the list below reaches
            // existing installs without touching the disk on every launch.
            if (!File.Exists(path) || File.ReadAllText(path) != FileContent)
                File.WriteAllText(path, FileContent);

            // Git config values on Windows take forward slashes reliably; a raw
            // backslash path is treated as containing escapes.
            return path.Replace('\\', '/');
        }
        catch
        {
            return null;
        }
    }
}
