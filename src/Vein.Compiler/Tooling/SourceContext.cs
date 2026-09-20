namespace Vein.Compiler.Tooling;

/// <summary>
/// Whether a qualified `*Author.Bundle.Publicator.$Name` is legal where the caret is.
/// </summary>
/// <remarks>
/// <para>
/// <b>IT IS NOT LEGAL EVERYWHERE, AND WHERE IT IS NOT IT FAILS SILENTLY.</b> RULES 17b: `target`,
/// `mark`/`unmark`, `audience` and `match` take a BARE `$Shape`/`#Mark` — "the qualified form parses
/// there and resolves to nothing". The AST is the proof: `QueryStmt.Components`, `MarkStmt.Mark` and
/// `AttachStmt.Shape` are plain strings with nowhere to put a path, while `ShapeInclude`, `EmitStmt`
/// and `BringStmt` each carry one.
/// </para>
/// <para>
/// So an editor that always inserted the qualified form would write `target *kit.X.Pub.$Thing`, which
/// compiles, matches no identity, and reports nothing — the exact failure this codebase keeps finding.
/// Knowing the site is what lets completion offer the full path for discovery and still insert
/// something that runs.
/// </para>
/// <para>
/// <b>LEXICAL ON PURPOSE.</b> Completion fires on a half-typed line, where a parse tree either does not
/// exist or does not contain the token being typed. This reads backwards from the caret the way the
/// Workbench's other completion helpers do, and answers <see cref="SigilSite.Other"/> when it cannot
/// tell — the caller then falls back to the bare name, which is legal in more places.
/// </para>
/// </remarks>
public static class SourceContext
{
    /// <summary>Where a sigil is being typed, and therefore what may be inserted.</summary>
    public enum SigilSite
    {
        /// <summary>Nothing recognisable. Insert the bare name — legal in more places than not.</summary>
        Other,

        /// <summary>
        /// A `builder`/`event` body, where a `$Shape` include may be qualified —
        /// `builder Conn { *Vein.Rest.Db.$Connection }`.
        /// </summary>
        Include,

        /// <summary>
        /// `target`, `mark`, `unmark`, `attach`, `unattach`, `match`, `audience` — BARE only.
        /// </summary>
        UseSite,

        /// <summary>`emit`, `hear`, `bring`, `start` — qualified is the normal spelling.</summary>
        Emit,
    }

    /// <summary>
    /// Statements whose shape/mark operand has nowhere to put a qualifier.
    ///
    /// `when` is here because a `match` arm takes a bare mark or enum case — and because the `{` of the
    /// enclosing `match` ends the statement scan, so `match` itself is never the head on an arm line.
    /// </summary>
    private static readonly string[] BareOnly =
        ["target", "mark", "unmark", "attach", "unattach", "match", "audience", "when"];

    /// <summary>Statements that take a `*Author.Bundle.Publicator.@Name` as a matter of course.</summary>
    private static readonly string[] Qualifying = ["emit", "hear", "bring", "start"];

    /// <summary>Where <paramref name="caret"/> sits in <paramref name="source"/>.</summary>
    public static SigilSite At(string source, int caret)
    {
        if (string.IsNullOrEmpty(source)) return SigilSite.Other;
        caret = Math.Clamp(caret, 0, source.Length);

        // The first word of the statement the caret is in. A statement ends at a newline or a brace
        // (RULES 1 — a newline ends a statement), so either bounds the scan.
        string head = StatementHead(source, caret);

        if (BareOnly.Contains(head, StringComparer.Ordinal)) return SigilSite.UseSite;
        if (Qualifying.Contains(head, StringComparer.Ordinal)) return SigilSite.Emit;

        // No leading keyword: a bare `$Shape` on its own line inside a `builder`/`event` body is an
        // INCLUDE, which is the one place a qualified shape belongs.
        return EnclosingIs(source, caret, "builder", "event") ? SigilSite.Include : SigilSite.Other;
    }

    /// <summary>The first word of the statement containing <paramref name="caret"/>.</summary>
    private static string StatementHead(string source, int caret)
    {
        int start = caret;
        while (start > 0 && source[start - 1] is not ('\n' or '{' or '}')) start--;

        int i = start;
        while (i < caret && char.IsWhiteSpace(source[i])) i++;

        int wordStart = i;
        while (i < caret && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;

        return source[wordStart..i];
    }

    /// <summary>
    /// Whether the innermost unclosed `{` before the caret is headed by one of <paramref name="heads"/>.
    /// </summary>
    private static bool EnclosingIs(string source, int caret, params string[] heads)
    {
        int depth = 0, i = caret - 1;
        for (; i >= 0; i--)
        {
            if (source[i] == '}') depth++;
            else if (source[i] == '{') { if (depth == 0) break; depth--; }
        }
        if (i < 0) return false;

        // Back over `Name` and the whitespace before it to reach the keyword: `builder Coin {`.
        //
        // THE NAME MAY CARRY A SIGIL. An event body is headed `event @Hit {`, and an identifier-only
        // rule stops dead at the `@` and never reaches the keyword behind it — so an include inside an
        // event was read as no site at all.
        int j = i - 1;
        while (j >= 0 && char.IsWhiteSpace(source[j])) j--;
        while (j >= 0 && (char.IsLetterOrDigit(source[j]) || source[j] is '_' or '@' or '$' or '&' or '#')) j--;
        while (j >= 0 && char.IsWhiteSpace(source[j])) j--;

        int end = j + 1;
        while (j >= 0 && char.IsLetter(source[j])) j--;

        return heads.Contains(source[(j + 1)..end], StringComparer.Ordinal);
    }
}
