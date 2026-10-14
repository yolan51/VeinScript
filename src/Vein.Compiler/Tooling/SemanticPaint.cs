using Vein.Compiler.Diagnostics;
using Vein.Compiler.Lexing;

namespace Vein.Compiler.Tooling;

/// The colours a word-list theme cannot give, for the editor to paint over it.
///
/// The Workbench's `.xshd` theme matches words and regexes one line at a time, which is enough for a
/// keyword or a `$Shape` but not for three things that need the tokens AROUND a name:
///
/// - **`fn` and `SF` functions.** A call site is `name(`, and whether `name` is pure or side-effecting
///   is only known from its declaration, possibly hundreds of lines away. They rendered as plain white.
/// - **Schedules.** `run`, `once`, `every` and `frame` are contextual words, not keywords (KEYWORDS.md),
///   so `run once` rendered white next to a blue `each tick` — the same kind of thing in two looks.
/// - **Qualified paths.** `*Author.Bundle.Publicator.@Event` — each segment is a different thing, and
///   the `*` is also multiplication. Only the token before it says which: `emit *A.B.@E` is a path,
///   `n *m.count` is arithmetic.
///
/// Lexing rather than regex is what keeps strings and comments out: the lexer already skips them, so a
/// `fn` in a comment or `*` in an HTML string never gets painted. It runs on text that is mid-edit;
/// lexer diagnostics are collected and ignored.
public static class SemanticPaint
{
    public enum Role { Fn, SF, Schedule, Author, Bundle, Publicator, Shard }

    /// One stretch to paint. Line and column are 1-based, as the lexer's spans are.
    public readonly record struct Paint(int Line, int Col, int Length, Role Role);

    /// Paints for <paramref name="source"/>.
    ///
    /// <paramref name="knownFunctions"/> adds names declared elsewhere — a fragment merged into this
    /// file, keyed by bare name — and <paramref name="qualifiedFunctions"/> resolves `*A.B.P.name(`,
    /// keyed `A.B.P.name`. Both map to true for an SF. A name declared in the source itself wins over
    /// either, because it is the declaration the reader can see.
    public static IReadOnlyList<Paint> Analyze(
        string source,
        IReadOnlyDictionary<string, bool>? knownFunctions = null,
        IReadOnlyDictionary<string, bool>? qualifiedFunctions = null)
    {
        IReadOnlyList<Token> tokens;
        try { tokens = new Lexer(source, "paint", new DiagnosticBag()).Tokenize(); }
        catch { return Array.Empty<Paint>(); }   // a half-typed file must never cost the editor its colours

        // Pass 1: what each local name is. Declarations can follow their calls, so this cannot be folded
        // into the painting pass.
        var isSf = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (knownFunctions is not null) foreach (var (k, v) in knownFunctions) isSf[k] = v;
        for (int i = 0; i + 1 < tokens.Count; i++)
            if (tokens[i].Kind is TokenKind.KwFn or TokenKind.KwSf && tokens[i + 1].Kind == TokenKind.Ident)
                isSf[tokens[i + 1].Text] = tokens[i].Kind == TokenKind.KwSf;

        var paints = new List<Paint>();
        void Add(Token t, Role r) => paints.Add(new Paint(t.Span.Line, t.Span.Col, t.Span.Length, r));
        Role FnRole(bool sf) => sf ? Role.SF : Role.Fn;

        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            Token? next = i + 1 < tokens.Count ? tokens[i + 1] : null;
            Token? prev = i > 0 ? tokens[i - 1] : null;

            switch (t.Kind)
            {
                // `fn name` / `SF name` — the declaration, even of a name nothing calls yet.
                case TokenKind.KwFn or TokenKind.KwSf when next?.Kind == TokenKind.Ident:
                    Add(next.Value, FnRole(t.Kind == TokenKind.KwSf));
                    i++;
                    break;

                // `name(` — a call, when `name` is a function this file or its build knows. A builder
                // call in `bring Panel(…)` and a built-in like `random()` share the shape and are left
                // alone: painting an unknown name as a function would be a guess shown as a fact.
                case TokenKind.Ident when next?.Kind == TokenKind.LParen && prev?.Kind != TokenKind.Dot
                                          && isSf.TryGetValue(t.Text, out bool sf):
                    Add(t, FnRole(sf));
                    break;

                // `do` is what makes a line an SF call (D13), so it wears the SF colour rather than the keyword one.
                case TokenKind.KwDo:
                    Add(t, Role.SF);
                    break;

                // A shard's NAME where it is declared (`shard Ledger {`, `ShardView Page`, `bridge Sync`) or
                // paused (`mute Ledger`) — the same colour `!Ledger` has as a reference (D19), so a shard
                // reads as one thing wherever it is named. `mute #Gameplay` is a mark and keeps the mark's.
                case TokenKind.KwShard or TokenKind.KwShardView or TokenKind.KwBridge or TokenKind.KwMute or TokenKind.KwUnmute
                    when next?.Kind == TokenKind.Ident && prev?.Kind != TokenKind.Dot:
                    Add(next.Value, Role.Shard);
                    i++;
                    break;

                case TokenKind.Ident when t.Text == "run" && next is { Kind: TokenKind.Ident, Text: "once" }:
                    Add(t, Role.Schedule); Add(next.Value, Role.Schedule); i++;
                    break;
                case TokenKind.Ident when t.Text == "every" && next?.Kind is TokenKind.Int or TokenKind.Float:
                    Add(t, Role.Schedule);
                    break;
                case TokenKind.KwEach when next is { Kind: TokenKind.KwTick } or { Kind: TokenKind.Ident, Text: "frame" }:
                    Add(t, Role.Schedule); Add(next.Value, Role.Schedule); i++;
                    break;
                case TokenKind.KwSettled when next?.Kind == TokenKind.LBrace:
                    Add(t, Role.Schedule);
                    break;

                case TokenKind.Star when StartsPath(tokens, i):
                    i = PaintPath(tokens, i, paints, isSf, qualifiedFunctions);
                    break;
            }
        }

        return paints;
    }

    /// Whether the `*` at <paramref name="i"/> opens a path rather than multiplying. It is multiplication
    /// exactly when the token before it ends a value — the same decision the parser makes by where it
    /// meets the `*`, prefix or infix.
    private static bool StartsPath(IReadOnlyList<Token> tokens, int i)
    {
        if (i + 1 >= tokens.Count || tokens[i + 1].Kind != TokenKind.Ident) return false;
        if (i == 0) return true;

        var prev = tokens[i - 1];
        bool afterDot = i >= 2 && tokens[i - 2].Kind == TokenKind.Dot;   // `e.count * 2` — a keyword as a member name
        return prev.Kind switch
        {
            TokenKind.Ident or TokenKind.Int or TokenKind.Float or TokenKind.Percent or TokenKind.String
                or TokenKind.RParen or TokenKind.RBracket
                or TokenKind.ShapeRef or TokenKind.EventRef or TokenKind.MarkRef or TokenKind.BuilderRef
                or TokenKind.KwTrue or TokenKind.KwFalse or TokenKind.KwEntity or TokenKind.KwIndex
                or TokenKind.KwBase => false,
            _ when afterDot => false,
            _ => true
        };
    }

    /// Paint `*A.B.P.member` from the `*` at <paramref name="star"/>; returns the index of its last token.
    ///
    /// The segments are named by position — author, bundle, then publicator — and the member keeps the
    /// colour its sigil already has everywhere else, so a `@Print` reads as an event whether or not it
    /// is qualified. A trailing plain name followed by `(` is a function, looked up by its full path.
    private static int PaintPath(IReadOnlyList<Token> tokens, int star, List<Paint> paints,
                                 Dictionary<string, bool> localSf, IReadOnlyDictionary<string, bool>? qualified)
    {
        var idents = new List<Token>();
        int i = star + 1;
        while (i < tokens.Count && tokens[i].Kind == TokenKind.Ident)
        {
            idents.Add(tokens[i]);
            if (i + 2 < tokens.Count && tokens[i + 1].Kind == TokenKind.Dot && tokens[i + 2].Kind == TokenKind.Ident) i += 2;
            else break;
        }
        int last = i;

        // `*A.B.P.name(` — the last name is the function, not a fourth namespace.
        Token? function = null;
        if (idents.Count >= 3 && last + 1 < tokens.Count && tokens[last + 1].Kind == TokenKind.LParen)
        {
            function = idents[^1];
            idents.RemoveAt(idents.Count - 1);
        }
        // `*A.B.@E` — step past the member so its sigil colour (the theme's) stays.
        else if (last + 2 < tokens.Count && tokens[last + 1].Kind == TokenKind.Dot
                 && tokens[last + 2].Kind is TokenKind.ShapeRef or TokenKind.EventRef
                                          or TokenKind.MarkRef or TokenKind.BuilderRef)
        {
            last += 2;
        }

        var roles = new[] { Role.Author, Role.Bundle, Role.Publicator };
        var s = tokens[star].Span;
        paints.Add(new Paint(s.Line, s.Col, s.Length, Role.Author));   // the `*` opens the path, in its first colour
        for (int k = 0; k < idents.Count && k < roles.Length; k++)
            paints.Add(new Paint(idents[k].Span.Line, idents[k].Span.Col, idents[k].Span.Length, roles[k]));

        if (function is { } f)
        {
            string key = string.Join(".", idents.Select(t => t.Text)) + "." + f.Text;
            bool sf = qualified is not null && qualified.TryGetValue(key, out bool q) ? q
                    : localSf.TryGetValue(f.Text, out bool l) && l;
            paints.Add(new Paint(f.Span.Line, f.Span.Col, f.Span.Length, sf ? Role.SF : Role.Fn));
        }

        return last;
    }
}
