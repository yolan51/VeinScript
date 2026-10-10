using System.Text;
using Vein.Compiler.Parsing;

namespace Vein.Compiler.Tooling;

// Read-only event and builder tooling: discover what a file can emit and bring (with required-vs-defaulted
// fields) and scaffold a ready-to-fill `emit` / `bring`. Feeds `veinc events` / `veinc scaffold` and the
// Workbench's `?`. Grammar is unchanged — this only READS.
//
// WHAT EACH THING TAKES comes from `Signatures`, which asks the compiler's own resolution. This file used
// to work that out itself — its own `need` widening, include expansion and variant flattening — and the
// copy had drifted: a kit builder's includes resolved in the consumer's scope, aliased needs widened bare
// names, and two same-named events from different bundles collapsed into whichever came first. What is
// left here is presentation.

public sealed record EventField(string Name, string Type, bool Required, string? Default, string? OriginShape = null);

public sealed record EventEntry(string Name, bool Shared, IReadOnlyList<EventField> Fields)
{
    /// `Author.Bundle.Publicator` for an event from another bundle; null for one declared here.
    public string? Owner { get; init; }
}

/// A builder as the tooling sees it: its parameters with `$Shape` includes flattened (the list `bring`
/// binds positionally and `?` fills), the marks it applies, and what bringing it produces.
public sealed record BuilderEntry(
    string Name, bool Shared, IReadOnlyList<EventField> Fields,
    IReadOnlyList<string> Marks, string Generates)
{
    /// The builder this one VARIES (`builder BigCoin from Coin { value = 5 }`), or null for an ordinary
    /// one. `Fields` is already the flattened, remaining list — correct, and enough to PLACE a variant
    /// but not to say what it is.
    ///
    /// A palette showing `Coin` and `BigCoin` side by side can only distinguish them by one asking for
    /// fewer arguments, which reads as an unrelated builder with a similar name. With the base and the
    /// fixes it can write the sentence that makes a variant obvious: "BigCoin: a Coin with value 5".
    public string? Base { get; init; }

    /// What the variant fixed, by parameter name, rendered as written. Empty for an ordinary builder.
    /// These are exactly the parameters ABSENT from `Fields`, which is the other half of the sentence.
    public IReadOnlyDictionary<string, string> Fixed { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// `Author.Bundle.Publicator` for a builder from another bundle; null for one declared here.
    public string? Owner { get; init; }
}

public static class EventCatalog
{
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

    /// Every event the unit declares, then — given a project — the shared events of the bundles it `need`s.
    /// Two needed bundles exporting one name both appear, each with its `Owner`: listing one would be the
    /// silent pick the compiler refuses (VS0216).
    public static List<EventEntry> Catalog(CompilationUnit unit, string? projectDir = null) =>
        Signatures.For(unit, projectDir).InScope(SignatureKind.Event, includeNeeded: projectDir is not null)
                  .Select(Entry).ToList();

    /// Every builder the unit declares, then the shared builders of needed bundles — each with its
    /// `$Shape` includes FLATTENED into the parameter list in the order `bring` binds them, a variant's
    /// fixed parameters removed.
    ///
    /// A builder is where discovery matters most: a `shared` one is consumed from another bundle, so the
    /// declaration is not on the reader's screen and the include hides the field names one level down.
    public static List<BuilderEntry> Builders(CompilationUnit unit, string? projectDir = null) =>
        Signatures.For(unit, projectDir).InScope(SignatureKind.Builder, includeNeeded: projectDir is not null)
                  .Select(BuilderEntryOf).ToList();

    public static EventEntry Entry(Signature s) => new(s.Name, s.Shared, s.Fields) { Owner = s.Origin };

    public static BuilderEntry BuilderEntryOf(Signature s) =>
        new(s.Name, s.Shared, s.Fields, s.Marks, s.Generates ?? "@" + s.Name)
        { Base = s.Base, Fixed = s.Fixed, Owner = s.Origin };

    /// Human listing: every event, `[shared]` when exported, each field required or defaulted. An event
    /// from another bundle names its owner, which is what tells two same-named ones apart.
    public static string Render(IReadOnlyList<EventEntry> events)
    {
        var sb = new StringBuilder();
        foreach (var e in events)
        {
            sb.Append('@').Append(e.Name);
            if (e.Shared) sb.Append("  [shared]");
            if (e.Owner is not null) sb.Append("  *").Append(e.Owner);
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
    public static string Body(EventEntry e) => Body(e.Fields);

    /// The same body for any field list — an event's payload, or a shape's for `attach`.
    public static string Body(IReadOnlyList<EventField> fields)
    {
        if (fields.Count == 0) return "{ }";
        var sb = new StringBuilder("{\n");
        foreach (var f in fields.Where(f => f.Required).Concat(fields.Where(f => !f.Required)))
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
    internal static string Placeholder(EventField f)
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

    /// Everything `bring` accepts before its argument list. Named groups: `path` (the qualifier, when
    /// written) and `name` (the bare builder name).
    ///
    ///   bring Unit                      bring 8 Unit
    ///   bring &amp;Unit                     bring 8 &amp;Unit
    ///   bring *alice.Combat.Api.&amp;Unit    bring *alice.Combat.Api.Unit
    ///
    /// Here rather than in the Workbench so it can be tested: the editor's `?` expansion used `\w+`,
    /// which matches neither `&amp;` nor a dotted path, so `bring &amp;Unit ?` silently expanded to nothing
    /// while `bring Unit ?` worked. One definition now, shared with signature help (FillHeads).
    public const string BringHead = FillHeads.Bring;

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
    public static string Args(BuilderEntry b, bool compact = false) => Args(b.Fields, compact);

    public static string Args(IReadOnlyList<EventField> fields, bool compact = false)
    {
        if (fields.Count == 0) return "()";

        // The one-line form: you already know the signature and want the slots.
        if (compact) return "(" + string.Join(", ", fields.Select(_ => "base")) + ")";

        var sb = new StringBuilder("(\n");
        for (int i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            // THE `)` CLOSES THE LAST SLOT'S LINE, never a line of its own. A newline ends a statement unless
            // the line ends in something that cannot (RULES 1). `base` is a keyword and cannot, so `base` then
            // `)` on the next line worked — until `base` was replaced with the value it asks for: `50` ends a
            // statement, and the `)` below it was an error. Written this way the slot can be overwritten.
            bool last = i == fields.Count - 1;
            sb.Append("    base").Append(last ? ")" : ",").Append("     // ").Append(f.Name)
              .Append(": ").Append(f.Type)
              .Append(f.OriginShape is null ? "" : "   from $" + f.OriginShape)
              .Append(f.Required ? "   REQUIRED — give it a value" : "   optional — default " + f.Default);
            if (!last) sb.Append('\n');
        }
        return sb.ToString();
    }

    /// One line per field for a completion popup: `name: type   from $Shape`. The popup is shown when
    /// `?` is typed INSIDE a payload, where `?` is the documented fill-the-rest token and must survive
    /// being dismissed — so this only ever offers, it never rewrites.
    public static List<(string Label, string Insert)> FieldPicks(IReadOnlyList<EventField> fields) =>
        fields.Select(f => (
            Label: f.Name + ": " + f.Type
                 + (f.OriginShape is null ? "" : "   from $" + f.OriginShape)
                 + (f.Required ? "" : "   = " + f.Default),
            Insert: f.Name + ": ")).ToList();
}
