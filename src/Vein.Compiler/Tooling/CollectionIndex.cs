using Vein.Compiler.Parsing;

namespace Vein.Compiler.Tooling;

/// What each `~` collection IS (D24) — the question an editor asks at every `.~` it shows. A `.~slots` says
/// "many", not "map" or "list": the kind lives in the shape declaration. Hover, the inline `‹map›` hint and
/// completion all answer from here, so they cannot disagree about a field.
///
/// LEXICAL where it reads the text: completion fires on a half-typed line, where a parse tree either does
/// not exist or does not hold the token being typed (the reason <see cref="SourceContext"/> is lexical).
/// The shape is always written right before the `.~` — `x.Hero.~slots`, `$Hero.~slots` — so the text alone
/// names it; only the declarations come from the last good parse.
public sealed class CollectionIndex
{
    private readonly Dictionary<(string Shape, string Field), CollectionSpec> _specs = new();

    public static CollectionIndex Build(CompilationUnit? unit)
    {
        var index = new CollectionIndex();
        if (unit is null) return index;
        void Decl(Decl d)
        {
            switch (d)
            {
                case BundleDecl b: foreach (var x in b.Members) Decl(x); break;
                case PublicatorDecl p: foreach (var x in p.Members) Decl(x); break;
                case ShapeDecl s:
                    foreach (var f in s.Members.OfType<FieldDecl>())
                        if (f.Collection is { } c) index._specs[(s.Name, f.Name)] = c;
                    break;
            }
        }
        foreach (var b in unit.Bundles) Decl(b);
        return index;
    }

    public CollectionSpec? Spec(string shape, string field) => _specs.GetValueOrDefault((shape, field));

    /// Every collection declared, as `Shape.~<kind> field: types` — what changed, when an editor asks.
    public IEnumerable<string> All => _specs.Select(kv => kv.Key.Shape + "." + Describe(kv.Key.Field, kv.Value));

    /// The shape's collections, in declaration order.
    public IEnumerable<(string Field, CollectionSpec Spec)> Of(string shape) =>
        _specs.Where(kv => kv.Key.Shape == shape).Select(kv => (kv.Key.Field, kv.Value));

    /// The declaration as it is written: `~<map> slots: string | owns $Item`.
    public static string Describe(string field, CollectionSpec c) => $"~<{Tag(c)}> {field}: {Types(c)}";

    /// What the collection holds, as declared: `string | owns $Item`, `$Person`, `int`.
    public static string Types(CollectionSpec c)
    {
        string value = (c.Owns ? "owns " : "") + (c.Carries is { } s ? "$" + s : c.Element);
        return c.Key is null ? value : (c.KeyCarries is { } k ? "$" + k : c.Key) + " | " + value;
    }

    /// The short form an inline hint shows: `map`, `set`, `list`, `ring 3`.
    public static string Tag(CollectionSpec c) => c.Kind == "ring" ? $"ring {c.Size}" : c.Kind;

    /// The words this kind has, as completion offers them: what the row shows, and what it types.
    public static IReadOnlyList<(string Label, string Insert)> Words(CollectionSpec c) => c.Kind == "map"
        ? new[] { ("put(key, value)", "put("), ("remove(key)", "remove("), ("clear()", "clear()"), ("has(key)", "has(") }
        : new[] { ("add(x)", "add("), ("remove(x)", "remove("), ("clear()", "clear()"), ("has(x)", "has(") };

    /// The four kinds `~<` offers in a declaration, each with what it means.
    public static IReadOnlyList<(string Label, string Insert, string Describe)> Kinds { get; } = new[]
    {
        ("list", "list", "ordered, repeats allowed — ~<list> name: Type"),
        ("set", "set", "each value once, in the order added — ~<set> name: Type"),
        ("ring 20", "ring 20", "only the last N — the oldest drops off — ~<ring 20> name: Type"),
        ("map", "map", "a value per key — ~<map> name: Key | Value"),
    };

    /// The `Shape.~field` the `.~name` at or around `offset` reads — or, when the caret is right after
    /// `.~name.`, the one before that dot. Null when the text there is not a collection read.
    public static (string Shape, string Field)? At(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        int end = offset;
        while (end < text.Length && IsName(text[end])) end++;
        int start = offset;
        while (start > 0 && IsName(text[start - 1])) start--;
        // `h.Hero.~slots.|` — the caret sits after a dot that follows the collection.
        if (start == end && start > 0 && text[start - 1] == '.')
        {
            end = start - 1;
            start = end;
            while (start > 0 && IsName(text[start - 1])) start--;
        }
        if (start == end || start < 2 || text[start - 1] != '~' || text[start - 2] != '.') return null;
        return ShapeBefore(text, start - 2) is { } shape ? (shape, text[start..end]) : null;
    }

    /// The shape whose collection is being named at `offset` — `h.Hero.~|` or `h.Hero.~sl|` — and where that
    /// name starts. Null anywhere else.
    public static (string Shape, int NameStart)? Naming(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        int start = offset;
        while (start > 0 && IsName(text[start - 1])) start--;
        if (start < 2 || text[start - 1] != '~' || text[start - 2] != '.') return null;
        return ShapeBefore(text, start - 2) is { } shape ? (shape, start) : null;
    }

    /// Whether a declaration's kind is being typed at `offset` — `~<|` or `~<ri|` — and where it starts.
    public static int? KindAt(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        int start = offset;
        while (start > 0 && IsName(text[start - 1])) start--;
        return start >= 2 && text[start - 1] == '<' && text[start - 2] == '~' ? start : null;
    }

    /// Every `.~name` in `text`: where its name ends, and the `Shape.~field` it reads.
    public static IEnumerable<(int End, string Shape, string Field)> Reads(string text)
    {
        for (int i = text.IndexOf(".~", StringComparison.Ordinal); i >= 0; i = text.IndexOf(".~", i + 2, StringComparison.Ordinal))
        {
            int s = i + 2, e = s;
            while (e < text.Length && IsName(text[e])) e++;
            if (e > s && ShapeBefore(text, i) is { } shape) yield return (e, shape, text[s..e]);
        }
    }

    /// The identifier ending right before the `.` at `dot` — `Hero` in `h.Hero.~` and in `$Hero.~`.
    private static string? ShapeBefore(string text, int dot)
    {
        int s = dot;
        while (s > 0 && IsName(text[s - 1])) s--;
        return s < dot ? text[s..dot] : null;
    }

    private static bool IsName(char c) => char.IsLetterOrDigit(c) || c == '_';
}
