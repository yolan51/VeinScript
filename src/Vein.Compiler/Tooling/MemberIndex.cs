using Vein.Compiler.Parsing;

namespace Vein.Compiler.Tooling;

// Heuristic member completion for `.` access. VeinScript has no full type resolver, so this resolves
// the common, unambiguous cases from the AST:
//   <hear-binding>.   -> the event's payload fields + auto metadata (from/cause/trail/…)
//   <target-binding>. -> shape names (self.Health)
//   <event>.from.     -> the origin object's fields (name/kind/identity/shapes/marks)
// It steps through chains (self.Health., d.from.) as far as it can, then lists that context's members.
public static class MemberIndex
{
    private static readonly string[] EventAuto = { "id", "from", "origin", "source", "bundle", "cause", "trail" };
    private static readonly string[] FromMembers = { "name", "kind", "identity", "shapes", "marks" };

    public sealed class Model
    {
        public Dictionary<string, List<(string Name, string Type)>> Shapes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<(string Name, string Type)>> Events { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Bindings { get; } = new(StringComparer.Ordinal); // name -> "event:E" | "entity"

        public IReadOnlyList<string> Resolve(IReadOnlyList<string> tokens)
        {
            if (tokens.Count == 0) return Array.Empty<string>();

            (string k, string v) ctx;
            string first = tokens[0];
            if (Bindings.TryGetValue(first, out var b))
                ctx = b.StartsWith("event:", StringComparison.Ordinal) ? ("event", b[6..]) : ("entity", "");
            else if (Shapes.ContainsKey(first)) ctx = ("shape", first);
            else if (Events.ContainsKey(first)) ctx = ("event", first);
            else ctx = ("", "");

            for (int i = 1; i < tokens.Count; i++) ctx = Step(ctx, tokens[i]);
            return Members(ctx);
        }

        private (string, string) Step((string k, string v) ctx, string tok)
        {
            switch (ctx.k)
            {
                case "shape":
                    var f = Shapes[ctx.v].FirstOrDefault(x => x.Name == tok);
                    if (f.Name is null) return ("", "");
                    if (Shapes.ContainsKey(f.Type)) return ("shape", f.Type);
                    if (Events.ContainsKey(f.Type)) return ("event", f.Type);
                    return f.Type == "Entity" ? ("entity", "") : ("", "");
                case "event":
                    if (tok == "from" || tok == "origin" || tok == "source") return ("from", "");
                    var ef = Events[ctx.v].FirstOrDefault(x => x.Name == tok);
                    if (ef.Name is not null && Shapes.ContainsKey(ef.Type)) return ("shape", ef.Type);
                    if (ef.Name is not null && ef.Type == "Entity") return ("entity", "");
                    return ("", "");
                case "entity":
                    return Shapes.ContainsKey(tok) ? ("shape", tok) : ("", "");
                default:
                    return ("", "");
            }
        }

        private IReadOnlyList<string> Members((string k, string v) ctx) => ctx.k switch
        {
            "shape" => Shapes[ctx.v].Select(x => x.Name).ToList(),
            "event" => Events[ctx.v].Select(x => x.Name).Concat(EventAuto).Distinct(StringComparer.Ordinal).ToList(),
            "entity" => Shapes.Keys.ToList(),
            "from" => FromMembers.ToList(),
            _ => Array.Empty<string>()
        };
    }

    public static Model Build(CompilationUnit unit)
    {
        var m = new Model();
        var shapeMap = Sig.Shapes(unit);   // for expanding `$Shape` includes in event bodies

        void Decl(Decl d)
        {
            switch (d)
            {
                case BundleDecl b: foreach (var x in b.Members) Decl(x); break;
                case PublicatorDecl p: foreach (var x in p.Members) Decl(x); break;
                case ShapeDecl s:
                    m.Shapes[s.Name] = s.Members.OfType<FieldDecl>().Select(f => (f.Name, Sig.TypeStr(f.Type))).ToList();
                    break;
                case EventDecl e:
                    m.Events[e.Name] = Sig.Expand(e.Members, shapeMap).Select(f => (f.Name, f.Type)).ToList();
                    break;
                case ShardDecl sh: foreach (var x in sh.Members) Member(x); break;
                case ViewDecl vw: foreach (var x in vw.Members) Member(x); break;
                case BridgeDecl br: foreach (var x in br.Members) Member(x); break;
            }
        }

        void Member(Node n)
        {
            switch (n)
            {
                case HearBlock hb: m.Bindings[hb.Bind] = "event:" + hb.Event; Block(hb.Body); break;
                case ScheduleBlock sc: Block(sc.Body); break;
                case FuncDecl f: Block(f.Body); break;
                case Stmt s: Stmt(s); break;
            }
        }

        void Block(Block b) { foreach (var s in b.Statements) Stmt(s); }

        void Stmt(Stmt s)
        {
            switch (s)
            {
                case TargetStmt t: m.Bindings[t.Bind] = "entity"; Block(t.Body); break;
                case QueryStmt q: m.Bindings[q.Bind] = "entity"; Block(q.Body); break;
                case IfStmt i: Block(i.Then); if (i.Else is Block eb) Block(eb); else if (i.Else is IfStmt ei) Stmt(ei); break;
                case WhileStmt w: Block(w.Body); break;
                case RepeatStmt r: Block(r.Body); break;
                case MatchStmt mt: foreach (var a in mt.Arms) Block(a.Body); if (mt.Else is not null) Block(mt.Else); break;
                case ChanceStmt c: Block(c.Body); break;
                case Block bl: Block(bl); break;
            }
        }

        foreach (var b in unit.Bundles) Decl(b);
        return m;
    }
}
