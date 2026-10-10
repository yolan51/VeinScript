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
