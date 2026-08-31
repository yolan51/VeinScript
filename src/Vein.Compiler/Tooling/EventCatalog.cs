using System.Text;
using Vein.Compiler.Parsing;
using Vein.Compiler.Project;

namespace Vein.Compiler.Tooling;

// Read-only event tooling: discover the events a file declares (with required-vs-defaulted payload
// fields) and scaffold a ready-to-fill `emit` body. Grammar is unchanged — this only *reads* the AST.
// Feeds `veinc events` / `veinc scaffold`, and (via --json) a future editor's `@` completion.

public sealed record EventField(string Name, string Type, bool Required, string? Default, string? OriginShape = null);
public sealed record EventEntry(string Name, bool Shared, IReadOnlyList<EventField> Fields);

/// A builder as the tooling sees it: its parameters with `$Shape` includes flattened (the list `bring`
/// binds positionally and `?` fills), the marks it applies, and what bringing it produces.
public sealed record BuilderEntry(
    string Name, bool Shared, IReadOnlyList<EventField> Fields,
    IReadOnlyList<string> Marks, string Generates);

public static class EventCatalog
{
    // origin/source are added by the runtime at emit; never scaffolded.
    private static readonly HashSet<string> Auto = new(StringComparer.Ordinal) { "origin", "source" };

    /// The provenance envelope the runtime auto-attaches to EVERY event — in every bundle and every app,
    /// always present, never declared. Always readable on a `hear` binding (`d.from.kind`, `d.cause`, …).
    /// See docs/LANGUAGE.md §3.8.
    public static readonly IReadOnlyList<(string Name, string Meaning)> Provenance = new[]
    {
        ("id", "this event's own id"),
        ("from", "the emitter (First-Class): from.name / from.kind / from.identity / from.shapes / from.marks"),
        ("origin", "the ECS entity that emitted, when inside a `target` (null outside one)"),
        ("source", "the originating entity/context"),
        ("bundle", "the emitting bundle"),
        ("cause", "the id of the event that caused this one"),
        ("trail", "id[] — the full causation chain that led here"),
    };

    /// The bundles this unit `use`s. `use` names a bundle, so a shared member of one is reachable by its
    /// bare name — and the tooling has to see exactly what the compiler sees, or `bring Button ?` expands
    /// to nothing for the very builders a project consumes most.
    private static List<string> Uses(CompilationUnit unit)
    {
        var names = new List<string>();
        void Walk(IEnumerable<Decl> ds)
        {
            foreach (var d in ds)
                switch (d)
                {
                    case UseDecl u when !names.Contains(u.Name, StringComparer.Ordinal): names.Add(u.Name); break;
                    case BundleDecl b: Walk(b.Members); break;
                    case PublicatorDecl p: Walk(p.Members); break;
                }
        }
        Walk(unit.Bundles);
        return names;
    }

    /// Index entries whose bundle segment is one this unit `use`s. Keys are `Author.Bundle[.Pub].Name`,
    /// the same suffix rule Lower.ResolveUsed applies.
    private static IEnumerable<KeyValuePair<string, T>> FromUsed<T>(
        IReadOnlyDictionary<string, T> index, IReadOnlyList<string> uses)
    {
        if (uses.Count == 0) yield break;
        foreach (var kv in index)
        {
            var parts = kv.Key.Split('.');
            if (parts.Length >= 3 && uses.Contains(parts[1], StringComparer.Ordinal)) yield return kv;
        }
    }

    /// Local shapes, plus the shared shapes of `use`d bundles under their bare names. A local
    /// declaration wins, which is `use` precedence — it only ever WIDENS what a bare name may mean.
    private static Dictionary<string, List<FieldDecl>> ShapesInScope(
        CompilationUnit unit, BundleIndex? index, IReadOnlyList<string> uses)
    {
        var shapes = Sig.Shapes(unit);
        if (index is null) return shapes;
        foreach (var kv in FromUsed(index.Shapes, uses))
        {
            string bare = kv.Key[(kv.Key.LastIndexOf('.') + 1)..];
            if (!shapes.ContainsKey(bare)) shapes[bare] = kv.Value.Members.OfType<FieldDecl>().ToList();
        }
        return shapes;
    }

    public static List<EventEntry> Catalog(CompilationUnit unit, string? projectDir = null)
    {
        var index = projectDir is null ? null : BundleIndex.For(projectDir);
        var uses = index is null ? new List<string>() : Uses(unit);
        var shapes = ShapesInScope(unit, index, uses);
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
                            .Select(f => new EventField(f.Name, f.Type, f.Required, f.Default, f.OriginShape))
                            .ToList();
                        list.Add(new EventEntry(e.Name, e.Shared, fields));   // [shared] = cross-bundle
                        break;
                }
        }
        Walk(unit.Bundles);

        // Then the shared events of `use`d bundles, under their bare names. Local wins, so a name this
        // unit declares is never displaced by an imported one.
        if (index is not null)
            foreach (var kv in FromUsed(index.Events, uses))
            {
                string bare = kv.Key[(kv.Key.LastIndexOf('.') + 1)..];
                if (list.Any(e => e.Name == bare)) continue;
                list.Add(new EventEntry(bare, true, Sig.Expand(kv.Value.Members, shapes)
                    .Where(f => !Auto.Contains(f.Name))
                    .Select(f => new EventField(f.Name, f.Type, f.Required, f.Default, f.OriginShape))
                    .ToList()));
            }
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
        // Every event also carries the provenance envelope — always present, on any bundle/app.
        sb.Append("\nprovenance (auto — always present on every event, readable in `hear`):\n");
        foreach (var (name, meaning) in Provenance)
            sb.Append("    ").Append(name).Append("  — ").Append(meaning).Append('\n');
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

    /// Every builder the unit declares, with its `$Shape` includes FLATTENED into the parameter list —
    /// which is what `bring X ?` has to fill, in the order `bring` binds them.
    ///
    /// A builder is where discovery matters most: a `shared` one is consumed from another bundle, so the
    /// declaration is not on the reader's screen and the include hides the field names one level down.
    public static List<BuilderEntry> Builders(CompilationUnit unit, string? projectDir = null)
    {
        var index = projectDir is null ? null : BundleIndex.For(projectDir);
        var uses = index is null ? new List<string>() : Uses(unit);
        var shapes = ShapesInScope(unit, index, uses);
        var list = new List<BuilderEntry>();
        void Walk(IEnumerable<Decl> decls)
        {
            foreach (var d in decls)
                switch (d)
                {
                    case BundleDecl b: Walk(b.Members); break;
                    case PublicatorDecl p: Walk(p.Members); break;
                    case BuilderDecl bd: list.Add(Entry(bd, bd.Name)); break;
                }
        }
        Walk(unit.Bundles);

        // Then the shared builders of `use`d bundles, under their bare names — which is exactly how
        // `bring Button(…)` resolves, so `bring Button ?` must see the same one.
        if (index is not null)
            foreach (var kv in FromUsed(index.Builders, uses))
            {
                string bare = kv.Key[(kv.Key.LastIndexOf('.') + 1)..];
                if (list.Any(b => b.Name == bare)) continue;   // local wins
                list.Add(Entry(kv.Value, bare));
            }
        return list;

        BuilderEntry Entry(BuilderDecl bd, string name)
        {
            var output = bd.Members.OfType<FieldDecl>()
                .FirstOrDefault(f => f.Name is "markup" or "code" or "css" or "line");
            var marks = bd.Members.OfType<MarkMember>().SelectMany(m => m.Marks).ToList();
            var fields = Sig.Expand(bd.Members.Where(m => !ReferenceEquals(m, output)).ToList(), shapes)
                .Select(f => new EventField(f.Name, f.Type, f.Required, f.Default, f.OriginShape))
                .ToList();
            return new BuilderEntry(name, bd.Shared, fields, marks,
                marks.Count > 0 ? "an identity" : output?.Name switch
                {
                    "code" => "@Script", "css" => "@Style", "line" => "@Print",
                    "markup" => "@Html", _ => "@" + name
                });
        }
    }

    /// A ready-to-fill `bring`, which is what the `?` sigil stands for at a call site.
    public static string Scaffold(BuilderEntry b)
    {
        var sb = new StringBuilder();
        sb.Append("// &").Append(b.Name).Append(" builds ").Append(b.Generates);
        if (b.Marks.Count > 0) sb.Append(' ').Append(string.Join(" ", b.Marks.Select(m => "#" + m)));
        sb.Append('\n');

        if (b.Fields.Count == 0) { sb.Append("bring ").Append(b.Name).Append("()\n"); return sb.ToString(); }

        // Positional, because that is how `bring` binds — so the comment carries the name each slot fills
        // and the shape it came from, which is the part an include would otherwise hide.
        sb.Append("bring ").Append(b.Name).Append("(\n");
        for (int i = 0; i < b.Fields.Count; i++)
        {
            var f = b.Fields[i];
            sb.Append("    ?").Append(i < b.Fields.Count - 1 ? "," : " ").Append("     // ").Append(f.Name)
              .Append(": ").Append(f.Type)
              .Append(f.OriginShape is null ? "" : "   from $" + f.OriginShape)
              .Append(f.Required ? "" : "   optional — default " + f.Default)
              .Append('\n');
        }
        sb.Append(")\n");
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
