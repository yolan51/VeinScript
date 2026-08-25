using Vein.Compiler.Parsing;

namespace Vein.Compiler.Tooling;

// Shared helper for the unified event/builder signature bodies: builds the shape→fields map and
// expands a member list (fields / vars / `$Shape` includes) into ordered concrete fields. Used by
// the event catalog, the completion indexes, and the printers so expansion logic lives in one place.
public static class Sig
{
    public sealed record Field(string Name, string Type, bool Required, string? Default, bool IsVar);

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

    public static List<Field> Expand(IReadOnlyList<Node> members, Dictionary<string, List<FieldDecl>> shapes)
    {
        var list = new List<Field>();
        foreach (var m in members)
        {
            if (m is FieldDecl f)
                list.Add(new Field(f.Name, TypeStr(f.Type), f.Default is null, DefaultText(f.Default), f.IsVar));
            else if (m is ShapeInclude si && shapes.TryGetValue(si.Shape, out var fs))
            {
                if (si.Field is not null)
                {
                    var one = fs.FirstOrDefault(x => x.Name == si.Field);
                    if (one is not null)
                    {
                        var d = si.Default ?? one.Default;
                        list.Add(new Field(one.Name, TypeStr(one.Type), d is null, DefaultText(d), false));
                    }
                }
                else foreach (var sf in fs)
                    list.Add(new Field(sf.Name, TypeStr(sf.Type), sf.Default is null, DefaultText(sf.Default), false));
            }
        }
        return list;
    }

    public static string TypeStr(TypeRef? t) =>
        t is null ? "infer" : t.Name + (t.Args.Count > 0 ? "<" + string.Join(", ", t.Args.Select(TypeStr)) + ">" : "");

    public static string? DefaultText(Expr? e) => e switch
    {
        null => null,
        LiteralExpr { Kind: LiteralKind.String } l => "\"" + (l.Value as string ?? "") + "\"",
        LiteralExpr l => l.Value?.ToString() ?? "null",
        NameExpr n => n.Name,
        _ => "…"
    };
}
