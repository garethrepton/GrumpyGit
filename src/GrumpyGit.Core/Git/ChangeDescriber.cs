using GrumpyGit.Core.Models;

namespace GrumpyGit.Core.Git;

/// <summary>
/// Describes in words what a change did to a symbol, instead of restating its lines.
///
/// These are HEURISTICS over the diff text, not an understanding of the code. That
/// constrains the design: every rule here must be one that cannot be confidently wrong.
/// "comments only" is decidable from the text; "fixes a null check" is not, and guessing
/// it would make the whole view untrustworthy — a reviewer who catches one invented
/// description stops believing the other forty. Where nothing certain can be said, the
/// describer falls back to counting, which is always true.
/// </summary>
public static class ChangeDescriber
{
    /// <summary>
    /// A one-line account of what happened to a symbol, e.g. "new method, 12 lines" or
    /// "reworked 3 lines, 2 added".
    /// </summary>
    public static string Describe(string symbol, IReadOnlyList<DiffLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var added = lines.Where(l => l.Type == DiffLineType.Added).Select(l => l.Content).ToList();
        var removed = lines.Where(l => l.Type == DiffLineType.Removed).Select(l => l.Content).ToList();

        if (added.Count == 0 && removed.Count == 0) return "no change";

        // Ordered most-specific first: a comment-only edit is also "n lines added", but
        // the former is what the reader needs to know.
        if (IsWhitespaceOnly(added, removed)) return "whitespace only";
        if (AllComments(added) && AllComments(removed)) return CommentDescription(added, removed);

        var declaresSymbol = symbol.Length > 0 && added.Any(l => ContainsDeclaration(l, symbol));
        var removesSymbol = symbol.Length > 0 && removed.Any(l => ContainsDeclaration(l, symbol));

        if (declaresSymbol && !removesSymbol)
            return $"new, {Plural(added.Count, "line")}" + Notes(symbol, added, removed, []);

        if (removesSymbol && !declaresSymbol)
            return $"removed, {Plural(removed.Count, "line")}";

        if (removed.Count == 0)
            return $"{Plural(added.Count, "line")} added" + Notes(symbol, added, removed, []);

        if (added.Count == 0)
            return $"{Plural(removed.Count, "line")} removed";

        // Both sides present: separate lines that were edited in place from lines that
        // are genuinely new or gone, because "reworked 2, added 8" reads very differently
        // from "rewrote 10".
        var pairs = MatchReworkedPairs(added, removed);
        var reworked = pairs.Count;
        var netAdded = added.Count - reworked;
        var netRemoved = removed.Count - reworked;

        var parts = new List<string>();
        if (reworked > 0) parts.Add($"reworked {Plural(reworked, "line")}");
        if (netAdded > 0) parts.Add($"{netAdded} added");
        if (netRemoved > 0) parts.Add($"{netRemoved} removed");

        var shape = parts.Count > 0
            ? string.Join(", ", parts)
            : $"rewrote {Plural(added.Count, "line")}";

        return shape + Notes(symbol, added, removed, pairs);
    }

    /// <summary>
    /// Short observations appended after the shape, e.g. "· null check added". Each is a
    /// direct textual reading — "this line now contains a throw" — never an inference
    /// about intent. Capped at two so the description stays scannable; the diff is one
    /// keystroke away for anyone who wants the rest.
    /// </summary>
    private static string Notes(
        string symbol,
        IReadOnlyList<string> added,
        IReadOnlyList<string> removed,
        IReadOnlyList<(string Removed, string Added)> pairs)
    {
        // Candidates are produced in priority order and the first two win. The ordering
        // is the design: a note that NAMES something ("signature changed: +branch",
        // "Start → StartForDiff") is worth several that merely categorise, so the
        // specific detectors run before the generic ones and crowd them out.
        var notes = new List<string>();

        void Note(string? text)
        {
            if (text is null || notes.Count >= 2 || notes.Contains(text)) return;
            notes.Add(text);
        }

        Note(SignatureNote(symbol, added, removed));
        Note(VisibilityNote(symbol, added, removed));
        Note(IdentifierSwapNote(pairs));
        Note(ConditionNote(pairs));

        foreach (var line in added)
        {
            if (Contains(line, "is null", "== null", "!= null", "ArgumentNullException"))
                Note("null check added");
        }

        if (GuardSuffix(added).Length > 0) Note("guard added");

        foreach (var line in added)
        {
            if (line.Contains("throw new", StringComparison.Ordinal)) Note("throws added");
            if (line.Contains("catch", StringComparison.Ordinal)
                || (line.Contains("try", StringComparison.Ordinal) && line.Contains('{')))
                Note("error handling added");
            if (Contains(line, "TODO", "FIXME", "HACK")) Note("TODO added");
        }

        if (AddedOnly(added, removed, "async ")) Note("made async");
        if (AddedOnly(added, removed, "await ")) Note("await added");

        Note(AllOf(added, removed, IsImport, "imports"));
        Note(AllOf(added, removed, IsAttribute, "attributes"));

        // Whether the edited lines grew or shrank as expressions. Token containment is a
        // fact about the text, so this says "more was added to this call" without
        // claiming to know what the call now does.
        var extended = pairs.Count(p => Tokenise(p.Added).IsProperSupersetOf(Tokenise(p.Removed)));
        var reduced = pairs.Count(p => Tokenise(p.Removed).IsProperSupersetOf(Tokenise(p.Added)));

        if (extended > 0 && reduced == 0) Note("extended in place");
        else if (reduced > 0 && extended == 0) Note("simplified");

        return notes.Count == 0 ? string.Empty : " · " + string.Join(" · ", notes);
    }

    /// <summary>
    /// Names the parameters a signature edit added or removed. The most valuable thing
    /// this class can say: a changed parameter list is the one edit that breaks callers,
    /// and knowing WHICH parameter saves opening the file.
    /// </summary>
    private static string? SignatureNote(
        string symbol, IReadOnlyList<string> added, IReadOnlyList<string> removed)
    {
        if (symbol.Length == 0) return null;

        var newDecl = added.FirstOrDefault(l => ContainsDeclaration(l, symbol));
        var oldDecl = removed.FirstOrDefault(l => ContainsDeclaration(l, symbol));
        if (newDecl is null || oldDecl is null) return null;

        var before = ParameterNames(oldDecl);
        var after = ParameterNames(newDecl);

        var gained = after.Except(before, StringComparer.Ordinal).ToList();
        var lost = before.Except(after, StringComparer.Ordinal).ToList();

        var detail = string.Join(" ",
            gained.Select(p => "+" + p).Concat(lost.Select(p => "−" + p)));

        return detail.Length > 0 ? $"signature changed: {detail}" : "signature changed";
    }

    /// <summary>
    /// Parameter names from a declaration: the last identifier of each comma-separated
    /// entry between the outermost parentheses. Types, defaults and modifiers are
    /// deliberately ignored — the name is what a caller reads.
    /// </summary>
    private static List<string> ParameterNames(string declaration)
    {
        var open = declaration.IndexOf('(');
        var close = declaration.LastIndexOf(')');
        if (open < 0 || close <= open) return [];

        var names = new List<string>();
        var depth = 0;
        var current = new System.Text.StringBuilder();

        // Split on commas at depth zero, so a generic argument list or a nested default
        // value does not fracture one parameter into several.
        foreach (var c in declaration[(open + 1)..close])
        {
            if (c is '<' or '(' or '[') depth++;
            else if (c is '>' or ')' or ']') depth--;

            if (c == ',' && depth == 0)
            {
                names.Add(LastIdentifier(current.ToString()));
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) names.Add(LastIdentifier(current.ToString()));
        return names.Where(n => n.Length > 0).ToList();
    }

    private static string LastIdentifier(string value)
    {
        // A default value ("int count = 3") puts the name before the '='.
        var equals = value.IndexOf('=');
        if (equals >= 0) value = value[..equals];

        var tokens = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length == 0 ? string.Empty : tokens[^1].Trim('?', '[', ']');
    }

    private static readonly string[] VisibilityKeywords =
        ["public", "private", "protected", "internal"];

    private static string? VisibilityNote(
        string symbol, IReadOnlyList<string> added, IReadOnlyList<string> removed)
    {
        if (symbol.Length == 0) return null;

        var newDecl = added.FirstOrDefault(l => ContainsDeclaration(l, symbol));
        var oldDecl = removed.FirstOrDefault(l => ContainsDeclaration(l, symbol));
        if (newDecl is null || oldDecl is null) return null;

        var before = VisibilityKeywords.FirstOrDefault(k => Tokenise(oldDecl).Contains(k));
        var after = VisibilityKeywords.FirstOrDefault(k => Tokenise(newDecl).Contains(k));

        return before is not null && after is not null && before != after
            ? $"{before} → {after}"
            : null;
    }

    /// <summary>
    /// When an edited line differs by exactly one identifier on each side, name both.
    /// "Start → StartForDiff" is the single most useful sentence this class can produce,
    /// and it is pure observation: those tokens are literally what changed.
    /// </summary>
    private static string? IdentifierSwapNote(IReadOnlyList<(string Removed, string Added)> pairs)
    {
        foreach (var (removedLine, addedLine) in pairs)
        {
            var before = Tokenise(removedLine);
            var after = Tokenise(addedLine);

            var lost = before.Except(after, StringComparer.Ordinal).ToList();
            var gained = after.Except(before, StringComparer.Ordinal).ToList();

            if (lost.Count == 1 && gained.Count == 1)
                return $"{lost[0]} → {gained[0]}";
        }

        return null;
    }

    private static string? ConditionNote(IReadOnlyList<(string Removed, string Added)> pairs) =>
        pairs.Any(p => StartsWithKeyword(p.Removed, "if") && StartsWithKeyword(p.Added, "if"))
            ? "condition changed"
            : null;

    private static bool StartsWithKeyword(string line, string keyword)
    {
        var t = line.TrimStart();
        return t.StartsWith(keyword + " ", StringComparison.Ordinal)
            || t.StartsWith(keyword + "(", StringComparison.Ordinal);
    }

    /// <summary>
    /// Reports a note when EVERY changed line matches a shape, e.g. an edit made up
    /// entirely of using directives. Requiring all of them keeps the note honest: one
    /// import among twenty statements is not "an import change".
    /// </summary>
    private static string? AllOf(
        IReadOnlyList<string> added,
        IReadOnlyList<string> removed,
        Func<string, bool> predicate,
        string noun)
    {
        var all = added.Concat(removed).Where(l => l.Trim().Length > 0).ToList();
        if (all.Count == 0 || !all.All(predicate)) return null;

        return (added.Count, removed.Count) switch
        {
            (> 0, 0) => $"{noun} added",
            (0, > 0) => $"{noun} removed",
            _ => $"{noun} changed",
        };
    }

    private static bool IsImport(string line)
    {
        var t = line.TrimStart();
        return (t.StartsWith("using ", StringComparison.Ordinal)
                || t.StartsWith("import ", StringComparison.Ordinal))
               && t.TrimEnd().EndsWith(';');
    }

    private static bool IsAttribute(string line)
    {
        var t = line.Trim();
        return t.StartsWith('[') && t.EndsWith(']');
    }

    private static bool Contains(string line, params string[] needles) =>
        needles.Any(n => line.Contains(n, StringComparison.Ordinal));

    /// <summary>True when a token appears on the added side and on no removed line.</summary>
    private static bool AddedOnly(
        IReadOnlyList<string> added, IReadOnlyList<string> removed, string token) =>
        added.Any(l => l.Contains(token, StringComparison.Ordinal))
        && !removed.Any(l => l.Contains(token, StringComparison.Ordinal));

    /// <summary>
    /// True when both sides carry the same content once all whitespace is stripped —
    /// a reindent or a rewrap, with nothing else in it.
    /// </summary>
    private static bool IsWhitespaceOnly(IReadOnlyList<string> added, IReadOnlyList<string> removed)
    {
        if (added.Count == 0 || removed.Count == 0) return false;

        static List<string> Squeeze(IReadOnlyList<string> source) =>
            source.Select(s => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray()))
                  .Where(s => s.Length > 0)
                  .OrderBy(s => s, StringComparer.Ordinal)
                  .ToList();

        return Squeeze(added).SequenceEqual(Squeeze(removed), StringComparer.Ordinal);
    }

    private static bool AllComments(IReadOnlyList<string> lines)
    {
        // An empty side counts as "all comments" so a pure comment insertion still
        // qualifies; the caller requires at least one line overall.
        foreach (var line in lines)
        {
            var t = line.TrimStart();
            if (t.Length == 0) continue;
            if (!t.StartsWith("//", StringComparison.Ordinal)
                && !t.StartsWith("*", StringComparison.Ordinal)
                && !t.StartsWith("/*", StringComparison.Ordinal)
                && !t.StartsWith("#", StringComparison.Ordinal)
                && !t.StartsWith("--", StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static string CommentDescription(IReadOnlyList<string> added, IReadOnlyList<string> removed) =>
        (added.Count, removed.Count) switch
        {
            (> 0, 0) => "comments added",
            (0, > 0) => "comments removed",
            _ => "comments reworded",
        };

    /// <summary>
    /// Whether a line declares <paramref name="symbol"/> rather than merely mentioning it.
    ///
    /// Matches on the declaration's NAME, not its full text. Comparing whole signatures
    /// fails in exactly the case that matters most: when the signature is what changed,
    /// neither side equals the other, and an edited method would be misreported as a
    /// brand new one.
    ///
    /// A call site also contains <c>Name(</c>, so declarations are separated from calls by
    /// the trailing semicolon a statement has and a declaration does not.
    /// </summary>
    private static bool ContainsDeclaration(string line, string symbol)
    {
        var name = DeclarationName(symbol);
        if (name.Length == 0) return false;

        var trimmed = Squash(line);
        if (trimmed.EndsWith(';')) return false;

        return trimmed.Contains(name + "(", StringComparison.Ordinal)
            || trimmed.EndsWith(" " + name, StringComparison.Ordinal);
    }

    /// <summary>
    /// The identifier out of a declaration: whatever sits immediately before the
    /// parameter list, or the final token when there is no parameter list (a property,
    /// a type).
    /// </summary>
    private static string DeclarationName(string symbol)
    {
        var squashed = Squash(symbol);
        if (squashed.Length == 0) return string.Empty;

        var paren = squashed.IndexOf('(');
        var head = paren >= 0 ? squashed[..paren] : squashed;

        var end = head.Length;
        while (end > 0 && !char.IsLetterOrDigit(head[end - 1]) && head[end - 1] != '_') end--;

        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(head[start - 1]) || head[start - 1] == '_')) start--;

        return head[start..end];
    }

    private static string Squash(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
              .TrimEnd('{', ' ');

    /// <summary>
    /// Counts added/removed pairs that are recognisably the same line edited, using token
    /// overlap. Deliberately conservative: an unmatched pair is reported as one addition
    /// and one deletion, which overstates the churn slightly but never invents a
    /// relationship that is not there.
    /// </summary>
    private static List<(string Removed, string Added)> MatchReworkedPairs(
        IReadOnlyList<string> added, IReadOnlyList<string> removed)
    {
        var used = new bool[added.Count];
        var pairs = new List<(string, string)>();

        foreach (var r in removed)
        {
            var rTokens = Tokenise(r);
            if (rTokens.Count == 0) continue;

            for (var i = 0; i < added.Count; i++)
            {
                if (used[i]) continue;

                var aTokens = Tokenise(added[i]);
                if (aTokens.Count == 0) continue;

                var shared = rTokens.Intersect(aTokens, StringComparer.Ordinal).Count();
                var similarity = (double)shared / Math.Max(rTokens.Count, aTokens.Count);

                // Half the tokens in common is a deliberate midpoint: high enough that
                // two unrelated statements rarely qualify, low enough to survive a real
                // edit that swaps an argument or a method name.
                if (similarity < 0.5) continue;

                used[i] = true;
                pairs.Add((r, added[i]));
                break;
            }
        }

        return pairs;
    }

    private static HashSet<string> Tokenise(string line)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var current = new System.Text.StringBuilder();

        foreach (var c in line)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>
    /// Notes an added early-exit, which is worth calling out: it changes control flow
    /// rather than adding to it, and is a common shape for a bug fix.
    /// </summary>
    private static string GuardSuffix(IReadOnlyList<string> added)
    {
        foreach (var line in added)
        {
            var t = line.TrimStart();
            if (!t.StartsWith("if ", StringComparison.Ordinal)
                && !t.StartsWith("if(", StringComparison.Ordinal)) continue;

            if (t.Contains("return", StringComparison.Ordinal)
                || t.Contains("throw", StringComparison.Ordinal)
                || t.Contains("continue", StringComparison.Ordinal))
                return " · guard added";
        }
        return string.Empty;
    }

    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
