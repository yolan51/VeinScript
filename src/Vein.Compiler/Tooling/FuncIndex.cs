using Vein.Compiler.Parsing;
using Vein.Compiler.Project;

namespace Vein.Compiler.Tooling;

// Finding a `fn`/`SF` by bare name, and rendering its signature.
//
// This lives here rather than in the Workbench because the Workbench is where duplicated tooling has
// hidden before: its `?` expansion carried a private copy of EventCatalog's builder walk that drifted
// until a builder reached through `use` expanded to nothing. Logic the GUI cannot be tested through
// belongs on this side of the seam.
//
// It matters for composed behaviour. Writing a handler as `addNumber(domId(out), 1)` instead of a quoted
// blob of JavaScript is only an improvement if the signatures are visible: `*Vein.Web.Js.` completes the
// names, and this is what says what each one takes.
public static class FuncIndex
{
    /// A function by bare name: this unit first, then the shared functions of the bundles it `use`s —
    /// the same order Lower resolves in, so what is shown is what a call would actually reach.
    public static (FuncDecl? Fn, string? Owner) Find(CompilationUnit? unit, string name, string? projectDir)
    {
        if (unit is null || string.IsNullOrEmpty(name)) return (null, null);

        FuncDecl? Search(IEnumerable<Decl> decls)
        {
            foreach (var d in decls)
                switch (d)
                {
                    case FuncDecl f when f.Name == name: return f;
                    case BundleDecl b when Search(b.Members) is { } r: return r;
                    case PublicatorDecl p when Search(p.Members) is { } r: return r;
                }
            return null;
        }
        if (Search(unit.Bundles) is { } local) return (local, null);

        var uses = new List<string>();
        void Uses(IEnumerable<Decl> ds)
        {
            foreach (var d in ds)
                switch (d)
                {
                    case NeedDecl n when !uses.Contains(n.Bundle, StringComparer.Ordinal): uses.Add(n.Bundle); break;
                    case BundleDecl b: Uses(b.Members); break;
                    case PublicatorDecl p: Uses(p.Members); break;
                }
        }
        Uses(unit.Bundles);
        if (uses.Count == 0) return (null, null);

        // Sorted, so two bundles exporting the same name resolve the same way every run rather than by
        // dictionary order — the mistake VS0220 was caught making.
        foreach (var kv in BundleIndex.For(projectDir).Functions.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var parts = kv.Key.Split('.');
            if (parts.Length >= 3 && parts[^1] == name && uses.Contains(parts[1], StringComparer.Ordinal))
                return (kv.Value, kv.Key);
        }
        return (null, null);
    }

    /// `fn addNumber(id: string, delta: int) -> string`, with the owning path when it came from a
    /// `use`d bundle. `SF` and `fn` are shown as written, because the difference is the point: an SF
    /// is behaviour, a fn is computation.
    public static string Signature(FuncDecl f, string? owner) =>
        (f.IsPure ? "SF " : "fn ") + f.Name +
        "(" + string.Join(", ", f.Params.Select(p => p.Name + ": " + (p.Type?.Name ?? "?"))) + ")" +
        (f.Return is null ? "" : " -> " + f.Return.Name) +
        (owner is null ? "" : "      *" + owner);
}
