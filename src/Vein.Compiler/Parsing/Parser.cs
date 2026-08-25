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
                string? doc = TryParseDoc();
                var d = ParseDecl(exported);
                if (d is not null)
                    members.Add(d with { Doc = doc ?? d.Doc, Exported = exported || d.Exported });
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
            case TokenKind.KwType: return ParseType();
            case TokenKind.KwEvent: return ParseEvent();
            case TokenKind.KwShard: return ParseShard();
            case TokenKind.KwShardView: return ParseView();
            case TokenKind.KwBridge: return ParseBridge();
            case TokenKind.KwSf: return ParseFunc();
            case TokenKind.KwBuilder: return ParseBuilder();
            case TokenKind.KwFn:
                _diag.Error("VS0106", "VeinScript has no `fn`; every function is an SF.", Here);
                throw new ParseError();
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
        if (Check(TokenKind.ShapeRef))                                   // $Shape or $Shape.field
        {
            string shape = Advance().Text;
            string? field = Match(TokenKind.Dot) ? ExpectName("field name").Text : null;
            Expr? sd = Match(TokenKind.Assign) ? ParseExpr() : null;
            return new ShapeInclude(shape, field, sd, s);
        }
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
    private FuncDecl ParseFunc()
    {
        var s = Here; Advance();
        string name = Expect(TokenKind.Ident, "SF name").Text;
        Expect(TokenKind.LParen, "'('");
        var ps = new List<Param>();
        if (!Check(TokenKind.RParen))
        {
            ps.Add(ParseParam());
            while (Match(TokenKind.Comma)) ps.Add(ParseParam());
        }
        Expect(TokenKind.RParen, "')'");
        if (Check(TokenKind.Arrow))
            _diag.Error("VS0105", "SF has no return type; it emits events instead of returning.", Here);
        var body = ParseBlock();
        return new FuncDecl(true, name, ps, null, body, s);
    }

    private Param ParseParam()
    {
        var s = Here;
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
            else if (Check(TokenKind.KwSf)) members.Add(ParseFunc());
            else { _diag.Error("VS0108", $"Unexpected '{Cur.Text}' in ShardView body (expected hear/var/let/SF).", Here); throw new ParseError(); }
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
            case TokenKind.KwTarget: return ParseTargetBlock();
            case TokenKind.KwEach:
            case TokenKind.KwSettled:
            case TokenKind.KwStart: return ParseLifecycle();
            case TokenKind.KwHear: return ParseHear();
            case TokenKind.KwSf: return ParseFunc();
            case TokenKind.KwFn:
                _diag.Error("VS0106", "VeinScript has no `fn`; every function is an SF.", Here);
                throw new ParseError();
            case TokenKind.KwLet: return ParseVar(mutable: false);
            case TokenKind.KwVar: return ParseVar(mutable: true);
            default:
                _diag.Error("VS0103", $"Unexpected '{Cur.Text}' in shard body.", Here);
                throw new ParseError();
        }
    }

    private TargetBlock ParseTargetBlock()
    {
        var s = Here; Advance();
        var comps = new List<string>();
        var tags = new List<string>();
        while (Check(TokenKind.ShapeRef) || Check(TokenKind.MarkRef))
        {
            if (Check(TokenKind.ShapeRef)) comps.Add(Advance().Text);
            else tags.Add(Advance().Text);
        }
        Expect(TokenKind.KwAs, "'as'");
        string bind = Expect(TokenKind.Ident, "binding name").Text;
        Expect(TokenKind.LBrace, "'{'");
        var body = new List<Node>();
        SkipTerms();
        while (!Check(TokenKind.RBrace) && !AtEnd)
        {
            if (Check(TokenKind.KwEach) || Check(TokenKind.KwSettled) || Check(TokenKind.KwStart))
                body.Add(ParseLifecycle());
            else if (Check(TokenKind.KwHear)) body.Add(ParseHear());
            else body.Add(ParseStmt());
            SkipTerms();
        }
        Expect(TokenKind.RBrace, "'}'");
        return new TargetBlock(comps, tags, bind, body, s);
    }

    private LifecycleBlock ParseLifecycle()
    {
        var s = Here;
        LifecyclePhase phase;
        if (Match(TokenKind.KwEach)) { Expect(TokenKind.KwTick, "'tick'"); phase = LifecyclePhase.Tick; }
        else if (Match(TokenKind.KwSettled)) phase = LifecyclePhase.Settled;
        else { Expect(TokenKind.KwStart, "'start'"); phase = LifecyclePhase.Start; }
        return new LifecycleBlock(phase, ParseBlock(), s);
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
            case TokenKind.KwTarget: return ParseTargetStmt();
            case TokenKind.KwRepeat: return ParseRepeat();
            case TokenKind.KwMatch: return ParseMatch();
            case TokenKind.KwReturn:
                _diag.Error("VS0107", "VeinScript has no `return`; an SF emits events instead of returning.", Here);
                throw new ParseError();
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

    private TargetStmt ParseTargetStmt()
    {
        var s = Here; Advance();
        var src = ParseExpr();
        Expect(TokenKind.KwAs, "'as'");
        string bind = Expect(TokenKind.Ident, "binding").Text;
        return new TargetStmt(src, bind, ParseBlock(), s);
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
                string caseName = Expect(TokenKind.Ident, "case name").Text;
                arms.Add(new MatchArm(caseName, ParseBlock(), a));
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
        var target = ParseExpr();
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
        Expr? count = null;
        if (Check(TokenKind.Int) || Check(TokenKind.Float) ||
            (Check(TokenKind.Ident) && Peek(1).Kind != TokenKind.LParen))
            count = ParsePrimary();
        string name = Expect(TokenKind.Ident, "builder name").Text;

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
                    args.Add(ParseExpr());
                    if (!Match(TokenKind.Comma)) break;
                }
            Expect(TokenKind.RParen, "')'");
        }
        return new BringStmt(count, name, args, fill, s);
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
            case TokenKind.Scope: { Advance(); return new SelfScopeExpr(Expect(TokenKind.Ident, "component name after '::'").Text, s); }
            case TokenKind.KwEntity: { Advance(); return new EntityExpr(s); }
            case TokenKind.Star: return ParseStarRef();
            case TokenKind.LParen: { Advance(); var e = ParseExpr(); Expect(TokenKind.RParen, "')'"); return e; }
            case TokenKind.LBracket: return ParseListLit();
            case TokenKind.Ident:
            {
                string name = Advance().Text;
                if (Match(TokenKind.Scope)) return new ScopeExpr(name, Expect(TokenKind.Ident, "name after '::'").Text, s);
                // struct literal: `Ident {` but only when it clearly starts a struct (field: value)
                if (Check(TokenKind.LBrace) && LooksLikeStructBody()) return new StructLitExpr(name, ParseStructBody(), s);
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

    /// A `{` begins a struct body if it is `{ ident : …`. Avoids swallowing control-flow blocks.
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
