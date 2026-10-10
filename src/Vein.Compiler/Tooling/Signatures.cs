using Vein.Compiler.Ir;
using Vein.Compiler.Parsing;

namespace Vein.Compiler.Tooling;

public enum SignatureKind { Event, Builder, Shape }

/// What an `@Event`, `&Builder` or `$Shape` takes, as the compiler resolves it from where the reference is
/// written. `Fields` are in binding order for a builder (fixed parameters removed) and declaration order
/// for an event or a shape, includes expanded either way.
public sealed record Signature(SignatureKind Kind, string Name, string? OwnerKey, IReadOnlyList<EventField> Fields)
{
    public bool Shared { get; init; }
    public string? Doc { get; init; }

    /// A builder: every mark `bring` applies. A shape: the marks attaching it brings (RULES 15c).
    public IReadOnlyList<string> Marks { get; init; } = Array.Empty<string>();

    /// A builder: "an identity", or the event it emits — `@Html`, `@Crate`.
    public string? Generates { get; init; }

    /// A variant: the base its chain ends at, and what it fixes by name.
    public string? Base { get; init; }
    public IReadOnlyDictionary<string, string> Fixed { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public string Sigil => Kind switch { SignatureKind.Event => "@", SignatureKind.Builder => "&", _ => "$" };

    /// `Author.Bundle.Publicator` for a member of another bundle; null for one declared here.
    public string? Origin => OwnerKey is { } k && k.LastIndexOf('.') is > 0 and var dot ? k[..dot] : null;

    /// How to write it unambiguously from anywhere: `*kit.Shop.Api.@Sold`, or `@Sold` when it is local.
    public string Reference => Origin is null ? Sigil + Name : "*" + Origin + "." + Sigil + Name;
}

/// A reference as somebody typed it: `@Sold`, `&Crate`, `$Price`, `Crate`, `*kit.Shop.Api.@Sold`.
/// `Kind` is null when no sigil says which.
public sealed record SignatureRef(SignatureKind? Kind, IReadOnlyList<string> Path, string Name)
{
    public static SignatureRef? Parse(string raw)
    {
        string s = raw.Trim();
        if (s.Length == 0) return null;

        var path = Array.Empty<string>();
        if (s[0] == '*')
        {
            int cut = s.LastIndexOf('.');
            if (cut <= 1) return null;
            path = s[1..cut].Split('.');
            s = s[(cut + 1)..];
            if (path.Any(p => p.Length == 0)) return null;
        }

        SignatureKind? kind = s.Length > 0 ? s[0] switch
        {
            '@' => SignatureKind.Event, '&' => SignatureKind.Builder, '$' => SignatureKind.Shape, _ => null,
        } : null;
        string name = kind is null ? s : s[1..];
        return name.Length > 0 && name.All(c => char.IsLetterOrDigit(c) || c == '_')
            ? new SignatureRef(kind, path, name) : null;
    }

    public string Written =>
        (Path.Count > 0 ? "*" + string.Join(".", Path) + "." : "") +
        Kind switch { SignatureKind.Event => "@", SignatureKind.Builder => "&", SignatureKind.Shape => "$", _ => "" } + Name;

    /// The reference under `offset` in source — `@Sold`, `$Price`, `*kit.Shop.Api.&Crate`, a bare `Crate`
    /// — with its sigil and any `*path.` in front. Null for a mark, a number, or a segment of a path (the
    /// name at its END is the reference).
    public static SignatureRef? At(string text, int offset)
    {
        if (offset < 0 || offset > text.Length) return null;
        int i = offset;
        if (i >= text.Length || !Id(text[i])) { if (i > 0 && Id(text[i - 1])) i--; else return null; }
        int s = i; while (s > 0 && Id(text[s - 1])) s--;
        int e = i; while (e < text.Length && Id(text[e])) e++;
        string name = text[s..e];
        if (name.Length == 0 || char.IsDigit(name[0]) || (e < text.Length && text[e] == '.')) return null;

        int p = s;
        SignatureKind? kind = null;
        if (p > 0 && text[p - 1] is '@' or '&' or '$' or '#')
        {
            if (text[p - 1] == '#') return null;
            kind = text[p - 1] switch { '@' => SignatureKind.Event, '&' => SignatureKind.Builder, _ => SignatureKind.Shape };
            p--;
        }

        var path = Array.Empty<string>();
        if (p > 0 && text[p - 1] == '.')
        {
            int t = p - 2;
            while (t >= 0 && (Id(text[t]) || text[t] == '.')) t--;
            if (t < 0 || text[t] != '*') return null;               // `s.item` — a member, not a reference
            path = text[(t + 1)..(p - 1)].Split('.');
            if (path.Any(x => x.Length == 0)) return null;
        }
        return new SignatureRef(kind, path, name);

        static bool Id(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}

/// The answer to a lookup: the signature, or — for a bare name two needed bundles both export — no
/// signature and the candidates, which is VS0216 in the compiler and must not be a silent pick here.
public sealed record SignatureLookup(Signature? Hit, IReadOnlyList<string> Candidates)
{
    public bool Ambiguous => Hit is null && Candidates.Count > 1;

    public static readonly SignatureLookup None = new(null, Array.Empty<string>());
}

/// The one place tooling asks what a reference takes. Built per compile; cheap — one declaration pass per
/// bundle, over an index that is already cached.
public sealed class Signatures
{
    private readonly CompilationUnit? _unit;
    private readonly List<(BundleDecl Bundle, Lower Scope)> _bundles = new();
    private readonly Lower _primary;

    private Signatures(CompilationUnit? unit, string? projectDir, int line)
    {
        _unit = unit;
        foreach (var b in unit?.Bundles ?? (IReadOnlyList<BundleDecl>)Array.Empty<BundleDecl>())
            _bundles.Add((b, Lower.ToolingScope(b, projectDir)));

        // The bundle the reference is written in decides what a bare name means: its needs, its aliases.
        // `line` picks the last bundle starting at or above it; without one, the first.
        _primary = _bundles.Count == 0 ? Lower.ToolingScope(null, projectDir)
                 : line <= 0 ? _bundles[0].Scope
                 : _bundles.LastOrDefault(x => x.Bundle.Span.Line <= line).Scope ?? _bundles[0].Scope;
    }

    /// `line` (1-based) is where the reference is, for a file holding more than one bundle.
    public static Signatures For(CompilationUnit? unit, string? projectDir, int line = 0) => new(unit, projectDir, line);

    public SignatureLookup Event(IReadOnlyList<string> path, string name) => Lookup(SignatureKind.Event, path, name);
    public SignatureLookup Builder(IReadOnlyList<string> path, string name) => Lookup(SignatureKind.Builder, path, name);
    public SignatureLookup Shape(IReadOnlyList<string> path, string name) => Lookup(SignatureKind.Shape, path, name);

    /// A reference with no sigil tries an event, then a builder, then a shape — the first kind that
    /// resolves, or is ambiguous, answers.
    public SignatureLookup Find(SignatureRef r)
    {
        if (r.Kind is { } k) return Lookup(k, r.Path, r.Name);
        foreach (var kind in new[] { SignatureKind.Event, SignatureKind.Builder, SignatureKind.Shape })
        {
            var hit = Lookup(kind, r.Path, r.Name);
            if (hit.Hit is not null || hit.Ambiguous) return hit;
        }
        return SignatureLookup.None;
    }

    private SignatureLookup Lookup(SignatureKind kind, IReadOnlyList<string> path, string name)
    {
        if (path.Count == 0)
        {
            var candidates = _primary.Candidates(SigilOf(kind), name);
            if (candidates.Count > 1) return new SignatureLookup(null, candidates);
        }

        var sig = Resolve(_primary, kind, path, name);
        return sig is null ? SignatureLookup.None : new SignatureLookup(sig, Array.Empty<string>());
    }

    /// Everything a bare name can reach from this unit, for lists and catalogs: each bundle's own
    /// declarations in order, then — when `includeNeeded` — the shared members of the bundles it `need`s,
    /// each resolved by its full path. A collision therefore shows BOTH members, each with its owner; a name
    /// declared locally hides the needed one, which is `need` precedence.
    public IReadOnlyList<Signature> InScope(SignatureKind kind, bool includeNeeded = true)
    {
        var list = new List<Signature>();
        var local = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (bundle, scope) in _bundles)
            foreach (var name in Declared(bundle.Members, kind))
                if (local.Add(name) && Resolve(scope, kind, Array.Empty<string>(), name) is { } sig)
                    list.Add(sig);

        if (!includeNeeded) return list;

        var needed = _bundles.SelectMany(b => b.Scope.NeededBundles).Distinct(StringComparer.Ordinal).ToList();
        var index = _primary.ToolingIndex;
        IEnumerable<string> keys = kind switch
        {
            SignatureKind.Event => index.Events.Keys,
            SignatureKind.Builder => index.Builders.Keys,
            _ => index.Shapes.Keys,
        };
        foreach (var key in keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var parts = key.Split('.');
            if (parts.Length < 3 || !needed.Contains(parts[0] + "." + parts[1], StringComparer.Ordinal)) continue;
            if (local.Contains(parts[^1])) continue;
            if (Resolve(_primary, kind, parts[..^1], parts[^1]) is { } sig) list.Add(sig);
        }
        return list;
    }

    // ---- hover -------------------------------------------------------------------------------------------

    /// What hovering at `offset` should say, or null when it is not about a signature: a field name inside
    /// `emit @E { … }` / `attach $S to e { … }`, a slot inside `bring X(…)`, or the reference itself —
    /// from any bundle, by any path, the same answer `?` would give.
    public string? DescribeAt(string text, int offset)
    {
        if (offset < 0 || offset > text.Length) return null;

        // A field or a slot: the innermost open bracket before the word, and the head in front of it.
        int open = QuestionFill.Unclosed(text, offset);
        if (open >= 0 && FillHeads.Before(text, open) is { } head)
        {
            bool paren = text[open] == '(';
            if (paren == (head.Kind == SignatureKind.Builder) && Find(new SignatureRef(head.Kind, head.Path, head.Name)).Hit is { } sig)
            {
                if (paren)
                {
                    // Any token in an argument — a literal as much as a name — is IN a slot, and positional
                    // binding means the slot is the one thing it cannot say about itself.
                    var (commas, _) = QuestionFill.Args(text[(open + 1)..offset]);
                    if (commas < sig.Fields.Count)
                        return $"{sig.Name} slot {commas}: {Field(sig.Fields[commas])}";
                }
                else if (SignatureRef.At(text, offset) is { Kind: null, Path.Count: 0 } word
                         && sig.Fields.FirstOrDefault(f => f.Name == word.Name) is { } f && FollowedByColon(text, offset))
                    return $"{sig.Sigil}{sig.Name}.{Field(f)}";
            }
        }

        if (SignatureRef.At(text, offset) is not { } r) return null;
        var lookup = Find(r);
        if (lookup.Ambiguous)
            return $"{r.Written} is ambiguous here — " +
                   string.Join(" and ", lookup.Candidates.Select(c => "*" + c[..(c.LastIndexOf('.') + 1)] + Sigil(r.Kind) + c[(c.LastIndexOf('.') + 1)..])) +
                   " (VS0216). Write the path to choose one.";
        return lookup.Hit is { } hit ? Describe(hit) : null;
    }

    /// A signature as a hover shows it: the head and where it comes from, one field per line with its type,
    /// default and origin (slot numbers for a builder, whose arguments bind by position), then the doc.
    public static string Describe(Signature s)
    {
        var lines = new List<string>();
        string head = s.Kind switch
        {
            SignatureKind.Builder => "builder &" + s.Name,
            SignatureKind.Event => "event @" + s.Name,
            _ => "shape $" + s.Name,
        };
        if (s.Origin is not null) head += "   *" + s.Origin;
        if (s.Kind == SignatureKind.Builder)
            head += " — builds " + s.Generates + (s.Marks.Count > 0 ? " " + string.Join(" ", s.Marks.Select(m => "#" + m)) : "");
        else if (s.Kind == SignatureKind.Shape && s.Marks.Count > 0)
            head += " — attaching it also marks " + string.Join(" ", s.Marks.Select(m => "#" + m));
        lines.Add(head);
        if (s.Base is not null)
            lines.Add("a variant of &" + s.Base + ", fixing " + string.Join(", ", s.Fixed.Select(kv => kv.Key + " = " + kv.Value)));

        if (s.Fields.Count == 0) lines.Add(s.Kind == SignatureKind.Builder ? "  takes nothing" : "  no fields");
        int width = s.Fields.Count == 0 ? 0 : s.Fields.Max(f => Bare(f).Length);
        for (int i = 0; i < s.Fields.Count; i++)
        {
            var f = s.Fields[i];
            string slot = s.Kind == SignatureKind.Builder ? $"{i}  " : "";
            lines.Add("  " + slot + Bare(f).PadRight(width) + (f.OriginShape is null ? "" : "   from $" + f.OriginShape));
        }
        if (s.Doc is { Length: > 0 } doc) lines.Add(doc);
        return string.Join("\n", lines);
    }

    private static string Bare(EventField f) => f.Name + ": " + f.Type + (f.Required ? "" : " = " + f.Default);
    private static string Field(EventField f) => Bare(f) + (f.OriginShape is null ? "" : "   from $" + f.OriginShape);

    private static bool FollowedByColon(string text, int offset)
    {
        int i = offset;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
        while (i < text.Length && text[i] is ' ' or '\t') i++;
        return i < text.Length && text[i] == ':';
    }

    private static string Sigil(SignatureKind? k) => k switch
    {
        SignatureKind.Builder => "&", SignatureKind.Shape => "$", _ => "@",
    };

    private static Signature? Resolve(Lower scope, SignatureKind kind, IReadOnlyList<string> path, string name)
    {
        var t = kind switch
        {
            SignatureKind.Event => scope.EventSignature(path, name),
            SignatureKind.Builder => scope.BuilderSignature(path, name),
            _ => scope.ShapeSignature(path, name),
        };
        if (t is null) return null;

        var fields = t.Fields.Select(f => new EventField(
            f.Name, Sig.TypeStr(f.Type), f.Default is null, Sig.DefaultText(f.Default), f.Origin)).ToList();
        return new Signature(kind, name, t.Key, fields)
        {
            Shared = t.Decl.Shared,
            Doc = t.Decl.Doc,
            Marks = t.Marks,
            Generates = t.Generates,
            Base = t.Root,
            Fixed = t.Fixed.ToDictionary(kv => kv.Key, kv => Sig.DefaultText(kv.Value) ?? "", StringComparer.Ordinal),
        };
    }

    private static IEnumerable<string> Declared(IEnumerable<Decl> members, SignatureKind kind)
    {
        foreach (var m in members)
            switch (m)
            {
                case PublicatorDecl p:
                    foreach (var n in Declared(p.Members, kind)) yield return n;
                    break;
                case EventDecl e when kind == SignatureKind.Event: yield return e.Name; break;
                case BuilderDecl b when kind == SignatureKind.Builder: yield return b.Name; break;
                case ShapeDecl s when kind == SignatureKind.Shape: yield return s.Name; break;
            }
    }

    private static char SigilOf(SignatureKind kind) => kind switch
    {
        SignatureKind.Event => '@', SignatureKind.Builder => '&', _ => '$',
    };
}
