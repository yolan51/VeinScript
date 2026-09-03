using System.Globalization;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Lexing;

namespace Vein.Compiler.Parsing;

/// Recursive-descent parser: tokens -> AST. One method per production in docs/LANGUAGE.md §7.
/// Text is only one way to author IOP definitions; the AST it produces is the same structure the
/// registry stores. Kept intentionally lean.
public sealed class Parser
{
    private readonly IReadOnlyList<Token> _t;
    private readonly DiagnosticBag _diag;
    private int _i;

    private sealed class ParseError : Exception { }

    public Parser(IReadOnlyList<Token> tokens, DiagnosticBag diagnostics)
    {
        _t = tokens;
        _diag = diagnostics;
    }

    private Token Cur => _t[_i];
    private Token Peek(int n = 1) => _t[Math.Min(_i + n, _t.Count - 1)];
    private bool AtEnd => Cur.Kind == TokenKind.EndOfFile;
    private SourceSpan Here => Cur.Span;

    private Token Advance() => _t[_i++];
    private bool Check(TokenKind k) => Cur.Kind == k;
    private bool Match(TokenKind k) { if (Check(k)) { _i++; return true; } return false; }

    private Token Expect(TokenKind k, string what)
    {
        if (Check(k)) return Advance();
        _diag.Error("VS0100", $"Expected {what}, found {Cur.Kind} '{Cur.Text}'.", Here);
        throw new ParseError();
    }

    private static bool IsKeyword(TokenKind k) => k >= TokenKind.KwAnd && k <= TokenKind.KwShardView;

    /// A name position (member after '.', a field name, a struct-field name) accepts an identifier
    /// OR any keyword spelled as text — so common words like `from`, `class`, `count`, `to` can be
    /// field/member names without colliding with keywords.
    private Token ExpectName(string what)
    {
        if (Check(TokenKind.Ident) || IsKeyword(Cur.Kind)) return Advance();
        _diag.Error("VS0100", $"Expected {what}, found {Cur.Kind} '{Cur.Text}'.", Here);
        throw new ParseError();
    }

    /// Skip virtual newline terminators (and stray ones between members).
    private void SkipTerms() { while (Check(TokenKind.Term)) _i++; }

    // ---- entry ----------------------------------------------------------

    public CompilationUnit ParseUnit()
    {
        var start = Here;
        var bundles = new List<BundleDecl>();
        var apps = new List<AppDecl>();
        SkipTerms();
        while (!AtEnd)
        {
            try
            {
                if (Check(TokenKind.KwBundle)) bundles.Add(ParseBundle());
                else if (Check(TokenKind.KwApp)) apps.Add(ParseApp());
                else { _diag.Error("VS0101", $"Expected 'bundle' or 'app', found '{Cur.Text}'.", Here); Synchronize(); }
            }
            catch (ParseError) { Synchronize(); }
            SkipTerms();
        }
        return new CompilationUnit(bundles, start) { Apps = apps };
    }

    /// A bundle FRAGMENT — the declarations in a `publicators/` or `shards/` file, with no `bundle`
    /// wrapper. The folder it lives in says which bundle it joins and whether it is API (`exported: true`,
    /// so `shared("…")` is meaningful) or behaviour. Merged by BundleLoader.
    public List<Decl> ParseFragment(bool exported) => ParseDeclList(exported, TokenKind.EndOfFile);

    private BundleDecl ParseBundle()
    {
        var start = Here;
        Expect(TokenKind.KwBundle, "'bundle'");
        string name = Expect(TokenKind.Ident, "bundle name").Text;
        // Optional author/pseudo: `bundle Combat by yolan` — the root of its qualified name.
        string? author = Match(TokenKind.KwBy) ? ExpectName("author").Text : null;
        Expect(TokenKind.LBrace, "'{'");
        var members = ParseDeclList(exported: false, until: TokenKind.RBrace);
        Expect(TokenKind.RBrace, "'}'");
        return new BundleDecl(name, members, start) { Author = author };
    }

    /// `app N { load "path" … }` — the loaded-bundle set. No entry point (IOP is reactive).
    private AppDecl ParseApp()
    {
        var start = Here;
        Expect(TokenKind.KwApp, "'app'");
        string name = Expect(TokenKind.Ident, "app name").Text;
        Expect(TokenKind.LBrace, "'{'");
        var loads = new List<AppLoad>();
        SkipTerms();
        while (!Check(TokenKind.RBrace) && !AtEnd)
        {
            // `load` is a contextual keyword inside the app body. An app has no boot event of its own —
            // it composes bundles; each bundle's own `start` is its entry point.
            if ((Check(TokenKind.Ident) || IsKeyword(Cur.Kind)) && Cur.Text == "load")
            {
                var ls = Here;
                Advance();
                string p = Expect(TokenKind.String, "a file path string after 'load'").Value as string ?? "";
                // Optional `start { … }` / `start ?` override of the loaded bundle's start payload.
                var fields = new List<FieldInit>();
                bool fill = false;
                bool hasStart = Check(TokenKind.KwStart) && Peek(1).Kind != TokenKind.EventRef;
                if (hasStart) { Advance(); (fields, fill) = ParseEmitBody(); }
                loads.Add(new AppLoad(p, fields, fill, hasStart, ls));
            }
            else { _diag.Error("VS0106", $"Expected 'load', found '{Cur.Text}'.", Here); Advance(); }
            SkipTerms();
        }
        Expect(TokenKind.RBrace, "'}'");
        return new AppDecl(name, loads, start);
    }

    /// Parse declarations until `until`. Handles `shared("doc")` prefixes and `publicator` groups.
    private List<Decl> ParseDeclList(bool exported, TokenKind until)
    {
        var members = new List<Decl>();
        SkipTerms();
        while (!Check(until) && !AtEnd)
        {
            int before = _i;
            try
            {
                var docStart = Here;
                string? doc = TryParseDoc();   // non-null ⟺ a `shared("…")` annotation ⇒ cross-bundle public
                // `shared` is only meaningful inside a publicator (a bundle's public grouping); reject it
                // elsewhere so "what's shared across bundles" is always chosen there.
                if (doc is not null && !exported)
                    _diag.Error("VS0107", "`shared(\"…\")` is only valid inside a `publicator`.", docStart);
                var d = ParseDecl(exported);
                if (d is not null)
                {
                    // A `shard` is bundle BEHAVIOUR — it comes with the bundle and runs when the bundle is
                    // loaded; it is never part of a publicator's cross-bundle API (which holds shared
                    // shapes/events/builders). So a shard belongs at the bundle level, not in a publicator.
                    if (d is ShardDecl && exported)
                        _diag.Error("VS0108", "a `shard` is bundle behaviour — declare it at the bundle level, not inside a `publicator`.", d.Span);
                    members.Add(d with { Doc = doc ?? d.Doc, Exported = exported || d.Exported, Shared = (doc is not null && exported) || d.Shared });
                }
            }
            catch (ParseError) { Synchronize(); }
            if (_i == before && !AtEnd) Advance();          // guarantee progress; never hang on bad input
            SkipTerms();
        }
        return members;
    }

    private string? TryParseDoc()
    {
        if (!Check(TokenKind.KwShared)) return null;
        Advance();
        Expect(TokenKind.LParen, "'('");
        string doc = Expect(TokenKind.String, "doc string").Text;
        Expect(TokenKind.RParen, "')'");
        SkipTerms();
        return doc;
    }

    private Decl? ParseDecl(bool exported)
    {
        switch (Cur.Kind)
        {
            case TokenKind.KwUse: return ParseUse();
            case TokenKind.KwPublicator: return ParsePublicator();
            case TokenKind.KwShape: return ParseShape();
            case TokenKind.KwMark: return ParseMarkDecl();
            case TokenKind.KwType: return ParseType();
            case TokenKind.KwEvent: return ParseEvent();
            case TokenKind.KwShard: return ParseShard();
            case TokenKind.KwShardView: return ParseView();
            case TokenKind.KwBridge: return ParseBridge();
            case TokenKind.KwSf: return ParseFunc();
            case TokenKind.KwFn: return ParseFunc();
            case TokenKind.KwBuilder: return ParseBuilder();
            case TokenKind.KwLet: return ParseVar(mutable: false);
            case TokenKind.KwVar: return ParseVar(mutable: true);
            case TokenKind.KwStart: return ParseStart();   // bundle-level boot event
            default:
                _diag.Error("VS0102", $"Unexpected '{Cur.Text}' at declaration level.", Here);
                throw new ParseError();
        }
    }

    private UseDecl ParseUse()
    {
        var s = Here; Advance();
        string name = Expect(TokenKind.Ident, "bundle name").Text;
        string? alias = Match(TokenKind.KwAs) ? Expect(TokenKind.Ident, "alias").Text : null;
        return new UseDecl(name, alias, s);
    }

    private PublicatorDecl ParsePublicator()
    {
        var s = Here; Advance();
        string name = Expect(TokenKind.Ident, "publicator name").Text;
        Expect(TokenKind.LBrace, "'{'");
        var members = ParseDeclList(exported: true, until: TokenKind.RBrace);
        Expect(TokenKind.RBrace, "'}'");
        return new PublicatorDecl(name, members, s);
    }

    /// `mark #Enemy` — a mark declared rather than merely typed. No body: a mark has no fields.
    ///
    /// Unambiguous with the `mark e #T` STATEMENT because statements never appear at declaration level;
    /// `mark` there was a VS0102 until now.
    private MarkDecl ParseMarkDecl()
    {
        var s = Here; Advance();
        string name = Expect(TokenKind.MarkRef, "#MarkName").Text;
        return new MarkDecl(name, s);
    }

    private ShapeDecl ParseShape()
    {
        var s = Here; Advance();
        string name = Expect(TokenKind.ShapeRef, "$ShapeName").Text;
        Expect(TokenKind.LBrace, "'{'");
        var members = new List<Node>();
        SkipTerms();
        while (!Check(TokenKind.RBrace) && !AtEnd)
        {
            if (Check(TokenKind.KwEnum)) members.Add(ParseEnum());
            else { members.Add(ParseField()); Match(TokenKind.Comma); }   // comma- or newline-separated
            SkipTerms();
        }
        Expect(TokenKind.RBrace, "'}'");
        return new ShapeDecl(name, members, s);
    }

    private TypeDecl ParseType()
    {
        var s = Here; Advance();
        string name = Expect(TokenKind.Ident, "type name").Text;
        Expect(TokenKind.LBrace, "'{'");
        var fields = ParseFieldList();
        Expect(TokenKind.RBrace, "'}'");
        return new TypeDecl(name, fields, s);
    }

    private EventDecl ParseEvent()
    {
        var s = Here; Advance();
        string name = Expect(TokenKind.EventRef, "@EventName").Text;
        return new EventDecl(name, ParseSigBody(), s);
    }

    /// The shared `{ members }` body of an event/builder: fields, vars, and `$Shape` includes,
    /// separated by whitespace / newline / optional comma. `=` marks a member optional.
    private List<Node> ParseSigBody()
    {
        Expect(TokenKind.LBrace, "'{'");
        var members = new List<Node>();
        SkipTerms();
        while (!Check(TokenKind.RBrace) && !AtEnd)
        {
            members.Add(ParseSigMember());
            Match(TokenKind.Comma);
            SkipTerms();
        }
        Expect(TokenKind.RBrace, "'}'");
        return members;
    }

    private Node ParseSigMember()
    {
        var s = Here;
        // `mark #A #B` — the marks the identity this builder builds will wear. No target: inside a
        // template the target is the identity being built. Its presence is also what tells `bring` this
        // builder constructs an identity rather than emitting a fragment or an event.
        if (Match(TokenKind.KwMark))
        {
            var marks = new List<string>();
            while (Check(TokenKind.MarkRef)) marks.Add(Advance().Text);
            if (marks.Count == 0)
                _diag.Error("VS0100", $"Expected a #Mark after `mark`, found {Cur.Kind} '{Cur.Text}'.", Here);
            return new MarkMember(marks, s);
        }
        if (Check(TokenKind.ShapeRef))                                   // $Shape or $Shape.field
        {
            string shape = Advance().Text;
            string? field = Match(TokenKind.Dot) ? ExpectName("field name").Text : null;
            Expr? sd = Match(TokenKind.Assign) ? ParseExpr() : null;
            return new ShapeInclude(shape, field, sd, s);
        }
        // `*Author.Bundle.Publicator.$Shape[.field]` — reuse a SHARED shape from another bundle as a
        // field group. Same qualified form as `emit`/`hear`/`bring`; only `shared` shapes resolve.
        if (Check(TokenKind.Star)) return ParseQualifiedShapeInclude();
        bool isVar = Match(TokenKind.KwVar);                             // optional `var`
        string name = ExpectName("member name").Text;
        TypeRef? type = Match(TokenKind.Colon) ? ParseTypeRef() : null;  // type optional (inferred)
        string? fold = Match(TokenKind.KwFolds) ? Expect(TokenKind.Ident, "fold reducer").Text : null;
        Expr? def = Match(TokenKind.Assign) ? ParseExpr() : null;        // `=` ⇒ defaulted
        return new FieldDecl(name, type, fold, def, s) { IsVar = isVar };
    }

    private EnumDecl ParseEnum()
    {
        var s = Here; Advance();
        string name = Expect(TokenKind.Ident, "enum name").Text;
        Expect(TokenKind.LBrace, "'{'");
        var cases = new List<string> { Expect(TokenKind.Ident, "enum case").Text };
        while (Match(TokenKind.Comma)) { SkipTerms(); cases.Add(Expect(TokenKind.Ident, "enum case").Text); }
        SkipTerms();
        Expect(TokenKind.RBrace, "'}'");
        return new EnumDecl(name, cases, s);
    }

    private List<FieldDecl> ParseFieldList()
    {
        var fields = new List<FieldDecl>();
        SkipTerms();
        while (!Check(TokenKind.RBrace) && !AtEnd)
        {
            fields.Add(ParseField());
            if (!Match(TokenKind.Comma)) { /* newline-separated ok */ }
            SkipTerms();
        }
        return fields;
    }

    private FieldDecl ParseField()
    {
        var s = Here;
        string name = ExpectName("field name").Text;
        Expect(TokenKind.Colon, "':'");
        var type = ParseTypeRef();
        string? fold = Match(TokenKind.KwFolds) ? Expect(TokenKind.Ident, "fold reducer").Text : null;
        Expr? def = Match(TokenKind.Assign) ? ParseExpr() : null;   // `name: Type = default`
        return new FieldDecl(name, type, fold, def, s);
    }

    private TypeRef ParseTypeRef()
    {
        var s = Here;
        // `Entity` is a keyword but also the ECS entity type, so accept it in type position.
        string name = Check(TokenKind.KwEntity) ? Advance().Text : Expect(TokenKind.Ident, "type").Text;
        var args = new List<TypeRef>();
        if (Match(TokenKind.Lt))
        {
            args.Add(ParseTypeRef());
            while (Match(TokenKind.Comma)) args.Add(ParseTypeRef());
            Expect(TokenKind.Gt, "'>'");
        }
        bool nullable = false; // `T?` — no '?' token yet; reserved for later
        return new TypeRef(name, args, nullable, s);
    }

    /// SF is the only function kind. It never returns a value — it emits events (auto origin/source).
    /// `SF name(params) { … }` — a shard function: emits events, returns nothing.
    /// `fn name(params) -> T { … return e }` — a function: computes and returns a value.
    /// The two are deliberately distinct (see SYNTAX-DECISIONS D6): behaviour vs computation.
    private FuncDecl ParseFunc()
    {
        var s = Here;
        bool isSf = Check(TokenKind.KwSf);
        Advance();
        string name = Expect(TokenKind.Ident, isSf ? "SF name" : "fn name").Text;
        Expect(TokenKind.LParen, "'('");
        var ps = new List<Param>();
        if (!Check(TokenKind.RParen))
        {
            ps.Add(ParseParam());
            while (Match(TokenKind.Comma)) ps.Add(ParseParam());
        }
        Expect(TokenKind.RParen, "')'");

        TypeRef? ret = null;
        if (Check(TokenKind.Arrow))
        {
            if (isSf) _diag.Error("VS0105", "SF has no return type; it emits events instead of returning.", Here);
            else { Advance(); ret = ParseTypeRef(); }
        }

        // `return` is legal only inside an `fn`; an SF emits instead (VS0107).
        bool wasInFn = _inFn;
        _inFn = !isSf;
        var body = ParseBlock();
        _inFn = wasInFn;

        return new FuncDecl(isSf, name, ps, ret, body, s);
    }

    /// True while parsing an `fn` body, where `return` is allowed.
    private bool _inFn;

    private Param ParseParam()
    {
        var s = Here;
        // Expect(Ident), NOT ExpectName: unlike a field name (which is only ever read as `x.to`), a
        // parameter must be referenceable as a BARE name, and a keyword cannot be one in expression
        // position. Admitting `to`/`by` here would let you declare a parameter you could never use.
        string name = Expect(TokenKind.Ident, "parameter name").Text;
        Expect(TokenKind.Colon, "':'");
        return new Param(name, ParseTypeRef(), s);
    }

    private VarDecl ParseVar(bool mutable)
    {
        var s = Here; Advance();
        string name = Expect(TokenKind.Ident, "name").Text;
        TypeRef? type = Match(TokenKind.Colon) ? ParseTypeRef() : null;
        Expr? init = Match(TokenKind.Assign) ? ParseExpr() : null;
        return new VarDecl(mutable, name, type, init, s);
    }

    // ---- shards ---------------------------------------------------------

    /// Parse the shapes/marks a First-Class object carries: `$Shape #Mark …` before its `{`.
    private (List<string> Shapes, List<string> Marks) ParseCarried()
    {
        var shapes = new List<string>();
        var marks = new List<string>();
        while (Check(TokenKind.ShapeRef) || Check(TokenKind.MarkRef))
        {
            if (Check(TokenKind.ShapeRef)) shapes.Add(Advance().Text);
            else marks.Add(Advance().Text);
        }
        return (shapes, marks);
    }

    private List<Node> ParseFirstClassBody()
    {
        Expect(TokenKind.LBrace, "'{'");
        var members = new List<Node>();
        SkipTerms();
        while (!Check(TokenKind.RBrace) && !AtEnd)
        {
            members.Add(ParseShardMember());
            SkipTerms();
        }
        Expect(TokenKind.RBrace, "'}'");
        return members;
    }

    private ShardDecl ParseShard()
    {
        var s = Here; Advance();
        string name = Expect(TokenKind.Ident, "shard name").Text;
        var (shapes, marks) = ParseCarried();
        return new ShardDecl(name, ParseFirstClassBody(), s) { CarriedShapes = shapes, CarriedMarks = marks };
    }

    private BuilderDecl ParseBuilder()
    {
        var s = Here; Advance();
        // `builder Name { members }` — same body as an event; the output field (markup/code/css)
        // carries the template and determines the kind.
        string name = Expect(TokenKind.Ident, "builder name").Text;
        return new BuilderDecl(name, ParseSigBody(), s);
    }

    private ViewDecl ParseView()
    {
        var s = Here; Advance();
        string name = Expect(TokenKind.Ident, "ShardView name").Text;
        var (carriedShapes, carriedMarks) = ParseCarried();
        Expect(TokenKind.LBrace, "'{'");
        var members = new List<Node>();
        SkipTerms();
        while (!Check(TokenKind.RBrace) && !AtEnd)
        {
            if (Check(TokenKind.KwHear)) members.Add(ParseHear());
            else if (Check(TokenKind.KwVar)) members.Add(ParseVar(mutable: true));
            else if (Check(TokenKind.KwLet)) members.Add(ParseVar(mutable: false));
            else if (Check(TokenKind.KwSf) || Check(TokenKind.KwFn)) members.Add(ParseFunc());
            else { _diag.Error("VS0108", $"Unexpected '{Cur.Text}' in ShardView body (expected hear/var/let/SF/fn).", Here); throw new ParseError(); }
            SkipTerms();
        }
        Expect(TokenKind.RBrace, "'}'");
        return new ViewDecl(name, members, s) { CarriedShapes = carriedShapes, CarriedMarks = carriedMarks };
    }

    private BridgeDecl ParseBridge()
    {
        var s = Here; Advance();
        string name = Expect(TokenKind.Ident, "bridge name").Text;
        var (shapes, marks) = ParseCarried();
        return new BridgeDecl(name, ParseFirstClassBody(), s) { CarriedShapes = shapes, CarriedMarks = marks };
    }

    private Node ParseShardMember()
    {
        switch (Cur.Kind)
        {
            case TokenKind.KwEach:
            case TokenKind.KwSettled: return ParseSchedule();
            case TokenKind.KwHear: return ParseHear();
            case TokenKind.KwSf: return ParseFunc();
            case TokenKind.KwFn: return ParseFunc();     // a shard-local computation helper
            case TokenKind.KwLet: return ParseVar(mutable: false);
            case TokenKind.KwVar: return ParseVar(mutable: true);
            default:
                // `run once` / `every N` use contextual words (so `frame`/`every` stay valid field names).
                if (Check(TokenKind.Ident) && (Cur.Text == "run" || Cur.Text == "every")) return ParseSchedule();
                _diag.Error("VS0103", $"Unexpected '{Cur.Text}' in shard body.", Here);
                throw new ParseError();
        }
    }

    /// A shard behaviour block: WHEN it runs (the schedule) wraps a body. The entity `target` query
    /// nests INSIDE, as a statement. `run once`, `each tick`, `each frame`, `every N`, `settled`.
    private ScheduleBlock ParseSchedule()
    {
        var s = Here;
        ScheduleKind kind;
        double? interval = null;
        if (Match(TokenKind.KwEach))
        {
            if (Match(TokenKind.KwTick)) kind = ScheduleKind.Tick;
            else if (Check(TokenKind.Ident) && Cur.Text == "frame") { Advance(); kind = ScheduleKind.Frame; }
            else { _diag.Error("VS0109", "expected `tick` or `frame` after `each`.", Here); throw new ParseError(); }
        }
        else if (Match(TokenKind.KwSettled)) kind = ScheduleKind.Settled;
        else if (Check(TokenKind.Ident) && Cur.Text == "run") { Advance(); ExpectContextual("once"); kind = ScheduleKind.Once; }
        else if (Check(TokenKind.Ident) && Cur.Text == "every") { Advance(); kind = ScheduleKind.Every; interval = ParseSeconds(); }
        else { _diag.Error("VS0109", "expected a schedule (`run once`, `each tick`, `each frame`, `every N`, `settled`).", Here); throw new ParseError(); }
        return new ScheduleBlock(kind, interval, ParseBlock(), s);
    }

    /// Consume a contextual word (ident spelled `word`, e.g. `once`).
    private void ExpectContextual(string word)
    {
        if ((Check(TokenKind.Ident) || IsKeyword(Cur.Kind)) && Cur.Text == word) { Advance(); return; }
        _diag.Error("VS0100", $"Expected `{word}`, found {Cur.Kind} '{Cur.Text}'.", Here);
        throw new ParseError();
    }

    /// A number of seconds after `every` (int or float): `every 1.0`, `every 0.5`.
    private double ParseSeconds()
    {
        if (Check(TokenKind.Int) || Check(TokenKind.Float))
            return Advance().Value switch { long l => l, int i => i, double d => d, _ => 0.0 };
        _diag.Error("VS0110", "expected a number of seconds after `every` (e.g. `every 1.0`).", Here);
        throw new ParseError();
    }

    private HearBlock ParseHear()
    {
        var s = Here; Advance();
        var (evPath, ev) = ParseEventRef("@EventName");
        Expect(TokenKind.KwAs, "'as'");
        string bind = Expect(TokenKind.Ident, "binding").Text;
        // Optional audience barrier: only react to emitters carrying these shapes/marks.
        var audShapes = new List<string>();
        var audMarks = new List<string>();
        if (Match(TokenKind.KwAudience))
            while (Check(TokenKind.ShapeRef) || Check(TokenKind.MarkRef))
            {
                if (Check(TokenKind.ShapeRef)) audShapes.Add(Advance().Text);
                else audMarks.Add(Advance().Text);
            }
        return new HearBlock(ev, bind, audShapes, audMarks, ParseBlock(), s) { EventPath = evPath };
    }

    // ---- statements -----------------------------------------------------

    private Block ParseBlock()
    {
        var s = Here;
        Expect(TokenKind.LBrace, "'{'");
        var stmts = new List<Stmt>();
        SkipTerms();
        while (!Check(TokenKind.RBrace) && !AtEnd)
        {
            int before = _i;
            try { stmts.Add(ParseStmt()); }
            catch (ParseError) { SynchronizeInBlock(); }
            if (_i == before && !AtEnd) Advance();           // guarantee progress; never hang on bad input
            SkipTerms();
        }
        Expect(TokenKind.RBrace, "'}'");
        return new Block(stmts, s);
    }

    private Stmt ParseStmt()
    {
        switch (Cur.Kind)
        {
            case TokenKind.KwLet: { var v = ParseVar(mutable: false); return new LocalVarStmt(v, v.Span); }
            case TokenKind.KwVar: { var v = ParseVar(mutable: true); return new LocalVarStmt(v, v.Span); }
            case TokenKind.KwIf: return ParseIf();
            case TokenKind.KwWhile: return ParseWhile();
            case TokenKind.KwTarget: return ParseTargetOrQuery();
            // `ordered by k { … }` — contextual, like `run once` and `every N`, so `ordered` stays a
            // name a program may use. Recognised only when `by` follows it at statement position.
            case TokenKind.Ident when Cur.Text == "ordered" && Peek(1).Kind == TokenKind.KwBy: return ParseOrdered();
            case TokenKind.KwRepeat: return ParseRepeat();
            case TokenKind.KwMatch: return ParseMatch();
            case TokenKind.KwReturn:
            {
                if (!_inFn)
                {
                    _diag.Error("VS0107", "`return` is only valid inside an `fn`; an SF emits events instead of returning.", Here);
                    throw new ParseError();
                }
                var rs = Here; Advance();
                // `return` alone (void) vs `return expr` — a terminator or `}` means no value.
                Expr? val = Check(TokenKind.Term) || Check(TokenKind.RBrace) ? null : ParseExpr();
                return new ReturnStmt(val, rs);
            }
            case TokenKind.KwBreak: { var s = Here; Advance(); return new BreakStmt(s); }
            case TokenKind.KwContinue: { var s = Here; Advance(); return new ContinueStmt(s); }
            case TokenKind.KwMark: return ParseMark(remove: false);
            case TokenKind.KwUnmark: return ParseMark(remove: true);
            case TokenKind.KwEmit: return ParseEmit();
            case TokenKind.KwDestroy: { var s = Here; Advance(); return new DestroyStmt(ParseExpr(), s); }
            case TokenKind.KwAttach: return ParseAttach(remove: false);
            case TokenKind.KwUnattach: return ParseAttach(remove: true);
            case TokenKind.KwChance: return ParseChance();
            case TokenKind.KwBring: return ParseBring();
            default: return ParseAssignOrExpr();
        }
    }

    private IfStmt ParseIf()
    {
        var s = Here; Advance();
        var cond = ParseExpr();
        var then = ParseBlock();
        Node? els = null;
        if (Match(TokenKind.KwElse))
            els = Check(TokenKind.KwIf) ? ParseIf() : ParseBlock();
        return new IfStmt(cond, then, els, s);
    }

    private WhileStmt ParseWhile()
    {
        var s = Here; Advance();
        var cond = ParseExpr();
        return new WhileStmt(cond, ParseBlock(), s);
    }

    /// `target $Shape #Mark as self { … }` (a typed identity query → QueryStmt) or `target <expr> as x
    /// { … }` (iterate an expression → TargetStmt). Distinguished by a leading `$`/`#`.
    private Stmt ParseTargetOrQuery()
    {
        var s = Here; Advance();   // 'target'
        if (Check(TokenKind.ShapeRef) || Check(TokenKind.MarkRef))
        {
            var comps = new List<string>();
            var tags = new List<string>();
            while (Check(TokenKind.ShapeRef) || Check(TokenKind.MarkRef))
            {
                if (Check(TokenKind.ShapeRef)) comps.Add(Advance().Text);
                else tags.Add(Advance().Text);
            }
            // `by Shape.field` — optional, and it goes before `as` so the query reads as one clause:
            // what to match, how to order it, what to call each one. The shape is named explicitly
            // rather than inferred, because a multi-shape query has more than one candidate.
            string? orderShape = null, orderField = null;
            if (Match(TokenKind.KwBy))
            {
                orderShape = ExpectName("a shape name after 'by'").Text;
                Expect(TokenKind.Dot, "'.' after the shape name");
                orderField = ExpectName("a field name").Text;
            }

            Expect(TokenKind.KwAs, "'as'");
            string bind = Expect(TokenKind.Ident, "binding name").Text;
            return new QueryStmt(comps, tags, bind, ParseBlock(), s)
                { OrderShape = orderShape, OrderField = orderField };
        }
        var src = ParseExpr();
        Expect(TokenKind.KwAs, "'as'");
        string b = Expect(TokenKind.Ident, "binding").Text;

        // `as row: $Row` — an OPTIONAL ascription saying what shape the records have.
        //
        // A collection is the one binding whose element type nothing can infer: the list came from
        // `fromJson`, a query reply, or a field of a parsed document, and none of those carry a shape.
        // So `row.title` was unchecked and untyped — in database code, where field names come from a
        // schema someone else controls and drift without warning.
        //
        // The author knows: they asked for those columns. This is where they say so, once, using the
        // shape they have already declared for the same records.
        string? asShape = null;
        if (Match(TokenKind.Colon))
            asShape = Check(TokenKind.ShapeRef) ? Advance().Text : ExpectName("a $Shape after ':'").Text;

        return new TargetStmt(src, b, ParseBlock(), s) { AsShape = asShape };
    }


    /// `ordered by rank { bring A(…)  bring B(…) }` — run the brings sorted by one of their arguments,
    /// rather than in the order written.
    ///
    /// This is for the case an ordered QUERY cannot reach: a `bring` that emits a fragment produces its
    /// @Html the moment it runs, with no identity to query later, so the call order IS the output order.
    /// Sorting identities does nothing for it.
    private OrderedStmt ParseOrdered()
    {
        var s = Here; Advance();                       // 'ordered'
        Expect(TokenKind.KwBy, "'by'");

        // `&Row.rank` names the builder the key belongs to; a bare `rank` resolves per bring, which is
        // what lets one block hold several builders that each have that parameter. Qualifying says
        // WHICH builder's parameter is meant, and is checked against every bring in the block.
        string? builder = null, shape = null;
        if (Check(TokenKind.BuilderRef))
        {
            builder = Advance().Text;
            Expect(TokenKind.Dot, "'.' after the builder name");

            // `&Row.$Row.rank` — the optional middle segment says WHICH include contributed the
            // parameter. It earns its place: `builder Both { $A $B }` where both shapes carry `rank`
            // gives two parameters of that name, and without this there is no way to say which.
            if (Check(TokenKind.ShapeRef))
            {
                shape = Advance().Text;
                Expect(TokenKind.Dot, "'.' after the shape name");
            }
        }
        string key = ExpectName("the argument name to order by").Text;

        Expect(TokenKind.LBrace, "'{'");
        var body = new List<Stmt>();
        SkipTerms();
        while (!Check(TokenKind.RBrace) && !AtEnd)
        {
            // `target` is admitted alongside `bring` because the ordering people actually need is over
            // data, and data arrives as a list: one written `bring` inside a loop becomes N brings, and
            // the count is not known until the list is. Restricting this block to literal brings meant
            // `ordered by` could sort a menu you typed out and nothing you fetched.
            if (Check(TokenKind.KwBring)) body.Add(ParseBring());
            else if (Check(TokenKind.KwTarget)) body.Add(ParseTargetOrQuery());
            else
            {
                _diag.Error("VS0223", "`ordered by` holds `bring` statements and the `target` loops that " +
                                      "produce them — it reorders brings, and anything else at this level " +
                                      "has no place in that order.", Here);
                throw new ParseError();
            }
            SkipTerms();
        }
        Expect(TokenKind.RBrace, "'}'");
        return new OrderedStmt(key, body, s) { Builder = builder, Shape = shape };
    }
    private RepeatStmt ParseRepeat()
    {
        var s = Here; Advance();
        var count = ParseExpr();
        string? var = Match(TokenKind.KwAs) ? Expect(TokenKind.Ident, "counter").Text : null;
        return new RepeatStmt(count, var, ParseBlock(), s);
    }

    private MatchStmt ParseMatch()
    {
        var s = Here; Advance();
        var subject = ParseExpr();
        Expect(TokenKind.LBrace, "'{'");
        var arms = new List<MatchArm>();
        Block? els = null;
        SkipTerms();
        while (!Check(TokenKind.RBrace) && !AtEnd)
        {
            if (Match(TokenKind.KwElse)) { els = ParseBlock(); }
            else
            {
                var a = Here;
                Expect(TokenKind.KwWhen, "'when'");
                // `when North` (an enum case) or `when #Alpha` (a mark) — a mark is the pattern that
                // makes `match here() { … }` a role switch on which console this process is.
                bool isMark = Check(TokenKind.MarkRef);
                string caseName = isMark ? Advance().Text : Expect(TokenKind.Ident, "case name").Text;
                arms.Add(new MatchArm(caseName, ParseBlock(), a, isMark));
            }
            SkipTerms();
        }
        Expect(TokenKind.RBrace, "'}'");
        return new MatchStmt(subject, arms, els, s);
    }

    private MarkStmt ParseMark(bool remove)
    {
        var s = Here; Advance();
        var target = ParseExpr();
        string mark = Expect(TokenKind.MarkRef, "#Mark").Text;
        return new MarkStmt(remove, target, mark, s);
    }

    private EmitStmt ParseEmit()
    {
        var s = Here; Advance();
        var (path, ev) = ParseEventRef("@EventName");
        var (fields, fill) = ParseEmitBody();
        return new EmitStmt(ev, fields, fill, s) { EventPath = path };
    }

    /// The tail shared by `emit`/`start`: `?` (fill-the-rest), `{ a: 1, ? }`, or nothing (`@E` alone —
    /// omitted fields fill from defaults/context at runtime).
    private (List<FieldInit> Fields, bool Fill) ParseEmitBody()
    {
        var fields = new List<FieldInit>();
        bool fill = false;
        if (Match(TokenKind.Question)) fill = true;
        else if (Check(TokenKind.LBrace))
        {
            Advance();
            SkipTerms();
            while (!Check(TokenKind.RBrace) && !AtEnd)
            {
                if (Match(TokenKind.Question)) fill = true;
                else
                {
                    var fs = Here;
                    string name = ExpectName("field name").Text;
                    Expect(TokenKind.Colon, "':'");
                    fields.Add(new FieldInit(name, ParseExpr(), fs));
                }
                Match(TokenKind.Comma);
                SkipTerms();
            }
            Expect(TokenKind.RBrace, "'}'");
        }
        return (fields, fill);
    }

    /// `start @Event { payload }` — the bundle's entry point. Reuses the emit body.
    private StartDecl ParseStart()
    {
        var s = Here;
        Expect(TokenKind.KwStart, "'start'");
        var (path, ev) = ParseEventRef("@EventName after 'start'");
        var (fields, fill) = ParseEmitBody();
        return new StartDecl(ev, fields, fill, s) { EventPath = path };
    }

    /// An event reference: bare `@Event` (local) OR `*Author.Bundle.@Event` (a collision-safe reference
    /// to an event another bundle OWNS — so its payload types are unambiguous). Returns (path, name);
    /// path is empty for the bare form. Used by emit/hear/start.
    private (IReadOnlyList<string> Path, string Name) ParseEventRef(string what)
    {
        if (!Check(TokenKind.Star))
            return (Array.Empty<string>(), Expect(TokenKind.EventRef, what).Text);
        Advance();   // '*'
        var path = new List<string> { ExpectName("qualified path after '*'").Text };
        while (true)
        {
            Expect(TokenKind.Dot, "'.'");
            if (Check(TokenKind.EventRef)) return (path, Advance().Text);
            path.Add(ExpectName("path segment or @Event").Text);
        }
    }

    private AttachStmt ParseAttach(bool remove)
    {
        var s = Here; Advance();
        string shape = Expect(TokenKind.ShapeRef, "$Shape").Text;
        if (remove) { Expect(TokenKind.KwFrom, "'from'"); return new AttachStmt(true, shape, ParseExpr(), null, s); }
        Expect(TokenKind.KwTo, "'to'");
        // `attach $C to e { hp: 5 }` — the braces are the COMPONENT's field init, so the target must not
        // absorb them as a struct literal named `e`. Nothing else in the grammar puts a struct body
        // directly after an expression, so the suppression is scoped to exactly this parse.
        _noStructLit = true;
        Expr target;
        try { target = ParseExpr(); } finally { _noStructLit = false; }
        IReadOnlyList<FieldInit>? init = Check(TokenKind.LBrace) ? ParseStructBody() : null;
        return new AttachStmt(false, shape, target, init, s);
    }

    private ChanceStmt ParseChance()
    {
        var s = Here; Advance();
        var pct = Expect(TokenKind.Percent, "a percentage like 30%");
        double p = pct.Value is double d ? d / 100.0 : 0;
        return new ChanceStmt(p, ParseBlock(), s);
    }

    private BringStmt ParseBring()
    {
        var s = Here; Advance();
        // Optional count: `bring 3 Item(...)` / `bring n Item(...)`. Present when the token before the
        // builder name is not immediately followed by '(' (i.e. a number, or an ident that isn't the callee).
        // A leading count is `bring 3 Item(…)` or `bring n Item(…)`. A NUMBER is unambiguous — it can
        // never be a builder name. An IDENT only is one when a builder reference still follows it;
        // testing "the next token is not `(`" instead swallowed the builder itself in every form that
        // does not end in an argument list, so `bring Unit ?` and a bare `bring JumpLine` both failed
        // with "Expected builder name, found Question" — pointing at the `?`, not at the real cause.
        static bool StartsBuilderRef(TokenKind k) =>
            k is TokenKind.Ident or TokenKind.BuilderRef or TokenKind.Star;

        Expr? count = null;
        if (Check(TokenKind.Int) || Check(TokenKind.Float) ||
            (Check(TokenKind.Ident) && StartsBuilderRef(Peek(1).Kind)))
            count = ParsePrimary();

        // The builder: bare `Name` (local, back-compat), `&Name` (local, sigil'd), or a qualified
        // `*Author.Bundle.Publicator.&Name` (cross-bundle), ending at the `&Builder` ref.
        string name;
        IReadOnlyList<string> path = Array.Empty<string>();
        if (Check(TokenKind.Star))
        {
            Advance();   // '*'
            var p = new List<string> { ExpectName("qualified path after '*'").Text };
            while (true)
            {
                Expect(TokenKind.Dot, "'.'");
                if (Check(TokenKind.BuilderRef)) { name = Advance().Text; break; }
                p.Add(ExpectName("path segment or &Builder").Text);
            }
            path = p;
        }
        else if (Check(TokenKind.BuilderRef)) name = Advance().Text;
        else name = Expect(TokenKind.Ident, "builder name").Text;

        var args = new List<Expr>();
        bool fill = false;
        if (Match(TokenKind.Question))                    // bring X ?
            fill = true;
        else if (Check(TokenKind.LParen))                 // bring X(a, ?)
        {
            Advance();
            if (!Check(TokenKind.RParen))
                while (true)
                {
                    if (Match(TokenKind.Question)) { fill = true; break; }

                    // `base` is admitted HERE and nowhere else. It is not a value — there is nothing it
                    // could evaluate to outside an argument list, because what it means is decided by
                    // the parameter it lands on. Parsing it as an ordinary expression would let it
                    // appear in `let x = def`, where no parameter exists to take a default from.
                    if (Check(TokenKind.KwBase)) { args.Add(new DefaultArgExpr(Here)); Advance(); }
                    else args.Add(ParseExpr());

                    if (!Match(TokenKind.Comma)) break;
                }
            Expect(TokenKind.RParen, "')'");
        }
        // `as name` binds the identity this builds, so a later statement can refer to it. `as` already
        // means exactly this in `target … as self` and `hear … as e`; `bring` stays a statement.
        string? bind = Match(TokenKind.KwAs) ? ExpectName("a name after 'as'").Text : null;

        return new BringStmt(count, name, args, fill, s) { BuilderPath = path, Bind = bind };
    }

    private Stmt ParseAssignOrExpr()
    {
        var s = Here;
        var lhs = ParseExpr();
        AssignOp? op = Cur.Kind switch
        {
            TokenKind.Assign => AssignOp.Assign,
            TokenKind.PlusEq => AssignOp.PlusEq,
            TokenKind.MinusEq => AssignOp.MinusEq,
            TokenKind.StarEq => AssignOp.StarEq,
            TokenKind.SlashEq => AssignOp.SlashEq,
            _ => null
        };
        if (op is null) return new ExprStmt(lhs, s);
        Advance();
        return new AssignStmt(lhs, op.Value, ParseExpr(), s);
    }

    // ---- expressions (precedence climbing) ------------------------------

    private Expr ParseExpr() => ParseOr();

    private Expr ParseOr()
    {
        var e = ParseAnd();
        while (Check(TokenKind.KwOr)) { var s = Here; Advance(); e = new BinaryExpr(BinOp.Or, e, ParseAnd(), s); }
        return e;
    }
    private Expr ParseAnd()
    {
        var e = ParseCmp();
        while (Check(TokenKind.KwAnd)) { var s = Here; Advance(); e = new BinaryExpr(BinOp.And, e, ParseCmp(), s); }
        return e;
    }
    private Expr ParseCmp()
    {
        var e = ParseAdd();
        while (true)
        {
            BinOp? op = Cur.Kind switch
            {
                TokenKind.Eq => BinOp.Eq, TokenKind.Ne => BinOp.Ne,
                TokenKind.Lt => BinOp.Lt, TokenKind.Gt => BinOp.Gt,
                TokenKind.Le => BinOp.Le, TokenKind.Ge => BinOp.Ge, _ => null
            };
            if (op is null) return e;
            var s = Here; Advance(); e = new BinaryExpr(op.Value, e, ParseAdd(), s);
        }
    }
    private Expr ParseAdd()
    {
        var e = ParseMul();
        while (Check(TokenKind.Plus) || Check(TokenKind.Minus))
        {
            var op = Advance().Kind == TokenKind.Plus ? BinOp.Add : BinOp.Sub;
            e = new BinaryExpr(op, e, ParseMul(), e.Span);
        }
        return e;
    }
    private Expr ParseMul()
    {
        var e = ParseUnary();
        while (Check(TokenKind.Star) || Check(TokenKind.Slash) || Check(TokenKind.Mod))
        {
            var k = Advance().Kind;
            var op = k == TokenKind.Star ? BinOp.Mul : k == TokenKind.Slash ? BinOp.Div : BinOp.Mod;
            e = new BinaryExpr(op, e, ParseUnary(), e.Span);
        }
        return e;
    }
    private Expr ParseUnary()
    {
        if (Check(TokenKind.KwNot)) { var s = Here; Advance(); return new UnaryExpr(UnOp.Not, ParseUnary(), s); }
        if (Check(TokenKind.Minus)) { var s = Here; Advance(); return new UnaryExpr(UnOp.Neg, ParseUnary(), s); }
        return ParsePostfix();
    }

    private Expr ParsePostfix()
    {
        var e = ParsePrimary();
        while (true)
        {
            if (Match(TokenKind.Dot)) { var n = ExpectName("member name"); e = new MemberExpr(e, n.Text, n.Span); }
            else if (Match(TokenKind.LBracket)) { var idx = ParseExpr(); Expect(TokenKind.RBracket, "']'"); e = new IndexExpr(e, idx, e.Span); }
            else if (Check(TokenKind.LParen)) { e = new CallExpr(e, ParseArgs(), e.Span); }
            else break;
        }
        return e;
    }

    private List<Expr> ParseArgs()
    {
        Expect(TokenKind.LParen, "'('");
        var args = new List<Expr>();
        if (!Check(TokenKind.RParen))
        {
            args.Add(ParseExpr());
            while (Match(TokenKind.Comma)) args.Add(ParseExpr());
        }
        Expect(TokenKind.RParen, "')'");
        return args;
    }

    private Expr ParsePrimary()
    {
        var s = Here;
        switch (Cur.Kind)
        {
            case TokenKind.Int: return new LiteralExpr(Advance().Value, LiteralKind.Int, s);
            case TokenKind.Float: return new LiteralExpr(Advance().Value, LiteralKind.Float, s);
            case TokenKind.Percent: return new LiteralExpr(Advance().Value, LiteralKind.Percent, s);
            case TokenKind.String: return new LiteralExpr(Advance().Value, LiteralKind.String, s);
            case TokenKind.KwTrue: Advance(); return new LiteralExpr(true, LiteralKind.Bool, s);
            case TokenKind.KwFalse: Advance(); return new LiteralExpr(false, LiteralKind.Bool, s);
            case TokenKind.ShapeRef: return new ShapeRefExpr(Advance().Text, s);
            case TokenKind.EventRef: return new EventRefExpr(Advance().Text, s);
            case TokenKind.MarkRef: return new MarkRefExpr(Advance().Text, s);
            case TokenKind.KwEntity: { Advance(); return new EntityExpr(s); }
            case TokenKind.KwIndex:  { Advance(); return new LoopIndexExpr(s); }
            case TokenKind.Star: return ParseStarRef();
            // Parentheses re-open struct literals: the `}` that closes one cannot be mistaken for the
            // attach init, because the `)` has to come first.
            case TokenKind.LParen:
            {
                Advance();
                bool outer = _noStructLit; _noStructLit = false;
                var e = ParseExpr();
                _noStructLit = outer;
                Expect(TokenKind.RParen, "')'");
                return e;
            }
            case TokenKind.LBracket: return ParseListLit();
            case TokenKind.Ident:
            {
                string name = Advance().Text;
                // struct literal: `Ident {` but only when it clearly starts a struct (field: value)
                if (!_noStructLit && Check(TokenKind.LBrace) && LooksLikeStructBody())
                    return new StructLitExpr(name, ParseStructBody(), s);
                return new NameExpr(name, s);
            }
            default:
                _diag.Error("VS0104", $"Unexpected '{Cur.Text}' in expression.", s);
                throw new ParseError();
        }
    }

    /// `*Author.Bundle.Publicator.@Event` — a collision-safe cross-bundle reference. The leading dotted
    /// idents are a suffix of `Author.Bundle.Publicator`; the final segment is the member (a sigil'd
    /// `@Event`/`$Shape`/`#Mark`, or a plain name). Only valid in operand position, so `*` here never
    /// clashes with the multiply operator (which is infix, handled in ParseBinary).
    private StarRefExpr ParseStarRef()
    {
        var s = Here;
        Expect(TokenKind.Star, "'*'");
        var path = new List<string> { ExpectName("qualified name after '*'").Text };
        Expect(TokenKind.Dot, "'.'");
        while (true)
        {
            if (Check(TokenKind.EventRef)) return new StarRefExpr(path, Advance().Text, MemberSigil.Event, s);
            if (Check(TokenKind.ShapeRef)) return new StarRefExpr(path, Advance().Text, MemberSigil.Shape, s);
            if (Check(TokenKind.MarkRef)) return new StarRefExpr(path, Advance().Text, MemberSigil.Mark, s);
            var name = ExpectName("member or path segment").Text;
            if (Match(TokenKind.Dot)) { path.Add(name); continue; }   // another segment follows
            return new StarRefExpr(path, name, MemberSigil.None, s);
        }
    }

    /// `*Author.Bundle.Publicator.$Shape[.field]` in an event/builder body — the qualified form of a
    /// `$Shape` include. Mirrors ParseStarRef's suffix path, but the member must be a `$Shape`: a field
    /// group is the only thing an include can pull in.
    private ShapeInclude ParseQualifiedShapeInclude()
    {
        var s = Here;
        Expect(TokenKind.Star, "'*'");
        var path = new List<string> { ExpectName("qualified name after '*'").Text };
        Expect(TokenKind.Dot, "'.'");
        while (!Check(TokenKind.ShapeRef))
        {
            if (AtEnd || Check(TokenKind.RBrace))
            {
                _diag.Error("VS0111", "Expected a `$Shape` at the end of a qualified include.", Here);
                throw new ParseError();
            }
            path.Add(ExpectName("path segment").Text);
            Expect(TokenKind.Dot, "'.'");
        }
        string shape = Advance().Text;
        string? field = Match(TokenKind.Dot) ? ExpectName("field name").Text : null;
        Expr? def = Match(TokenKind.Assign) ? ParseExpr() : null;
        return new ShapeInclude(shape, field, def, s) { Path = path };
    }

    /// A `{` begins a struct body if it is `{ ident : …`. Avoids swallowing control-flow blocks.
    /// Set only while parsing the target of `attach $C to <target> { … }`; see ParseAttach.
    private bool _noStructLit;

    private bool LooksLikeStructBody()
        => Check(TokenKind.LBrace) && Peek(1).Kind == TokenKind.Ident && Peek(2).Kind == TokenKind.Colon;

    private ListLitExpr ParseListLit()
    {
        var s = Here; Expect(TokenKind.LBracket, "'['");
        var items = new List<Expr>();
        SkipTerms();
        if (!Check(TokenKind.RBracket))
        {
            items.Add(ParseExpr());
            while (Match(TokenKind.Comma)) { SkipTerms(); items.Add(ParseExpr()); }
        }
        SkipTerms();
        Expect(TokenKind.RBracket, "']'");
        return new ListLitExpr(items, s);
    }

    private List<FieldInit> ParseStructBody()
    {
        Expect(TokenKind.LBrace, "'{'");
        var fields = new List<FieldInit>();
        SkipTerms();
        while (!Check(TokenKind.RBrace) && !AtEnd)
        {
            var s = Here;
            string name = ExpectName("field name").Text;
            Expect(TokenKind.Colon, "':'");
            fields.Add(new FieldInit(name, ParseExpr(), s));
            if (!Match(TokenKind.Comma)) { }
            SkipTerms();
        }
        Expect(TokenKind.RBrace, "'}'");
        return fields;
    }

    // ---- error recovery -------------------------------------------------

    private void Synchronize()
    {
        while (!AtEnd)
        {
            if (Cur.Kind is TokenKind.KwBundle or TokenKind.KwShape or TokenKind.KwShard
                or TokenKind.KwShardView or TokenKind.KwBridge or TokenKind.KwEvent or TokenKind.KwType
                or TokenKind.KwSf or TokenKind.KwUse or TokenKind.KwPublicator) return;
            if (Cur.Kind == TokenKind.RBrace) { Advance(); return; }
            Advance();
        }
    }

    private void SynchronizeInBlock()
    {
        while (!AtEnd && Cur.Kind != TokenKind.Term && Cur.Kind != TokenKind.RBrace) Advance();
    }
}
