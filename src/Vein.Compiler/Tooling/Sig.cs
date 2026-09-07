using Vein.Compiler.Parsing;

namespace Vein.Compiler.Tooling;

// Shared helper for the unified event/builder signature bodies: builds the shape→fields map and
// expands a member list (fields / vars / `$Shape` includes) into ordered concrete fields. Used by
// the event catalog, the completion indexes, and the printers so expansion logic lives in one place.
public static class Sig
{
    // OriginShape = the `$Shape` a field was pulled in from (null for a plain field/var declared inline).
    public sealed record Field(string Name, string Type, bool Required, string? Default, bool IsVar, string? OriginShape = null);

    public static Dictionary<string, List<FieldDecl>> Shapes(CompilationUnit unit)
    {
        var map = new Dictionary<string, List<FieldDecl>>(StringComparer.Ordinal);
        void Walk(IEnumerable<Decl> ms)
        {
            foreach (var m in ms)
                switch (m)
                {
                    case ShapeDecl s: map[s.Name] = s.Members.OfType<FieldDecl>().ToList(); break;
                    case BundleDecl b: Walk(b.Members); break;
                    case PublicatorDecl p: Walk(p.Members); break;
                }
        }
        Walk(unit.Bundles);
        return map;
    }

    /// Every shape a bundle's identities can actually CARRY: the ones it declares, plus the ones that
    /// reach it through a qualified include on a builder or an event.
    ///
    /// `Shapes` above answers "what does this file declare", which is right for expanding a signature
    /// and wrong for analysing state. A builder including `*Vein.Transform.Spatial.$Position` gives its
    /// identities a `Position` component — under the BARE name, because components unify by bare name
    /// (RULES 15b) — and a `target $Position as t` then binds it and reads `t.Position.x`.
    ///
    /// An analysis working from declarations alone sees no such shape, and the failure is silent and
    /// INVERTS THE ANSWER: ExecutionModel gated its state tracking on this map, so a program built the
    /// documented way — over stdlib shapes — reported zero reads, zero writes and zero conflicts. Zero
    /// conflicts reads as "this program is safe" when it meant "this program was not examined", and
    /// every program that uses the standard library goes through an include, so the wrong answer was
    /// the ordinary case rather than the corner.
    public static Dictionary<string, List<FieldDecl>> ShapesInScope(CompilationUnit unit)
    {
        var map = Shapes(unit);

        void Walk(IEnumerable<Node> ms)
        {
            foreach (var m in ms)
                switch (m)
                {
                    // A local declaration always wins: `Shapes` already put it in, and a bundle that
                    // declares its own `$Position` means that one.
                    case ShapeInclude si when si.Path.Count > 0 && !map.ContainsKey(si.Shape):
                        if (External(si) is { } fields) map[si.Shape] = fields;
                        break;
                    case BundleDecl b: Walk(b.Members); break;
                    case PublicatorDecl p: Walk(p.Members); break;
                    case BuilderDecl bd: Walk(bd.Members); break;
                    case EventDecl ed: Walk(ed.Members); break;
                }
        }

        Walk(unit.Bundles);
        return map;
    }

    /// The fields of a `*Author.Bundle.Publicator.$Shape`, matched on a path suffix like every other
    /// qualified reference.
    private static List<FieldDecl>? External(ShapeInclude si)
    {
        string refKey = string.Join(".", si.Path) + "." + si.Shape;
        foreach (var kv in Project.StdlibIndex.Shapes())
            if (kv.Key == refKey || kv.Key.EndsWith("." + refKey, StringComparison.Ordinal))
                return kv.Value.Members.OfType<FieldDecl>().ToList();
        return null;
    }

    public static List<Field> Expand(IReadOnlyList<Node> members, Dictionary<string, List<FieldDecl>> shapes)
    {
        var list = new List<Field>();
        foreach (var m in members)
        {
            if (m is FieldDecl f)
                list.Add(new Field(f.Name, TypeStr(f.Type), f.Default is null, DefaultText(f.Default), f.IsVar));
            else if (m is ShapeInclude si && Lookup(si, shapes) is { } fs)
            {
                // OriginShape keeps the qualifier, so tooling can show WHERE a reused field came from.
                string origin = si.Path.Count > 0 ? string.Join(".", si.Path) + "." + si.Shape : si.Shape;
                if (si.Field is not null)
                {
                    var one = fs.FirstOrDefault(x => x.Name == si.Field);
                    if (one is not null)
                    {
                        var d = si.Default ?? one.Default;
                        list.Add(new Field(one.Name, TypeStr(one.Type), d is null, DefaultText(d), false, origin));
                    }
                }
                else foreach (var sf in fs)
                    list.Add(new Field(sf.Name, TypeStr(sf.Type), sf.Default is null, DefaultText(sf.Default), false, origin));
            }
        }
        return list;
    }

    /// A bare include resolves in this compilation; a `*Author.Bundle.Publicator.$Shape` one reaches the
    /// stdlib's SHARED shapes (matched on a path suffix, like every other qualified reference).
    private static List<FieldDecl>? Lookup(ShapeInclude si, Dictionary<string, List<FieldDecl>> shapes)
    {
        if (si.Path.Count == 0) return shapes.TryGetValue(si.Shape, out var local) ? local : null;

        string refKey = string.Join(".", si.Path) + "." + si.Shape;
        foreach (var kv in Project.StdlibIndex.Shapes())
            if (kv.Key == refKey || kv.Key.EndsWith("." + refKey, StringComparison.Ordinal))
                return kv.Value.Members.OfType<FieldDecl>().ToList();
        return null;
    }

    public static string TypeStr(TypeRef? t) =>
        t is null ? "infer" : t.Name + (t.Args.Count > 0 ? "<" + string.Join(", ", t.Args.Select(TypeStr)) + ">" : "");

    public static string? DefaultText(Expr? e) => e switch
    {
        null => null,
        LiteralExpr { Kind: LiteralKind.String } l => "\"" + (l.Value as string ?? "") + "\"",
        // `bool.ToString()` is "True"/"False" — C# casing, which is not VeinScript. A scaffold is meant
        // to be pasted and edited, so it has to spell its own literals.
        LiteralExpr { Value: bool b } => b ? "true" : "false",
        LiteralExpr l => l.Value?.ToString() ?? "null",
        NameExpr n => n.Name,
        _ => "…"
    };
}
