using System.Text;
using Vein.Compiler.Parsing;

namespace Vein.Compiler.Tooling;

// Read-only event tooling: discover the events a file declares (with required-vs-defaulted payload
// fields) and scaffold a ready-to-fill `emit` body. Grammar is unchanged — this only *reads* the AST.
// Feeds `veinc events` / `veinc scaffold`, and (via --json) a future editor's `@` completion.

public sealed record EventField(string Name, string Type, bool Required, string? Default);
public sealed record EventEntry(string Name, bool Shared, IReadOnlyList<EventField> Fields);

public static class EventCatalog
{
    // origin/source are added by the runtime at emit; never scaffolded.
    private static readonly HashSet<string> Auto = new(StringComparer.Ordinal) { "origin", "source" };

    public static List<EventEntry> Catalog(CompilationUnit unit)
    {
        var shapes = Sig.Shapes(unit);
        var list = new List<EventEntry>();
        void Walk(IEnumerable<Decl> decls)
        {
            foreach (var d in decls)
                switch (d)
                {
                    case BundleDecl b: Walk(b.Members); break;
                    case PublicatorDecl p: Walk(p.Members); break;
                    case EventDecl e:
                        var fields = Sig.Expand(e.Members, shapes)
                            .Where(f => !Auto.Contains(f.Name))
                            .Select(f => new EventField(f.Name, f.Type, f.Required, f.Default))
                            .ToList();
                        list.Add(new EventEntry(e.Name, e.Exported, fields));
                        break;
                }
        }
        Walk(unit.Bundles);
        return list;
    }

    /// Human listing: every event, `[shared]` when exported, each field required or defaulted.
    public static string Render(IReadOnlyList<EventEntry> events)
    {
        var sb = new StringBuilder();
        foreach (var e in events)
        {
            sb.Append('@').Append(e.Name);
            if (e.Shared) sb.Append("  [shared]");
            sb.Append('\n');
            foreach (var f in e.Fields)
                sb.Append("    ").Append(f.Name).Append(": ").Append(f.Type)
                  .Append("  ").Append(f.Required ? "required" : $"default = {f.Default}").Append('\n');
        }
        return sb.ToString();
    }

    /// A paste-ready emit body: required fields first, each a `?` placeholder with a label.
    public static string Scaffold(EventEntry e)
    {
        var sb = new StringBuilder();
        sb.Append("emit @").Append(e.Name).Append(" {\n");
        foreach (var f in e.Fields.Where(f => f.Required))
            sb.Append("    ").Append(f.Name).Append(": ?      // required — ").Append(f.Type).Append('\n');
        foreach (var f in e.Fields.Where(f => !f.Required))
            sb.Append("    ").Append(f.Name).Append(": ?      // optional — default ").Append(f.Default).Append('\n');
        sb.Append("}\n");
        return sb.ToString();
    }

    private static string TypeStr(TypeRef t) =>
        t.Name + (t.Args.Count > 0 ? "<" + string.Join(", ", t.Args.Select(TypeStr)) + ">" : "");

    private static string DefaultText(Expr e) => e switch
    {
        LiteralExpr { Kind: LiteralKind.String } l => "\"" + (l.Value as string ?? "") + "\"",
        LiteralExpr l => l.Value?.ToString() ?? "null",
        NameExpr n => n.Name,
        _ => "…"   // non-literal default; see `veinc ir` for the full expression
    };
}
