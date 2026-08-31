using Vein.Compiler.Parsing;

namespace Vein.Compiler.Tooling;

// A read-only "IOP manifest" of a bundle for the Workbench's Bundle Explorer: every primitive grouped by
// kind and visibility, plus the emit/hear relationships between events and shards. Pure analysis over the
// AST — reuses BundleInfo (counts) and SymbolIndex (marks).
public enum PrimitiveKind { Event, Builder, Shard, Shape, Mark, Bridge, Publicator, ShardView }

public static class PrimitiveKinds
{
    /// Behaviour, not API. A shard/view/bridge can never be `shared` — VS0108 rejects one inside a
    /// publicator — so it is not part of what another bundle can reference; it simply runs when the
    /// bundle is loaded. Listing them beside the API confuses "what can I use" with "what happens".
    public static bool IsBehaviour(PrimitiveKind k) =>
        k is PrimitiveKind.Shard or PrimitiveKind.ShardView or PrimitiveKind.Bridge;

    /// The consumable surface: what another bundle can actually reference.
    public static bool IsApi(PrimitiveKind k) => !IsBehaviour(k);
}

// VeinScript visibility: `shared("…")` decls are the cross-bundle API (Shared); everything else declared
// is Public; Private is reserved (no language concept yet).
public enum Visibility { Public, Shared, Private }

public sealed record PrimitiveInfo
{
    public PrimitiveKind Kind { get; init; }
    public string Name { get; init; } = "";
    public Visibility Visibility { get; init; }
    public IReadOnlyList<(string Name, string Type)> Payload { get; init; } = Array.Empty<(string, string)>();
    public IReadOnlyList<string> EmittedBy { get; init; } = Array.Empty<string>();   // events: shards that emit it
    public IReadOnlyList<string> HeardBy { get; init; } = Array.Empty<string>();     // events: shards that hear it
    public IReadOnlyList<string> Hears { get; init; } = Array.Empty<string>();       // shards: events heard
    public IReadOnlyList<string> Emits { get; init; } = Array.Empty<string>();       // shards: events emitted
    public IReadOnlyList<string> Brings { get; init; } = Array.Empty<string>();      // shards: builders invoked
    public string? Generates { get; init; }                                          // builders: event produced
}

public sealed class BundleModel
{
    public required string Name { get; init; }
    public string? Author { get; init; }
    public required IReadOnlyList<PrimitiveInfo> Primitives { get; init; }

    public IEnumerable<PrimitiveInfo> ByKind(PrimitiveKind k) => Primitives.Where(p => p.Kind == k);
    public IEnumerable<PrimitiveInfo> ByKindAndVisibility(PrimitiveKind k, Visibility v) =>
        Primitives.Where(p => p.Kind == k && p.Visibility == v);
    public int Count(PrimitiveKind k) => Primitives.Count(p => p.Kind == k);
    public PrimitiveInfo? Find(PrimitiveKind k, string name) =>
        Primitives.FirstOrDefault(p => p.Kind == k && p.Name == name);

    public static BundleModel? Analyze(CompilationUnit unit) =>
        unit.Bundles.Count > 0 ? Analyze(unit.Bundles[0]) : null;

    public static BundleModel Analyze(BundleDecl bundle)
    {
        var events = new List<(EventDecl Decl, bool Shared)>();
        var builders = new List<(BuilderDecl Decl, bool Shared)>();
        var shapes = new List<Decl>();
        var shards = new List<ShardDecl>();
        var views = new List<ViewDecl>();
        var bridges = new List<BridgeDecl>();
        var publicators = new List<PublicatorDecl>();

        void Collect(IEnumerable<Decl> members)
        {
            foreach (var m in members)
                switch (m)
                {
                    case PublicatorDecl p: publicators.Add(p); Collect(p.Members); break;
                    case EventDecl e: events.Add((e, e.Shared)); break;
                    case BuilderDecl b: builders.Add((b, b.Shared)); break;
                    case ShapeDecl s: shapes.Add(s); break;
                    case ShardDecl sh: shards.Add(sh); break;
                    case ViewDecl v: views.Add(v); break;
                    case BridgeDecl br: bridges.Add(br); break;
                }
        }
        Collect(bundle.Members);

        // Behaviour: per shard/view/bridge, which events it hears/emits and which builders it brings.
        var behaviour = new Dictionary<string, (List<string> Hears, SortedSet<string> Emits, SortedSet<string> Brings)>(StringComparer.Ordinal);
        void Behave(string owner, IReadOnlyList<Node> members) => behaviour[owner] = Analyse(members);
        foreach (var sh in shards) Behave(sh.Name, sh.Members);
        foreach (var v in views) Behave(v.Name, v.Members);
        foreach (var br in bridges) Behave(br.Name, br.Members);

        // Invert to per-event emitted-by / heard-by.
        List<string> EmittedBy(string ev) => behaviour.Where(b => b.Value.Emits.Contains(ev)).Select(b => b.Key).OrderBy(x => x).ToList();
        List<string> HeardBy(string ev) => behaviour.Where(b => b.Value.Hears.Contains(ev)).Select(b => b.Key).OrderBy(x => x).ToList();

        var prims = new List<PrimitiveInfo>();

        foreach (var (e, shared) in events)
            prims.Add(new PrimitiveInfo
            {
                Kind = PrimitiveKind.Event, Name = e.Name, Visibility = shared ? Visibility.Shared : Visibility.Public,
                Payload = Fields(e.Members), EmittedBy = EmittedBy(e.Name), HeardBy = HeardBy(e.Name)
            });

        // A builder's params are its members with `$Shape` INCLUDES FLATTENED — the same expansion `bring`
        // binds arguments against, and the same one `?` has to offer. Listing only the plain `FieldDecl`s
        // reported no params at all for every builder written the shape-backed way, which is most of
        // Vein.Web and the whole point of an identity template. It matters most for a `shared` builder,
        // where the consumer is in another bundle and the declaration is not on screen.
        var shapeFields = shapes.OfType<ShapeDecl>()
            .ToDictionary(s => s.Name, s => s.Members.OfType<FieldDecl>().ToList(), StringComparer.Ordinal);

        foreach (var (b, shared) in builders)
        {
            var output = b.Members.OfType<FieldDecl>().FirstOrDefault(f => f.Name is "markup" or "code" or "css" or "line");
            var marks = b.Members.OfType<MarkMember>().SelectMany(m => m.Marks).ToList();

            // A `mark` member makes it an identity template: it builds rather than emits.
            string generates = marks.Count > 0
                ? "an identity " + string.Join(" ", marks.Select(m => "#" + m))
                : output?.Name switch { "code" => "@Script", "css" => "@Style", "line" => "@Print", "markup" => "@Html", _ => "@" + b.Name };

            var payload = Sig.Expand(b.Members.Where(m => !ReferenceEquals(m, output)).ToList(), shapeFields)
                             .Select(f => (f.Name, f.OriginShape is null ? f.Type : f.Type + " ($" + f.OriginShape + ")"))
                             .ToList();

            prims.Add(new PrimitiveInfo
            {
                Kind = PrimitiveKind.Builder, Name = b.Name, Visibility = shared ? Visibility.Shared : Visibility.Public,
                Payload = payload, Generates = generates
            });
        }

        foreach (var s in shapes)
            prims.Add(new PrimitiveInfo { Kind = PrimitiveKind.Shape, Name = NameOf(s), Visibility = VisibilityOf(s) });

        void AddBehaviour(PrimitiveKind kind, string owner)
        {
            var (hears, emits, brings) = behaviour[owner];
            prims.Add(new PrimitiveInfo
            {
                Kind = kind, Name = owner, Visibility = Visibility.Public,
                Hears = hears, Emits = emits.ToList(), Brings = brings.ToList()
            });
        }
        foreach (var sh in shards) AddBehaviour(PrimitiveKind.Shard, sh.Name);
        foreach (var v in views) AddBehaviour(PrimitiveKind.ShardView, v.Name);
        foreach (var br in bridges) AddBehaviour(PrimitiveKind.Bridge, br.Name);

        foreach (var p in publicators)
            prims.Add(new PrimitiveInfo { Kind = PrimitiveKind.Publicator, Name = p.Name, Visibility = Visibility.Public });

        // Marks have no declaration — the distinct marks used in the bundle (via SymbolIndex).
        foreach (var mark in SymbolIndex.Collect(new CompilationUnit(new[] { bundle }, bundle.Span)).Marks)
            prims.Add(new PrimitiveInfo { Kind = PrimitiveKind.Mark, Name = mark, Visibility = Visibility.Public });

        return new BundleModel { Name = bundle.Name, Author = bundle.Author, Primitives = prims };
    }

    private static (List<string> Hears, SortedSet<string> Emits, SortedSet<string> Brings) Analyse(IReadOnlyList<Node> members)
    {
        var hears = new List<string>();
        var emits = new SortedSet<string>(StringComparer.Ordinal);
        var brings = new SortedSet<string>(StringComparer.Ordinal);

        void Block(Block b) { foreach (var s in b.Statements) Stmt(s); }
        void Stmt(Stmt s)
        {
            switch (s)
            {
                case Block b: Block(b); break;
                case IfStmt i: Block(i.Then); if (i.Else is Block eb) Block(eb); else if (i.Else is IfStmt ei) Stmt(ei); break;
                case WhileStmt w: Block(w.Body); break;
                case TargetStmt t: Block(t.Body); break;
                case QueryStmt q: Block(q.Body); break;
                case RepeatStmt r: Block(r.Body); break;
                case MatchStmt m: foreach (var a in m.Arms) Block(a.Body); if (m.Else is not null) Block(m.Else); break;
                case ChanceStmt c: Block(c.Body); break;
                case EmitStmt em: emits.Add(em.Event); break;
                case BringStmt br: brings.Add(br.Builder); break;
            }
        }
        foreach (var n in members)
            switch (n)
            {
                case HearBlock hb: hears.Add(hb.Event); Block(hb.Body); break;
                case ScheduleBlock sc: Block(sc.Body); break;
                case FuncDecl f: Block(f.Body); break;
                case Stmt st: Stmt(st); break;
            }
        return (hears, emits, brings);
    }

    private static IReadOnlyList<(string, string)> Fields(IEnumerable<Node> members) =>
        members.OfType<FieldDecl>().Select(f => (f.Name, f.Type?.Name ?? "any")).ToList();

    private static string NameOf(Decl d) => d switch
    {
        ShapeDecl s => s.Name, EventDecl e => e.Name, BuilderDecl b => b.Name,
        ShardDecl sh => sh.Name, ViewDecl v => v.Name, BridgeDecl br => br.Name, _ => "?"
    };

    private static Visibility VisibilityOf(Decl d) => d.Shared ? Visibility.Shared : Visibility.Public;
}
