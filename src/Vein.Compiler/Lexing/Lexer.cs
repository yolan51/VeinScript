using System.Globalization;
using System.Text;
using Vein.Compiler.Diagnostics;

namespace Vein.Compiler.Lexing;

public sealed class Lexer
{
    private static readonly Dictionary<string, TokenKind> Keywords = new(StringComparer.Ordinal)
    {
        ["and"] = TokenKind.KwAnd,             ["as"] = TokenKind.KwAs,
        ["attach"] = TokenKind.KwAttach,       ["audience"] = TokenKind.KwAudience,
        ["builder"] = TokenKind.KwBuilder,     ["bridge"] = TokenKind.KwBridge,
        ["bring"] = TokenKind.KwBring,         ["bundle"] = TokenKind.KwBundle,
        ["by"] = TokenKind.KwBy,               ["chance"] = TokenKind.KwChance,
        ["count"] = TokenKind.KwCount,         ["destroy"] = TokenKind.KwDestroy,
        ["each"] = TokenKind.KwEach,           ["else"] = TokenKind.KwElse,
        ["emit"] = TokenKind.KwEmit,           ["event"] = TokenKind.KwEvent,
        ["false"] = TokenKind.KwFalse,         ["folds"] = TokenKind.KwFolds,
        ["from"] = TokenKind.KwFrom,           ["hear"] = TokenKind.KwHear,
        ["let"] = TokenKind.KwLet,             ["map"] = TokenKind.KwMap,
        ["mark"] = TokenKind.KwMark,           ["mute"] = TokenKind.KwMute,
        ["not"] = TokenKind.KwNot,             ["on"] = TokenKind.KwOn,
        ["or"] = TokenKind.KwOr,               ["publicator"] = TokenKind.KwPublicator,
        // NO `random` HERE, deliberately. It is a BUILT-IN FUNCTION, not a keyword, and reserving the
        // word made it unreachable: `random()` was VS0104 because ParsePrimary only accepts an Ident as
        // a call target. Every other stage already implemented it — Interp.PrebuiltNames, Prebuilt's
        // `_rng.NextDouble()`, Resolve typing it float, and the backend's `World.Random()` — so the
        // lexer was the single thing standing between a working built-in and any program that wanted
        // it. `chance N%` lowered to it internally and was the only way to reach it at all.
        //
        // Same shape as `spawn`, which is a built-in for the same reason and was never a keyword.
        // KwRandom stays in TokenKind: nothing produces it now, and removing an enum member buys
        // nothing but renumbering.
        ["return"] = TokenKind.KwReturn,
        ["settled"] = TokenKind.KwSettled,     ["SF"] = TokenKind.KwSf,
        ["shape"] = TokenKind.KwShape,         ["shard"] = TokenKind.KwShard,
        ["shared"] = TokenKind.KwShared,       ["start"] = TokenKind.KwStart,
        ["sync"] = TokenKind.KwSync,           ["target"] = TokenKind.KwTarget,
        ["tick"] = TokenKind.KwTick,           ["to"] = TokenKind.KwTo,
        ["transform"] = TokenKind.KwTransform, ["true"] = TokenKind.KwTrue,
        ["unattach"] = TokenKind.KwUnattach,   ["unmark"] = TokenKind.KwUnmark,
        ["unmute"] = TokenKind.KwUnmute,       ["use"] = TokenKind.KwUse,
        ["need"] = TokenKind.KwNeed,
        ["var"] = TokenKind.KwVar,             ["when"] = TokenKind.KwWhen,

        // Milestone 2 additions (docs/KEYWORDS.md §2). `push` intentionally removed (D4).
        ["fn"] = TokenKind.KwFn,               ["type"] = TokenKind.KwType,
        ["enum"] = TokenKind.KwEnum,           ["if"] = TokenKind.KwIf,
        ["while"] = TokenKind.KwWhile,         ["repeat"] = TokenKind.KwRepeat,
        ["break"] = TokenKind.KwBreak,         ["continue"] = TokenKind.KwContinue,
        ["match"] = TokenKind.KwMatch,         ["ShardView"] = TokenKind.KwShardView,
        ["Entity"] = TokenKind.KwEntity,       ["app"] = TokenKind.KwApp,
        ["Index"] = TokenKind.KwIndex,   ["base"] = TokenKind.KwBase,

        // D13: `do name(…)` calls an SF — a statement that changes things says so where it is written.
        ["do"] = TokenKind.KwDo,

        // D21: `tune("doc")` marks a shard var as a setting (vein.settings).
        ["tune"] = TokenKind.KwTune,

        // D23: `weave Server(text)` — records written as text, each brought as `bring Server(…)` would;
        // `loom<Format> Server { … }` is what can be woven, `format Name { … }` how a record is written.
        ["weave"] = TokenKind.KwWeave,
        ["loom"] = TokenKind.KwLoom,           ["format"] = TokenKind.KwFormat,
    };

    private readonly string _src;
    private readonly string _file;
    private readonly DiagnosticBag _diagnostics;
    private readonly List<Token> _tokens = new();

    private int _pos;
    private int _line = 1;
    private int _col = 1;

    /// Every keyword, for tooling that must stay in step with the language — the Workbench's syntax
    /// theme is checked against this. A keyword the theme does not list renders as plain text, and a
    /// theme is an embedded XML resource, so nothing fails: it just quietly looks wrong.
    public static IReadOnlyCollection<string> KeywordNames => Keywords.Keys;

    public Lexer(string source, string file, DiagnosticBag diagnostics)
    {
        _src = source;
        _file = file;
        _diagnostics = diagnostics;
    }

    private char Current => _pos < _src.Length ? _src[_pos] : '\0';
    private char Peek(int n = 1) => _pos + n < _src.Length ? _src[_pos + n] : '\0';
    private bool AtEnd => _pos >= _src.Length;

    private char Advance()
    {
        char c = _src[_pos++];
        if (c == '\n') { _line++; _col = 1; } else { _col++; }
        return c;
    }

    private SourceSpan SpanFrom(int startPos, int startLine, int startCol) =>
        new(_file, startLine, startCol, _pos - startPos);

    public IReadOnlyList<Token> Tokenize()
    {
        while (!AtEnd) ScanNext();

        _tokens.Add(new Token(TokenKind.EndOfFile, "", null, new SourceSpan(_file, _line, _col, 0)));
        return _tokens;
    }

    /// One token, or the whitespace/comment before one. Its own method because an interpolation inside a
    /// string (`"HP: \(hp)"`) scans ordinary tokens until its `)` — the same scanning, entered from ScanString.
    private void ScanNext()
    {
        char c = Current;

        if (c == ' ' || c == '\t' || c == '\r') { Advance(); return; }

        if (c == '\n')
        {
            // Virtual terminator: only when the previous token can end a statement.
            // This is what replaces `end` as a statement separator (grammar 3.3).
            if (CanEndStatement())
            {
                _tokens.Add(new Token(TokenKind.Term, "\\n", null,
                    new SourceSpan(_file, _line, _col, 1)));
            }
            Advance();
            return;
        }

        if (c == '/' && Peek() == '/') { SkipLineComment(); return; }
        if (c == '/' && Peek() == '*') { SkipBlockComment(); return; }

        int startPos = _pos, startLine = _line, startCol = _col;

        if (c == '$' || c == '@' || c == '#' || c == '&') { ScanSigilRef(startPos, startLine, startCol); return; }
        // `!Name` names a shard (D19). A `!` NOT followed by a name is still VS0006 below: `!=`, `! x`.
        if (c == '!' && (char.IsLetter(Peek()) || Peek() == '_')) { ScanSigilRef(startPos, startLine, startCol); return; }
        if (char.IsDigit(c)) { ScanNumber(startPos, startLine, startCol); return; }
        if (c == '"') { ScanString(startPos, startLine, startCol); return; }
        if (char.IsLetter(c) || c == '_') { ScanIdentifier(startPos, startLine, startCol); return; }

        ScanOperator(startPos, startLine, startCol);
    }

    // ---- newline rule ----------------------------------------------------

    private bool CanEndStatement()
    {
        if (_tokens.Count == 0) return false;
        return _tokens[^1].Kind switch
        {
            TokenKind.Int or TokenKind.Float or TokenKind.Percent or TokenKind.String
                or TokenKind.Ident or TokenKind.RParen or TokenKind.RBrace or TokenKind.RBracket
                or TokenKind.ShapeRef or TokenKind.EventRef or TokenKind.MarkRef or TokenKind.BuilderRef or TokenKind.ShardRef
                or TokenKind.KwTrue or TokenKind.KwFalse or TokenKind.KwReturn
                or TokenKind.KwSync or TokenKind.KwBreak or TokenKind.KwContinue => true,
            _ => false
        };
    }

    // ---- scanners --------------------------------------------------------

    private void SkipLineComment()
    {
        while (!AtEnd && Current != '\n') Advance();
    }

    private void SkipBlockComment()
    {
        int startLine = _line, startCol = _col;
        Advance(); Advance();                       // consume /*
        while (!AtEnd && !(Current == '*' && Peek() == '/')) Advance();
        if (AtEnd)
        {
            _diagnostics.Error("VS0001", "Unterminated block comment.",
                new SourceSpan(_file, startLine, startCol, 2));
            return;
        }
        Advance(); Advance();                       // consume */
    }

    private void ScanSigilRef(int startPos, int startLine, int startCol)
    {
        char sigil = Advance();

        if (!(char.IsLetter(Current) || Current == '_'))
        {
            _diagnostics.Error("VS0002",
                $"Expected an identifier after '{sigil}'.",
                SpanFrom(startPos, startLine, startCol));
            return;
        }

        var sb = new StringBuilder();
        while (!AtEnd && (char.IsLetterOrDigit(Current) || Current == '_')) sb.Append(Advance());

        var kind = sigil switch
        {
            '$' => TokenKind.ShapeRef,
            '@' => TokenKind.EventRef,
            '&' => TokenKind.BuilderRef,
            '!' => TokenKind.ShardRef,
            _   => TokenKind.MarkRef
        };

        // The sigil is folded into the token; it is never emitted separately.
        _tokens.Add(new Token(kind, sb.ToString(), null, SpanFrom(startPos, startLine, startCol)));
    }

    private void ScanNumber(int startPos, int startLine, int startCol)
    {
        var sb = new StringBuilder();
        while (!AtEnd && char.IsDigit(Current)) sb.Append(Advance());

        bool isFloat = false;
        if (Current == '.' && char.IsDigit(Peek()))
        {
            isFloat = true;
            sb.Append(Advance());
            while (!AtEnd && char.IsDigit(Current)) sb.Append(Advance());
        }

        // Maximal munch: `30%` is one token. This must happen here, before Int is
        // emitted, or `chance 30%` collides with modulo (grammar 1.3).
        if (Current == '%')
        {
            Advance();
            double pct = double.Parse(sb.ToString(), CultureInfo.InvariantCulture);
            _tokens.Add(new Token(TokenKind.Percent, sb + "%", pct,
                SpanFrom(startPos, startLine, startCol)));
            return;
        }

        if (isFloat)
        {
            double d = double.Parse(sb.ToString(), CultureInfo.InvariantCulture);
            _tokens.Add(new Token(TokenKind.Float, sb.ToString(), d,
                SpanFrom(startPos, startLine, startCol)));
        }
        else
        {
            long l = long.Parse(sb.ToString(), CultureInfo.InvariantCulture);
            _tokens.Add(new Token(TokenKind.Int, sb.ToString(), l,
                SpanFrom(startPos, startLine, startCol)));
        }
    }

    // INTERPOLATION (SYNTAX-DECISIONS D29): `"HP: \(hp) of \(max)"` lexes as the tokens of
    // `("HP: " + (hp) + " of " + (max) + "")`. Building text with `+` was the most repeated thing in all our
    // code — 3,210 joins and 1,354 lines ending in `+` across the corpus (docs/CORPUS.md) — and doing it HERE
    // means the parser, Resolve, the interpreter and the C# backend all see the `+` they already handle, with
    // the same conversions: an interpolated value prints exactly as `"" + value` does. The first piece is
    // always a string (even "") so `"\(a)\(b)"` joins two numbers as text instead of adding them.
    //
    // Spans: each literal piece spans its own source (the quotes included), the expression's tokens are
    // ordinary tokens, `\(` and `)` are the group's parentheses, and the `+`s and the outer parentheses are
    // zero-length — so an editor painting tokens colours the pieces as strings and the expression as code.
    //
    // `\(` used to be an unknown escape that meant `(`; no string in the corpus used it. A backslash before a
    // parenthesis is now written `\\(`.
    private void ScanString(int startPos, int startLine, int startCol)
    {
        Advance();                                  // opening quote
        var sb = new StringBuilder();
        int piecePos = startPos, pieceLine = startLine, pieceCol = startCol;
        bool interpolated = false, reported = false;

        void Piece()
        {
            _tokens.Add(new Token(TokenKind.String, sb.ToString(), sb.ToString(), SpanFrom(piecePos, pieceLine, pieceCol)));
            sb.Clear();
        }
        Token Joint(TokenKind kind, string text) => new(kind, text, null, new SourceSpan(_file, _line, _col, 0));

        while (!AtEnd && Current != '"' && Current != '\n')
        {
            if (Current == '\\' && Peek() == '(')
            {
                if (!interpolated) _tokens.Add(new Token(TokenKind.LParen, "(", null, new SourceSpan(_file, startLine, startCol, 0)));
                interpolated = true;
                Piece();
                _tokens.Add(Joint(TokenKind.Plus, "+"));

                int open = _pos, openLine = _line, openCol = _col;
                int close = ClosingParen(_pos + 2);
                Advance(); Advance();               // `\(`
                _tokens.Add(new Token(TokenKind.LParen, "(", null, SpanFrom(open, openLine, openCol)));

                if (close < 0)
                {
                    // No `)` before the line ends. The line's last quote is most likely this string's own end, so
                    // skip to it and carry on: one error, and the statement around the string still parses.
                    _diagnostics.Error("VS0399", "`\\(` in a string needs its `)` on the same line, before the string ends: \"HP: \\(hp)\".",
                        SpanFrom(open, openLine, openCol));
                    reported = true;
                    int last = LastQuoteOnLine(_pos);
                    while (!AtEnd && Current != '\n' && (last < 0 || _pos < last)) Advance();
                    _tokens.Add(Joint(TokenKind.String, ""));
                    _tokens.Add(Joint(TokenKind.RParen, ")"));
                }
                else
                {
                    int before = _tokens.Count;
                    while (_pos < close) ScanNext();
                    if (_tokens.Count == before)
                    {
                        _diagnostics.Error("VS0398", "Empty `\\( )` in a string: put the value to show inside, as in \"HP: \\(hp)\".",
                            SpanFrom(open, openLine, openCol));
                        _tokens.Add(Joint(TokenKind.String, ""));   // keeps `( )` an expression
                    }
                    int end = _pos, endLine = _line, endCol = _col;
                    Advance();                      // `)`
                    _tokens.Add(new Token(TokenKind.RParen, ")", null, SpanFrom(end, endLine, endCol)));
                }

                _tokens.Add(Joint(TokenKind.Plus, "+"));
                piecePos = _pos; pieceLine = _line; pieceCol = _col;
                continue;
            }
            if (Current == '\\')
            {
                Advance();
                sb.Append(Current switch
                {
                    'n' => '\n', 't' => '\t', 'r' => '\r',
                    '\\' => '\\', '"' => '"',
                    _ => Current
                });
                Advance();
                continue;
            }
            sb.Append(Advance());
        }

        if (AtEnd || Current == '\n')
        {
            if (!reported)
                _diagnostics.Error("VS0003", "Unterminated string literal.",
                    SpanFrom(startPos, startLine, startCol));
            if (interpolated) _tokens.Add(Joint(TokenKind.RParen, ")"));
            return;
        }

        Advance();                                  // closing quote
        Piece();
        if (interpolated) _tokens.Add(Joint(TokenKind.RParen, ")"));
    }

    /// The index of the `)` closing an interpolation whose expression starts at <paramref name="from"/>, on
    /// the same line, or -1. Parentheses nest, and a string inside — with interpolations of its own — is
    /// skipped whole. A comment cannot sit inside one: it would swallow the string's closing quote.
    private int ClosingParen(int from)
    {
        int depth = 0;
        for (int i = from; i < _src.Length; i++)
        {
            char c = _src[i];
            if (c == '\n') return -1;
            if (c == '/' && i + 1 < _src.Length && _src[i + 1] is '/' or '*') return -1;
            if (c == '"') { i = StringEnd(i); if (i < 0) return -1; continue; }
            if (c == '(') depth++;
            else if (c == ')') { if (depth == 0) return i; depth--; }
        }
        return -1;
    }

    /// The index of the quote closing the string that opens at <paramref name="quote"/>, or -1.
    private int StringEnd(int quote)
    {
        for (int i = quote + 1; i < _src.Length; i++)
        {
            char c = _src[i];
            if (c == '\n') return -1;
            if (c == '"') return i;
            if (c != '\\' || i + 1 >= _src.Length) continue;
            if (_src[i + 1] == '(') { i = ClosingParen(i + 2); if (i < 0) return -1; }
            else i++;                               // the escaped character
        }
        return -1;
    }

    /// The last unescaped quote at or after <paramref name="from"/> on this line, or -1.
    private int LastQuoteOnLine(int from)
    {
        int last = -1;
        for (int i = from; i < _src.Length && _src[i] != '\n'; i++)
        {
            if (_src[i] == '\\') { i++; continue; }
            if (_src[i] == '"') last = i;
        }
        return last;
    }

    private void ScanIdentifier(int startPos, int startLine, int startCol)
    {
        var sb = new StringBuilder();
        while (!AtEnd && (char.IsLetterOrDigit(Current) || Current == '_')) sb.Append(Advance());

        string text = sb.ToString();
        // Keywords are not scanned separately: scan an identifier, then look it up.
        var kind = Keywords.TryGetValue(text, out var kw) ? kw : TokenKind.Ident;
        _tokens.Add(new Token(kind, text, null, SpanFrom(startPos, startLine, startCol)));
    }

    private void ScanOperator(int startPos, int startLine, int startCol)
    {
        char c = Advance();
        TokenKind kind;

        switch (c)
        {
            case '{': kind = TokenKind.LBrace; break;
            case '}': kind = TokenKind.RBrace; break;
            case '(': kind = TokenKind.LParen; break;
            case ')': kind = TokenKind.RParen; break;
            case '[': kind = TokenKind.LBracket; break;
            case ']': kind = TokenKind.RBracket; break;
            case '?': kind = TokenKind.Question; break;
            case '.': kind = TokenKind.Dot; break;
            case ',': kind = TokenKind.Comma; break;
            case '|': kind = TokenKind.Pipe; break;
            // D24: `~` marks a COLLECTION — `~<set> members: Entity` declares one, `s.Guild.~members` uses it.
            case '~': kind = TokenKind.Tilde; break;
            case '*': kind = Match('=') ? TokenKind.StarEq : TokenKind.Star; break;
            case '/': kind = Match('=') ? TokenKind.SlashEq : TokenKind.Slash; break;
            case '%': kind = TokenKind.Mod; break;
            case '+': kind = Match('=') ? TokenKind.PlusEq : TokenKind.Plus; break;
            case ':': kind = TokenKind.Colon; break;
            case '=': kind = Match('=') ? TokenKind.Eq : TokenKind.Assign; break;
            case '<': kind = Match('=') ? TokenKind.Le : TokenKind.Lt; break;
            case '>': kind = Match('=') ? TokenKind.Ge : TokenKind.Gt; break;

            case '-':
                // Order matters: -> before -= before -
                if (Match('>')) kind = TokenKind.Arrow;
                else if (Match('=')) kind = TokenKind.MinusEq;
                else kind = TokenKind.Minus;
                break;

            // `!` is intentionally NOT a token — inequality is written `not (a == b)` (the `not` keyword),
            // which frees `!` for a future sigil (SYNTAX-DECISIONS D12).
            //
            // But it gets its OWN case, because the generic "Unexpected character" was the wrong shape
            // of error for the most common thing anyone types here. `x != 0` produced VS0005 on the
            // `!`, then the parser met a stray `=` and reported "Expected '{'" — two errors pointing at
            // a brace, and neither said the language has no `!=`. Someone who knows C spent half an
            // hour on that. The `=` is consumed so the cascade stops, and the message is the fix.
            case '!':
            {
                bool neq = Match('=');
                _diagnostics.Error("VS0006",
                    neq ? "VeinScript has no `!=` — write `not (a == b)`. Logic is spelled in words here."
                        : "VeinScript has no `!` — write `not x`. Logic is spelled in words here.",
                    SpanFrom(startPos, startLine, startCol));

                // RECOVER WITH THE TOKEN THE AUTHOR MEANT, so the parser sees a well-formed expression
                // and reports nothing further. Returning nothing here left `if x 0.0 {` behind, which
                // is a second error about a brace and a third about the closing `}` — the cascade this
                // case exists to stop. The program is already rejected by the error above, so `Ne`
                // being otherwise unreachable (D12) is preserved: it can only ever appear in a file
                // that will not compile.
                kind = neq ? TokenKind.Ne : TokenKind.KwNot;
                break;
            }

            default:
                _diagnostics.Error("VS0005", $"Unexpected character '{c}'.",
                    SpanFrom(startPos, startLine, startCol));
                return;
        }

        _tokens.Add(new Token(kind, _src[startPos.._pos], null,
            SpanFrom(startPos, startLine, startCol)));
    }

    private bool Match(char expected)
    {
        if (Current != expected) return false;
        Advance();
        return true;
    }
}
