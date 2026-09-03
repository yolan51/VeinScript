namespace Vein.Compiler.Tooling;

/// The call the caret is inside: what is being called, and which slot is being typed.
public sealed record CallContext(string Name, bool IsEvent, int ActiveSlot);

// WHY: `bring Panel(?, ?, ?, ?)` tells you nothing about which `?` is which. An include flattens
// someone else's shape into the parameter list, so the names are not even in this file — and the
// binding is positional, so the slot carries no name of its own. That is the case signature help
// exists for here, more than for an ordinary function call.
//
// This finds the CALL; EventCatalog already knows what each one takes. Textual rather than AST-based
// on purpose: help is wanted while the call is half-written, which is exactly when it does not parse.
public static class SignatureHelp
{
    /// The `bring`/`emit` the caret sits inside, or null.
    public static CallContext? At(string source, int caret)
    {
        if (caret < 0 || caret > source.Length) return null;

        // Walk back to the innermost unclosed bracket. Strings are skipped so a `"("` in a text literal
        // does not read as an open call — the web samples are full of them.
        int depth = 0;
        for (int i = Math.Min(caret, source.Length) - 1; i >= 0; i--)
        {
            if (InString(source, i)) continue;
            char c = source[i];

            if (c is ')' or '}') depth++;
            else if (c is '(' or '{')
            {
                if (depth > 0) { depth--; continue; }

                // An unclosed opener. `(` is an argument list, `{` an emit payload — and a `{` that is
                // a block rather than a payload simply has no `emit` before it, so Preceding returns
                // null and no help is offered.
                return c == '('
                    ? Preceding(source, i, "bring") is { } b ? new CallContext(b, false, Slot(source, i, caret, ',')) : null
                    : Preceding(source, i, "emit") is { } e ? new CallContext(e, true, Slot(source, i, caret, ',')) : null;
            }
            else if (c == ';') return null;
        }
        return null;
    }

    /// Render the signature, marking the slot being typed. Positional binding means the marker is the
    /// only thing saying which parameter you are in.
    public static string? Describe(CallContext call, IReadOnlyList<EventField> fields)
    {
        if (fields.Count == 0) return $"{(call.IsEvent ? "@" : "&")}{call.Name} — no fields";

        var parts = fields.Select((f, i) =>
        {
            string one = f.Name + ": " + f.Type + (f.Required ? "" : " = " + (f.Default ?? "default"));
            return i == call.ActiveSlot ? "[" + one + "]" : one;
        });

        return $"{(call.IsEvent ? "@" : "&")}{call.Name}( {string.Join(", ", parts)} )";
    }

    /// The identifier immediately before `open`, when the keyword before IT is `keyword`.
    ///
    /// Handles `emit *Vein.Console.Io.@Print` as well as a bare `@Print`: the name is the last segment,
    /// which is exactly how Interp routes events.
    private static string? Preceding(string source, int open, string keyword)
    {
        int i = open - 1;
        while (i >= 0 && char.IsWhiteSpace(source[i])) i--;

        int end = i + 1;
        while (i >= 0 && (char.IsLetterOrDigit(source[i]) || source[i] is '_' or '.' or '@' or '*' or '$' or '#')) i--;
        if (end <= i + 1) return null;

        string token = source[(i + 1)..end];
        string name = token[(token.LastIndexOfAny(new[] { '.', '@', '*' }) + 1)..].TrimStart('@', '$', '#');
        if (name.Length == 0) return null;

        // The keyword must be the word before the token, not merely somewhere on the line.
        while (i >= 0 && char.IsWhiteSpace(source[i])) i--;
        int kwEnd = i + 1;
        while (i >= 0 && char.IsLetter(source[i])) i--;

        return source[(i + 1)..kwEnd] == keyword ? name : null;
    }

    /// Which slot the caret is in: separators between the opener and the caret, at depth zero.
    private static int Slot(string source, int open, int caret, char separator)
    {
        int slot = 0, depth = 0;
        for (int i = open + 1; i < caret && i < source.Length; i++)
        {
            if (InString(source, i)) continue;
            char c = source[i];
            if (c is '(' or '{' or '[') depth++;
            else if (c is ')' or '}' or ']') depth--;
            else if (c == separator && depth == 0) slot++;
        }
        return slot;
    }

    /// Whether the offset falls inside a string literal on its own line. Line-scoped because VeinScript
    /// has no multi-line strings, and scanning from the top per character would be quadratic.
    private static bool InString(string source, int at)
    {
        int lineStart = source.LastIndexOf('\n', Math.Max(0, Math.Min(at, source.Length - 1))) + 1;
        bool inString = false;
        for (int i = lineStart; i < at && i < source.Length; i++)
        {
            char c = source[i];
            if (c == '"' && (i == 0 || source[i - 1] != '\\')) inString = !inString;
            else if (!inString && c == '/' && i + 1 < source.Length && source[i + 1] == '/') return true;
        }
        return inString;
    }
}
