using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;

namespace Vein.Compiler.Tooling;

// WHY: a console address is an identity, but nothing ever checks it. The runtime has no registry and no
// lookup — ConsoleBus concatenates the address into an OS pipe name ("vein.console." + to) and lets the
// operating system do the matching. So `to: #Sever` opens a pipe nobody listens on, Connect throws, the
// catch returns false, and Interp discards that false. A typo'd address vanishes with no diagnostic at all.
//
// This is the missing check: pair every address site (`@Send { to }`) with a spawn site (`@Console { name }`
// or `bring &Console(name, …)`) and report the ones that resolve to nothing.
//
// Two deliberate details:
//   * Events match on their UNQUALIFIED name, exactly as Interp.Drain routes them — the *Vein.Console.Io.
//     qualifier is dropped at lower time, so this follows the runtime rather than inventing a second rule.
//   * Both `#Mark` and a string literal are accepted at every site. Marks and strings evaluate to the same
//     value, so mixed or not-yet-migrated code must not produce false warnings.
public sealed class ConsoleGraph
{
    /// One site that makes an address REACHABLE: `@Console { name }` or `bring &Console(name, …)` spawns a
    /// local console; `@Link { name }` routes to a network peer; `@Listen { as }` names this program.
    public sealed record Spawn(string Address, string Owner, SourceSpan Span);

    /// One `@Send { to }` — a console this bundle talks to.
    public sealed record Address(string Target, string Owner, SourceSpan Span);

    /// The reserved root console — written `#Main`, always addressable, never spawned by an `@Console`.
    public static string RootAddress => Ir.Interp.RootConsole;

    public required IReadOnlyList<Spawn> Spawns { get; init; }
    public required IReadOnlyList<Address> Addresses { get; init; }

    /// Every address that is spawned here, plus the reserved root. Ordinal, ordered for stable messages.
    public IReadOnlyList<string> Known =>
        Spawns.Select(s => s.Address).Append(Ir.Interp.RootConsole)
              .Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList();

    /// Addresses that no spawn site in this compilation accounts for.
    public IEnumerable<Address> Unresolved
    {
        get
        {
            var known = new HashSet<string>(Known, StringComparer.Ordinal);
            return Addresses.Where(a => !known.Contains(a.Target));
        }
    }

    public static ConsoleGraph? Analyze(CompilationUnit unit) =>
        unit.Bundles.Count > 0 ? Analyze(unit.Bundles[0]) : null;

    public static ConsoleGraph Analyze(BundleDecl bundle)
    {
        var spawns = new List<Spawn>();
        var addresses = new List<Address>();

        // A console address literal: `#Server` or the older `"Server"`. Anything else (a variable, an
        // expression) is not statically knowable, so it is skipped rather than guessed at.
        static string? Literal(Expr? e) => e switch
        {
            MarkRefExpr m => m.Name,
            LiteralExpr { Kind: LiteralKind.String } l => l.Value as string,
            _ => null
        };

        void Walk(string owner, IReadOnlyList<Node> members)
        {
            void Blk(Block b) { foreach (var s in b.Statements) Stm(s); }
            void Stm(Stmt s)
            {
                switch (s)
                {
                    case Block b: Blk(b); break;
                    case IfStmt i: Blk(i.Then); if (i.Else is Block eb) Blk(eb); else if (i.Else is IfStmt ei) Stm(ei); break;
                    case WhileStmt w: Blk(w.Body); break;
                    case TargetStmt t: Blk(t.Body); break;
                    case QueryStmt q: Blk(q.Body); break;
                    case RepeatStmt r: Blk(r.Body); break;
                    case MatchStmt m: foreach (var a in m.Arms) Blk(a.Body); if (m.Else is not null) Blk(m.Else); break;
                    case ChanceStmt c: Blk(c.Body); break;

                    case EmitStmt em when em.Event == "Console":
                        if (Field(em, "name") is { } spawned) spawns.Add(new Spawn(spawned, owner, em.Span));
                        break;

                    case EmitStmt em when em.Event == "Send":
                        if (Field(em, "to") is { } target) addresses.Add(new Address(target, owner, em.Span));
                        break;

                    // A NETWORK peer is reachable without ever being spawned: `@Link { name: #Server }`
                    // registers a route to a program on another machine, and `@Listen { as: #Me }` names
                    // this one. Both make an address real, so both resolve a `@Send` — without this,
                    // VS0212 fires on every Vein.Net program, telling it to spawn a console for a peer
                    // that is not a console and cannot be spawned.
                    case EmitStmt em when em.Event == "Link":
                        if (Field(em, "name") is { } linked) spawns.Add(new Spawn(linked, owner, em.Span));
                        break;

                    case EmitStmt em when em.Event == "Listen":
                        if (Field(em, "as") is { } self) spawns.Add(new Spawn(self, owner, em.Span));
                        break;

                    // `bring &Console(name, firsttext)` desugars to @Console, so it spawns too.
                    case BringStmt br when br.Builder == "Console" && br.Args.Count > 0:
                        if (Literal(br.Args[0]) is { } brought) spawns.Add(new Spawn(brought, owner, br.Span));
                        break;
                }
            }

            static string? Field(EmitStmt em, string name) =>
                Literal(em.Fields.FirstOrDefault(f => f.Name == name)?.Value);

            foreach (var n in members)
                switch (n)
                {
                    case HearBlock hb: Blk(hb.Body); break;
                    case ScheduleBlock sc: Blk(sc.Body); break;
                    case FuncDecl f: Blk(f.Body); break;
                    case Stmt st: Stm(st); break;
                }
        }

        void Collect(IEnumerable<Decl> members)
        {
            foreach (var m in members)
                switch (m)
                {
                    case PublicatorDecl p: Collect(p.Members); break;
                    case ShardDecl sh: Walk(sh.Name, sh.Members); break;
                    case ViewDecl v: Walk(v.Name, v.Members); break;
                    case BridgeDecl br: Walk(br.Name, br.Members); break;
                    case FuncDecl f: Walk(bundle.Name, new Node[] { f }); break;
                }
        }
        Collect(bundle.Members);

        return new ConsoleGraph { Spawns = spawns, Addresses = addresses };
    }
}
