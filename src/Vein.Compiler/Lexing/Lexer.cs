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
        ["random"] = TokenKind.KwRandom,       ["return"] = TokenKind.KwReturn,
        ["settled"] = TokenKind.KwSettled,     ["SF"] = TokenKind.KwSf,
        ["shape"] = TokenKind.KwShape,         ["shard"] = TokenKind.KwShard,
        ["shared"] = TokenKind.KwShared,       ["start"] = TokenKind.KwStart,
        ["sync"] = TokenKind.KwSync,           ["target"] = TokenKind.KwTarget,
        ["tick"] = TokenKind.KwTick,           ["to"] = TokenKind.KwTo,
        ["transform"] = TokenKind.KwTransform, ["true"] = TokenKind.KwTrue,
        ["unattach"] = TokenKind.KwUnattach,   ["unmark"] = TokenKind.KwUnmark,
        ["unmute"] = TokenKind.KwUnmute,       ["use"] = TokenKind.KwUse,
        ["var"] = TokenKind.KwVar,             ["when"] = TokenKind.KwWhen,

        // Milestone 2 additions (docs/KEYWORDS.md §2). `push` intentionally removed (D4).
        ["fn"] = TokenKind.KwFn,               ["type"] = TokenKind.KwType,
        ["enum"] = TokenKind.KwEnum,           ["if"] = TokenKind.KwIf,
        ["while"] = TokenKind.KwWhile,         ["repeat"] = TokenKind.KwRepeat,
        ["break"] = TokenKind.KwBreak,         ["continue"] = TokenKind.KwContinue,
        ["match"] = TokenKind.KwMatch,         ["ShardView"] = TokenKind.KwShardView,
        ["Entity"] = TokenKind.KwEntity,       ["app"] = TokenKind.KwApp,
    };

    private readonly string _src;
    private readonly string _file;
    private readonly DiagnosticBag _diagnostics;
    private readonly List<Token> _tokens = new();

    private int _pos;
    private int _line = 1;
    private int _col = 1;

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
        while (!AtEnd)
        {
            char c = Current;

            if (c == ' ' || c == '\t' || c == '\r') { Advance(); continue; }

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
                continue;
            }

            if (c == '/' && Peek() == '/') { SkipLineComment(); continue; }
            if (c == '/' && Peek() == '*') { SkipBlockComment(); continue; }

            int startPos = _pos, startLine = _line, startCol = _col;

            if (c == '$' || c == '@' || c == '#' || c == '&') { ScanSigilRef(startPos, startLine, startCol); continue; }
            if (char.IsDigit(c)) { ScanNumber(startPos, startLine, startCol); continue; }
            if (c == '"') { ScanString(startPos, startLine, startCol); continue; }
            if (char.IsLetter(c) || c == '_') { ScanIdentifier(startPos, startLine, startCol); continue; }

            ScanOperator(startPos, startLine, startCol);
        }

        _tokens.Add(new Token(TokenKind.EndOfFile, "", null, new SourceSpan(_file, _line, _col, 0)));
        return _tokens;
    }

    // ---- newline rule ----------------------------------------------------

    private bool CanEndStatement()
    {
        if (_tokens.Count == 0) return false;
        return _tokens[^1].Kind switch
        {
            TokenKind.Int or TokenKind.Float or TokenKind.Percent or TokenKind.String
                or TokenKind.Ident or TokenKind.RParen or TokenKind.RBrace or TokenKind.RBracket
                or TokenKind.ShapeRef or TokenKind.EventRef or TokenKind.MarkRef or TokenKind.BuilderRef
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

    private void ScanString(int startPos, int startLine, int startCol)
    {
        Advance();                                  // opening quote
        var sb = new StringBuilder();

        while (!AtEnd && Current != '"')
        {
            if (Current == '\n')
            {
                _diagnostics.Error("VS0003", "Unterminated string literal.",
                    SpanFrom(startPos, startLine, startCol));
                return;
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

        if (AtEnd)
        {
            _diagnostics.Error("VS0003", "Unterminated string literal.",
                SpanFrom(startPos, startLine, startCol));
            return;
        }

        Advance();                                  // closing quote
        _tokens.Add(new Token(TokenKind.String, sb.ToString(), sb.ToString(),
            SpanFrom(startPos, startLine, startCol)));
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
            case '*': kind = Match('=') ? TokenKind.StarEq : TokenKind.Star; break;
            case '/': kind = Match('=') ? TokenKind.SlashEq : TokenKind.Slash; break;
            case '%': kind = TokenKind.Mod; break;
            case '+': kind = Match('=') ? TokenKind.PlusEq : TokenKind.Plus; break;
            case ':': kind = Match(':') ? TokenKind.Scope : TokenKind.Colon; break;
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
            // which frees `!` for a future sigil.

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
