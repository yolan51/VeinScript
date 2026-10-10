using Vein.Compiler.Diagnostics;
using Vein.Compiler.Parsing;
using Vein.Compiler.Project;

namespace Vein.Compiler.Ir;

// The tooling's view of one bundle: a Lower that has run its declaration pass and NEVER lowers.
//
// WHY THROUGH LOWER. `?`, the field popup, signature help, hover and `veinc scaffold` all have to say what
// a `bring`/`emit`/`attach` will bind, and they used to work it out from a copy of the rules — EventCatalog's
// own `need` widening, include expansion and variant flattening. The copy drifted, exactly as a copy of
// binding always does: it resolved a kit builder's bare includes in the CONSUMER's scope, so a local `$Box`
// changed a kit's fields in the editor only; it widened aliased needs, which the compiler never does; and
// it picked one of two same-named events where the compiler says VS0216. Asking the compiler's own
// resolution removes the possibility rather than the instances.
//
// Every question goes through the methods `bring`, `emit` and `attach` use — ResolveBringTarget,
// BringParams, ResolveEventOwned, IncludeFields, MarksOfShape — with diagnostics into a throwaway bag.
public sealed partial class Lower
{
    /// One field as the tooling shows it. `Origin` is the `$Shape` it came through: the resolved index key
    /// for a qualified include (`Vein.Math.Values.Vec2`), the bare name for a bare one, null when the field
    /// is declared inline.
    internal sealed record ToolField(string Name, TypeRef? Type, Expr? Default, string? Origin);

    /// What a reference resolves to. `Key` is its index key, null when it is declared in this bundle.
    /// For a builder: `Fields` are the slots a call fills (fixed ones removed), `Root` the base a variant
    /// chain ends at, `Generates` "an identity" or the event it emits.
    internal sealed record ToolSignature(
        string? Key, Decl Decl, IReadOnlyList<ToolField> Fields,
        IReadOnlyList<string> Marks, string? Generates, string? Root,
        IReadOnlyDictionary<string, Expr> Fixed);

    private static readonly IReadOnlyDictionary<string, Expr> NoFixes = new Dictionary<string, Expr>(StringComparer.Ordinal);

    /// A scope for `bundle` — its declarations collected, its needs and aliases recorded — or an empty one
    /// that still resolves qualified references when there is no bundle.
    internal static Lower ToolingScope(BundleDecl? bundle, string? projectDir)
    {
        var scope = new Lower(new DiagnosticBag(), projectDir) { _lowered = true, _loweredName = "(tooling scope)" };
        if (bundle is null) return scope;

        scope._ownerKey = (bundle.Author ?? "local") + "." + bundle.Name;
        scope.CollectDecls(bundle.Members);
        return scope;
    }

    internal BundleIndex ToolingIndex => Index;

    /// The un-aliased needs — the bundles whose shared members a BARE name may mean (RULES 18, 18b).
    internal IReadOnlyList<string> NeededBundles => _needed;

    internal ToolSignature? EventSignature(IReadOnlyList<string> path, string name)
    {
        var (key, decl) = ResolveEventOwned(path, name);
        if (decl is null) return null;

        var slots = new List<Slot>();
        var prms = ExpandMembers(decl.Members, key, slots: slots);
        // origin/source are the runtime's — added at emit, never written by the program.
        var fields = Fields(prms, slots).Where(f => f.Name is not ("origin" or "source")).ToList();
        return new ToolSignature(key, decl, fields, Array.Empty<string>(), null, null, NoFixes);
    }

    internal ToolSignature? BuilderSignature(IReadOnlyList<string> path, string name)
    {
        if (ResolveBringTarget(path, name, default) is not { } t) return null;

        var slots = new List<Slot>();
        var (identity, output, prms) = BringParams(t, slots);
        var fixedArgs = t.Fixed ?? NoFixes;
        var fields = Fields(prms, slots).Where(f => !fixedArgs.ContainsKey(f.Name)).ToList();

        // Its own `mark` lines, then the marks its shapes bring (RULES 15c) — both are applied by `bring`,
        // and the second kind is what makes a builder an identity template without a mark line of its own.
        var marks = t.Decl.Members.OfType<MarkMember>().SelectMany(m => m.Marks).ToList();
        foreach (var si in t.Decl.Members.OfType<ShapeInclude>())
            foreach (var mk in MarksOfShape(si.Shape, si.Path, t.OwnerKey, si.Span))
                if (!marks.Contains(mk, StringComparer.Ordinal)) marks.Add(mk);

        string generates = identity ? "an identity"
                         : output is null ? "@" + name
                         : "@" + ChannelEvent(output.Name);
        return new ToolSignature(t.OutsideKey, t.Decl, fields, marks, generates, t.Root, fixedArgs);
    }

    internal ToolSignature? ShapeSignature(IReadOnlyList<string> path, string name)
    {
        // The resolver an include uses — and `attach $S` reaches a shape the same way a bare include does.
        var (key, fields) = IncludeFields(new ShapeInclude(name, null, null, default) { Path = path }, null);
        if (fields is null) return null;

        Decl? decl = key is not null && Index.Shapes.TryGetValue(key, out var shared) ? shared
                   : _shapeDecls.TryGetValue(name, out var local) ? local
                   : null;
        if (decl is null) return null;

        var marks = MarksOfShape(name, path, null, default);
        var list = fields.Select(f => new ToolField(f.Name, f.Type, f.Default, null)).ToList();
        return new ToolSignature(key, decl, list, marks.ToList(), null, null, NoFixes);
    }

    /// For a BARE name this bundle does not declare: every needed bundle's member of that name. More than
    /// one is the ambiguity the compiler reports as VS0216 — and the tooling must name it, not pick one.
    internal IReadOnlyList<string> Candidates(char sigil, string name)
    {
        switch (sigil)
        {
            case '@' when !_events.ContainsKey(name):   return UsedHits(Index.Events, name).Select(h => h.Key).ToList();
            case '&' when !_builders.ContainsKey(name): return UsedHits(Index.Builders, name).Select(h => h.Key).ToList();
            case '$' when !_shapeFields.ContainsKey(name): return UsedHits(Index.Shapes, name).Select(h => h.Key).ToList();
            default: return Array.Empty<string>();
        }
    }

    /// The event a fragment builder's output channel emits on — `markup` → Html, `code` → Script.
    internal static string ChannelEvent(string field) => field switch
    {
        "code" => "Script", "css" => "Style", "line" => "Print", _ => "Html",
    };

    /// `ExpandMembers` returns parameters and fills `slots` one-for-one; zip them into what tooling shows.
    private static IEnumerable<ToolField> Fields(
        List<(string Name, TypeRef? Type, Expr? Default, string? From)> prms, List<Slot> slots)
    {
        for (int i = 0; i < prms.Count; i++)
        {
            var p = prms[i];
            string? origin = i < slots.Count && slots[i].Qualified ? slots[i].ShapeKey : p.From;
            yield return new ToolField(p.Name, p.Type, p.Default, origin);
        }
    }
}
