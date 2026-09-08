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

    /// The bundles this unit `need`s, as `Author.Bundle`. A shared member of one is reachable by its
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
                    case NeedDecl n when !names.Contains(n.Key, StringComparer.Ordinal): names.Add(n.Key); break;
                    case BundleDecl b: Walk(b.Members); break;
                    case PublicatorDecl p: Walk(p.Members); break;
                }
        }
        Walk(unit.Bundles);
        return names;
    }

    /// Index entries belonging to a bundle this unit needs. Keys are `Author.Bundle[.Pub].Name`, matched
    /// on AUTHOR AND BUNDLE — the same rule Lower.ResolveUsed applies, so the tooling offers exactly what
    /// the compiler will resolve rather than another author's bundle of the same name.
    private static IEnumerable<KeyValuePair<string, T>> FromUsed<T>(
        IReadOnlyDictionary<string, T> index, IReadOnlyList<string> uses)
    {
        if (uses.Count == 0) yield break;
        foreach (var kv in index)
        {
            var parts = kv.Key.Split('.');
            if (parts.Length >= 3 && uses.Contains(parts[0] + "." + parts[1], StringComparer.Ordinal)) yield return kv;
        }
    }

    /// Local shapes, plus the shared shapes of needed bundles under their bare names. A local
    /// declaration wins, which is `need` precedence — it only ever WIDENS what a bare name may mean.
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


    /// The marks each in-scope shape BRINGS (RULES 15c) — local declarations first, then the shared
    /// shapes of needed bundles, the same precedence their fields get.
    ///
    /// The tooling needs this because a mark can now arrive two ways. `builder Cam { $Actor $GameCamera }`
    /// applies `#CameraFollow` if `$GameCamera` brings it, and reading only the builder's own `mark`
    /// lines sees none — so the editor's layer list lost the mark, and worse, the same count decides
    /// whether bringing it makes an IDENTITY. A builder whose marks all arrive through its shapes was
    /// classified as emitting an event, and every surface that lists identity builders filtered it out:
    /// it compiled, ran, and brought a perfectly good identity the editor said did not exist.
    private static Dictionary<string, List<string>> ShapeMarksInScope(
        CompilationUnit unit, BundleIndex? index, IReadOnlyList<string> uses)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        void Walk(IEnumerable<Decl> ms)
        {
            foreach (var m in ms)
                switch (m)
                {
                    case ShapeDecl s:
                        map[s.Name] = s.Members.OfType<MarkMember>().SelectMany(x => x.Marks)
                                       .Distinct(StringComparer.Ordinal).ToList();
                        break;
                    case BundleDecl b: Walk(b.Members); break;
                    case PublicatorDecl p: Walk(p.Members); break;
                }
        }
        Walk(unit.Bundles);

        if (index is not null)
            foreach (var kv in FromUsed(index.Shapes, uses))
            {
                string bare = kv.Key[(kv.Key.LastIndexOf('.') + 1)..];
                if (map.ContainsKey(bare)) continue;                      // local wins
                map[bare] = kv.Value.Members.OfType<MarkMember>().SelectMany(x => x.Marks)
                             .Distinct(StringComparer.Ordinal).ToList();
            }

        return map;
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

        // Then the shared events of needed bundles, under their bare names. Local wins, so a name this
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
    public static string Scaffold(EventEntry e) => "emit @" + e.Name + " " + Body(e);

    /// Just the `{ … }`, for a caller that has already typed `emit @E` — the Workbench's `?`. Required
    /// fields first, each labelled with its type and, when it arrived through an include, the `$Shape`
    /// it came from. That provenance is the part an include otherwise hides: the field is real, but its
    /// name appears nowhere in the event's own declaration.
    public static string Body(EventEntry e)
    {
        if (e.Fields.Count == 0) return "{ }";
        var sb = new StringBuilder("{\n");
        foreach (var f in e.Fields.Where(f => f.Required).Concat(e.Fields.Where(f => !f.Required)))
            sb.Append("    ").Append(f.Name).Append(": ").Append(Placeholder(f)).Append("      // ")
              .Append(f.Required ? "required — " + f.Type : "optional — " + f.Type + " = " + f.Default)
              .Append(f.OriginShape is null ? "" : "   from $" + f.OriginShape)
              .Append('\n');
        return sb.Append('}').ToString();
    }

    /// A VALUE for a field, not a `?`.
    ///
    /// This used to emit `name: ?`, and that does not parse: `?` is the standalone fill-the-rest marker,
    /// and in value position it is VS0104 "unexpected '?' in expression". Every scaffolded emit body
    /// with a field in it was code that could not compile — which is a strange thing for a scaffold to
    /// hand you, and stranger still that the tool that produced it is the one that would reject it.
    ///
    /// A typed zero compiles and is editable, which is what a placeholder is for. A declared default is
    /// used when there is one, so the line already says what the field would have been.
    private static string Placeholder(EventField f)
    {
        if (!f.Required && f.Default is { Length: > 0 } d) return d;

        return f.Type switch
        {
            "string" or "Mark" => "\"\"",
            "int" => "0",
            "float" => "0.0",
            "bool" => "false",
            "Entity" => "0",
            _ => "0"
        };
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
        var shapeMarks = ShapeMarksInScope(unit, index, uses);
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

            // The builder's own `mark` lines, THEN the marks its shapes bring (RULES 15c). Both are
            // applied by `bring`, so both belong here — and the count below decides whether this builds
            // an identity at all, which is why missing the second kind hid whole builders from the
            // editor. `Lower.BuildsIdentity` draws the same line for the same reason.
            var marks = bd.Members.OfType<MarkMember>().SelectMany(m => m.Marks).ToList();
            foreach (var si in bd.Members.OfType<ShapeInclude>())
                if (shapeMarks.TryGetValue(si.Shape, out var brought))
                    foreach (var mk in brought)
                        if (!marks.Contains(mk, StringComparer.Ordinal)) marks.Add(mk);

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

    /// Everything `bring` accepts before its argument list, capturing the BARE builder name in group 1.
    ///
    ///   bring Unit                      bring 8 Unit
    ///   bring &amp;Unit                     bring 8 &amp;Unit
    ///   bring *alice.Combat.Api.&amp;Unit    bring *alice.Combat.Api.Unit
    ///
    /// The sigil and the qualifier are both optional because `bring` treats them so, and the BARE name
    /// is the right capture because that is what `Builders` keys on — a `use`d bundle's builders are
    /// stored under their bare names precisely because that is how `bring Button(…)` resolves.
    ///
    /// Here rather than in the Workbench so it can be tested: the editor's `?` expansion used `\w+`,
    /// which matches neither `&amp;` nor a dotted path, so `bring &amp;Unit ?` silently expanded to nothing
    /// while `bring Unit ?` worked. Writing the sigil is the more explicit form and was the one the
    /// tooling ignored.
    public const string BringHead = @"bring\s+(?:\d+\s+)?(?:\*[\w.]*\.)?&?(\w+)";

    /// A ready-to-fill `bring`, which is what the `?` sigil stands for at a call site.
    public static string Scaffold(BuilderEntry b)
    {
        var sb = new StringBuilder();
        sb.Append("// &").Append(b.Name).Append(" builds ").Append(b.Generates);
        if (b.Marks.Count > 0) sb.Append(' ').Append(string.Join(" ", b.Marks.Select(m => "#" + m)));
        sb.Append('\n');

        sb.Append("bring ").Append(b.Name).Append(Args(b)).Append('\n');
        return sb.ToString();
    }

    /// Just the `( … )`, for a caller that has already typed `bring X` — the Workbench's `?`.
    ///
    /// Positional, because that is how `bring` binds, so the slot itself carries no name. The comment
    /// supplies it along with the `$Shape` the field came from — which for a builder is the whole point:
    /// an include flattens someone else's shape into this parameter list, and the reader is looking at
    /// four bare slots with nothing on screen to say which is which.
    ///
    /// EVERY SLOT IS `base`, and that is what makes this paste-able. It used to be `?`, which does not
    /// parse: `bring X(?, ?)` is VS0100, because `?` fills the WHOLE argument list and is not a
    /// per-slot token. `base` is — it means "this parameter's declared default", it mixes freely with
    /// real values, and replacing one is exactly the edit a scaffold exists to invite.
    ///
    /// A parameter with no default still takes `base` and raises VS0231, which reads "'X' parameter 'a'
    /// has no default, so `base` fills it with a typed zero. Give the field a default in its shape, or
    /// pass a value." That is a better outcome than a silent hole: the compiler names the slots you
    /// still have to think about.
    public static string Args(BuilderEntry b, bool compact = false)
    {
        if (b.Fields.Count == 0) return "()";

        // The one-line form: you already know the signature and want the slots.
        if (compact) return "(" + string.Join(", ", b.Fields.Select(_ => "base")) + ")";

        var sb = new StringBuilder("(\n");
        for (int i = 0; i < b.Fields.Count; i++)
        {
            var f = b.Fields[i];
            sb.Append("    base").Append(i < b.Fields.Count - 1 ? "," : " ").Append("     // ").Append(f.Name)
              .Append(": ").Append(f.Type)
              .Append(f.OriginShape is null ? "" : "   from $" + f.OriginShape)
              .Append(f.Required ? "   REQUIRED — give it a value" : "   optional — default " + f.Default)
              .Append('\n');
        }
        return sb.Append(')').ToString();
    }

    /// One line per field for a completion popup: `name: type   from $Shape`. The popup is shown when
    /// `?` is typed INSIDE a payload or an argument list, where `?` is the documented fill-the-rest
    /// token and must survive being dismissed — so this only ever offers, it never rewrites.
    public static List<(string Label, string Insert)> FieldPicks(IReadOnlyList<EventField> fields) =>
        fields.Select(f => (
            Label: f.Name + ": " + f.Type
                 + (f.OriginShape is null ? "" : "   from $" + f.OriginShape)
                 + (f.Required ? "" : "   = " + f.Default),
            Insert: f.Name + ": ")).ToList();

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
