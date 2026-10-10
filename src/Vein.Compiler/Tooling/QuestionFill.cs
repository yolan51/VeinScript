using System.Text.RegularExpressions;

namespace Vein.Compiler.Tooling;

/// What typing `?` should do to the text.
public abstract record FillOutcome(string Status);

/// Replace `[Start, Start+Length)` with `Text` and put the caret at `Caret`. The `?` is always inside the
/// replaced range — it is a keystroke, never something left in the file.
public sealed record FillEdit(int Start, int Length, string Text, int Caret, string Status) : FillOutcome(Status);

/// The `?` asked for something that cannot be shown — an unknown or ambiguous name. Remove it (it is at
/// `At`) and say why.
public sealed record FillProblem(int At, string Status) : FillOutcome(Status);

// WHY: `?` is the Workbench's "show me the signature" key — for an event, a builder or a shape, wherever it
// comes from. It is a SHORTCUT, not syntax: whatever it does, no `?` stays in the file. So every outcome here
// replaces or removes it. A `?` typed where nothing is waiting for fields (inside a string, a comment, after
// a word) is just a character, and `Plan` returns null to leave it alone.
//
// Pure — text in, edit out — so all of it is tested here, and the window only applies the result.
//
//   emit @Sold ?             the whole payload, one field per line, type and origin in a comment
//   bring Crate ?            the whole argument list, one `base` per slot
//   attach $Price to e ?     the shape's fields, like a payload
//   … }?  or  … )?           (`??`) the expansion just made, compacted onto one line
//   emit @Sold { item: "", ?     the fields NOT yet written
//   bring Crate(1, ?             the slots NOT yet passed
public static class QuestionFill
{
    public static FillOutcome? Plan(string text, int q, Signatures scope)
    {
        if (q < 0 || q >= text.Length || text[q] != '?') return null;
        if (SignatureHelp.InString(text, q)) return null;

        return Compact(text, q, scope)
            ?? Expand(text, q, scope)
            ?? Inside(text, q, scope);
    }

    // ---- `emit @E ?` / `bring X ?` / `attach $S to e ?` ---------------------------------------------------

    private static FillOutcome? Expand(string text, int q, Signatures scope)
    {
        if (FillHeads.Before(text, q) is not { } head) return null;

        var lookup = Look(scope, head);
        if (lookup.Hit is not { } sig) return new FillProblem(q, Explain(head, lookup));

        string indent = IndentOf(text, q);
        string full = Indent(Full(sig), indent);

        // An argument list sits against its name — `bring Crate(`, not `bring Crate (` — so for a builder the
        // space typed before the `?` goes too. A payload keeps it: `emit @Sold {`.
        int start = q;
        if (sig.Kind == SignatureKind.Builder)
            while (start > 0 && text[start - 1] is ' ' or '\t') start--;

        // The caret goes just after the closing bracket — for a builder that is mid-line, before the last
        // slot's comment — which is exactly where a second `?` compacts it.
        int last = full.LastIndexOf("base)", StringComparison.Ordinal);
        int close = sig.Kind == SignatureKind.Builder && last >= 0 ? last + "base)".Length : full.Length;
        return new FillEdit(start, q + 1 - start, full, start + close,
            Summary(head, sig) + "  ·  ?? right after the bracket gives the one-line form.");
    }

    // ---- `??` ----------------------------------------------------------------------------------------------

    /// The `?` lands right after a `)` or `}` that closes an expansion. If that expansion is untouched,
    /// replace it with the one-line form; if it was edited, leave it — compacting would throw the edits away.
    private static FillOutcome? Compact(string text, int q, Signatures scope)
    {
        if (q == 0 || text[q - 1] is not (')' or '}')) return null;
        int open = Opener(text, q - 1);
        if (open < 0 || FillHeads.Before(text, open) is not { } head) return null;
        if ((text[open] == '(') != (head.Kind == SignatureKind.Builder)) return null;
        if (Look(scope, head).Hit is not { } sig) return null;

        int lineEnd = text.IndexOf('\n', q);
        if (lineEnd < 0) lineEnd = text.Length;

        // What the first `?` wrote, as it would have been indented here — compared with what is there now.
        string expected = Indent(Full(sig), IndentOf(text, open));
        string actual = text[open..q] + text[(q + 1)..lineEnd];
        if (actual.TrimEnd() != expected.TrimEnd())
            return new FillProblem(q, "?? only compacts an expansion you have not edited yet — this one changed, so it is left as it is.");

        string compact = Short(sig);
        return new FillEdit(open, lineEnd - open, compact, open + compact.Length,
            Summary(head, sig) + "  ·  every slot is its typed zero or default; replace the ones you mean.");
    }

    // ---- `?` inside the braces or brackets: what is left ---------------------------------------------------

    private static FillOutcome? Inside(string text, int q, Signatures scope)
    {
        int open = Unclosed(text, q);
        if (open < 0 || FillHeads.Before(text, open) is not { } head) return null;

        bool paren = text[open] == '(';
        if (paren != (head.Kind == SignatureKind.Builder)) return null;

        var lookup = Look(scope, head);
        if (lookup.Hit is not { } sig) return new FillProblem(q, Explain(head, lookup));

        string written = text[(open + 1)..q];
        string lineBefore = text[(text.LastIndexOf('\n', Math.Max(0, q - 1)) + 1)..q];
        bool ownLine = lineBefore.Trim().Length == 0;
        string indent = IndentOf(text, q);

        if (paren)
        {
            // Slots already passed: commas at depth zero, plus the one being typed if it has text.
            var (commas, tail) = Args(written);
            int done = commas + (tail.Trim().Length > 0 ? 1 : 0);
            var left = sig.Fields.Skip(done).ToList();
            if (left.Count == 0)
                return new FillProblem(q, $"{Ref(head, sig)} takes {sig.Fields.Count} argument(s), and they are all passed.");

            string lead = tail.Trim().Length > 0 ? ", " : "";
            string slots = lead + string.Join(", ", left.Select(_ => "base"));
            return new FillEdit(q, 1, slots, q + slots.Length,
                $"{Ref(head, sig)}: {left.Count} more — " + string.Join(", ", left.Select(Label)));
        }

        // A payload: drop the fields already named.
        var named = Regex.Matches(written, @"(?<![\w.])(\w+)\s*:").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var rest = Ordered(sig.Fields).Where(f => !named.Contains(f.Name)).ToList();
        if (rest.Count == 0)
            return new FillProblem(q, $"every field of {Ref(head, sig)} is already written.");

        string insert = ownLine
            ? string.Join("\n" + indent, rest.Select(Line))
            : (Regex.IsMatch(written, @"[{,]\s*$") || written.Trim().Length == 0 ? "" : ", ") +
              string.Join(", ", rest.Select(f => f.Name + ": " + EventCatalog.Placeholder(f)));
        return new FillEdit(q, 1, insert, q + insert.Length,
            $"{Ref(head, sig)}: {rest.Count} more — " + string.Join(", ", rest.Select(Label)));
    }

    // ---- the texts ---------------------------------------------------------------------------------------

    /// The expansion the first `?` writes. Public so `veinc scaffold` prints exactly what the editor inserts.
    public static string Full(Signature sig) => sig.Kind == SignatureKind.Builder
        ? EventCatalog.Args(sig.Fields)
        : EventCatalog.Body(sig.Fields);

    /// The one-line form `??` writes: every field or slot, nothing left to fill in with `?`.
    public static string Short(Signature sig) => sig.Kind == SignatureKind.Builder
        ? EventCatalog.Args(sig.Fields, compact: true)
        : sig.Fields.Count == 0 ? "{ }"
        : "{ " + string.Join(", ", Ordered(sig.Fields).Select(f => f.Name + ": " + EventCatalog.Placeholder(f))) + " }";

    private static string Line(EventField f) =>
        f.Name + ": " + EventCatalog.Placeholder(f) + "      // " +
        (f.Required ? "required — " + f.Type : "optional — " + f.Type + " = " + f.Default) +
        (f.OriginShape is null ? "" : "   from $" + f.OriginShape);

    private static string Label(EventField f) =>
        f.Name + ": " + f.Type + (f.Required ? "" : " = " + f.Default) + (f.OriginShape is null ? "" : " (from $" + f.OriginShape + ")");

    /// Required first, then optional — the order the expansion writes them in.
    private static IEnumerable<EventField> Ordered(IReadOnlyList<EventField> fields) =>
        fields.Where(f => f.Required).Concat(fields.Where(f => !f.Required));

    private static string Summary(FillHead head, Signature sig)
    {
        int required = sig.Fields.Count(f => f.Required);
        string where = sig.Origin is null ? "" : $" — *{sig.Origin}";
        return $"{head.Keyword} {sig.Sigil}{sig.Name}{where}: {required} required, {sig.Fields.Count - required} optional";
    }

    private static string Ref(FillHead head, Signature sig) => sig.Origin is null ? sig.Sigil + sig.Name : sig.Reference;

    /// Why nothing was shown. An ambiguity names every candidate, so choosing one is a copy away.
    public static string Explain(FillHead head, SignatureLookup lookup)
    {
        string sigil = head.Kind switch { SignatureKind.Event => "@", SignatureKind.Builder => "&", _ => "$" };
        string written = (head.Path.Count > 0 ? "*" + string.Join(".", head.Path) + "." : "") + sigil + head.Name;
        if (lookup.Ambiguous)
            return $"'{written}' is ambiguous here — {string.Join(" and ", lookup.Candidates.Select(c => Qualified(c, sigil)))} (VS0216). Write the path to choose one.";
        return head.Path.Count > 0
            ? $"nothing called '{written}' is shared from that path — `veinc symbols` lists what is."
            : $"nothing called '{written}' is reachable from here — check the spelling, `need` its bundle, or write its *path.";
    }

    private static string Qualified(string key, string sigil)
    {
        int dot = key.LastIndexOf('.');
        return "*" + key[..(dot + 1)] + sigil + key[(dot + 1)..];
    }

    private static SignatureLookup Look(Signatures scope, FillHead head) => head.Kind switch
    {
        SignatureKind.Event => scope.Event(head.Path, head.Name),
        SignatureKind.Builder => scope.Builder(head.Path, head.Name),
        _ => scope.Shape(head.Path, head.Name),
    };

    // ---- text mechanics ----------------------------------------------------------------------------------

    private static string IndentOf(string text, int at)
    {
        int start = at == 0 ? 0 : text.LastIndexOf('\n', at - 1) + 1;
        int i = start;
        while (i < text.Length && text[i] is ' ' or '\t') i++;
        return text[start..i];
    }

    /// Every line after the first lines up under the statement — written flush left, an expansion inside a
    /// shard put its fields and closing bracket at column 0.
    private static string Indent(string block, string indent) => indent.Length == 0 ? block : block.Replace("\n", "\n" + indent);

    /// The `(` or `{` a `)` or `}` at `close` belongs to, skipping strings; -1 when unmatched.
    private static int Opener(string text, int close)
    {
        int depth = 0;
        for (int i = close; i >= 0; i--)
        {
            if (SignatureHelp.InString(text, i)) continue;
            if (text[i] is ')' or '}' or ']') depth++;
            else if (text[i] is '(' or '{' or '[' && --depth == 0) return text[i] == '[' ? -1 : i;
        }
        return -1;
    }

    /// The innermost bracket still open at `at`, skipping strings; -1 when none.
    private static int Unclosed(string text, int at)
    {
        int depth = 0;
        for (int i = at - 1; i >= 0; i--)
        {
            if (SignatureHelp.InString(text, i)) continue;
            if (text[i] is ')' or '}' or ']') depth++;
            else if (text[i] is '(' or '{' or '[')
            {
                if (depth == 0) return text[i] == '[' ? -1 : i;
                depth--;
            }
        }
        return -1;
    }

    /// Commas at depth zero in an argument list so far, and the text after the last one.
    private static (int Commas, string Tail) Args(string written)
    {
        int commas = 0, depth = 0, lastComma = -1;
        for (int i = 0; i < written.Length; i++)
        {
            char c = written[i];
            if (c == '"') { int end = written.IndexOf('"', i + 1); i = end < 0 ? written.Length : end; continue; }
            if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']') depth--;
            else if (c == ',' && depth == 0) { commas++; lastComma = i; }
        }
        return (commas, written[(lastComma + 1)..]);
    }
}
