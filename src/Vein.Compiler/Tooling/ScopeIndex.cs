using Vein.Compiler.Parsing;
using Vein.Compiler.Project;

namespace Vein.Compiler.Tooling;

/// <summary>
/// Everything a sigil can reach from one file: what this unit declares or uses, plus what the bundles
/// on the search path SHARE.
/// </summary>
/// <remarks>
/// <para>
/// <b>THE TWO HALVES WERE NEVER JOINED.</b> `SymbolIndex.Collect` walks one `CompilationUnit`, so it
/// sees non-shared declarations and merely-used marks — and nothing whatsoever from another bundle.
/// `BundleIndex` holds every SHARED declaration on the search path — and knows nothing about the file
/// being edited. An editor offering `$` had only the first, so a dev who needed ten bundles was shown
/// the shapes of none of them, while `&amp;` (through `EventCatalog`) had been cross-bundle all along.
/// The sigils disagreed about what "in scope" meant.
/// </para>
/// <para>
/// <b>BOTH VISIBILITY RULES FALL OUT, rather than being enforced here.</b> Everything in your own
/// bundle is yours whether shared or not, and `SymbolIndex` reports all of it. Only `shared("…")`
/// crosses a boundary (RULES 17), and `BundleIndex.Collect` already drops the rest with one line —
/// so this cannot offer a name that would not resolve. The `shared` gate is not re-implemented; it is
/// inherited.
/// </para>
/// <para>
/// <b>MARKS ARE MOSTLY UNDECLARED, which is why the local half matters.</b> A mark is a name unless
/// declared (RULES 16), and in `samples/` alone dozens are only ever used — `#Alice`, `#Client1`,
/// `#Control`. A list built from declarations would miss them, and `SymbolIndex` already harvests
/// `mark e #X`, a query's tags, an audience and a shard's carried marks.
/// </para>
/// </remarks>
public static class ScopeIndex
{
    /// <summary>One name a sigil can offer, and everything the editor needs to show and insert it.</summary>
    /// <param name="Name">The bare name — `Position`, without its sigil.</param>
    /// <param name="Sigil">`$`, `#`, `@` or `&amp;`.</param>
    /// <param name="Origin">`Vein.Transform.Spatial` — the path that OWNS it; null when it is this unit's.</param>
    /// <param name="Qualified">
    /// `*Vein.Transform.Spatial.$Position`, or the bare `$Position` for a local one. What to insert
    /// where the language accepts a qualified reference; see <see cref="SourceContext"/> for where it
    /// does not.
    /// </param>
    /// <param name="Needed">
    /// The owning bundle is already `need`ed here, so the BARE name resolves too. False for something
    /// merely discoverable — offering it is the point, but a use site will want a `need` line first.
    /// </param>
    /// <param name="Local">Declared or used in this unit. Always reachable, bare, whatever its visibility.</param>
    /// <param name="Doc">
    /// The `shared("…")` text, which is the sentence the author wrote to explain what this IS.
    ///
    /// It is the reason `shared` takes a string at all rather than being a bare keyword, and it was
    /// being thrown away by everything that read the index — so a list of forty names told you what
    /// existed and nothing about which one you wanted. Null for a name with no doc, which includes
    /// every mark that is only ever used.
    /// </param>
    /// <param name="Fields">
    /// `x: float, y: float, z: float` — what the shape or event actually holds, rendered. Null for a
    /// mark, which holds nothing, and for a name with no declaration to read (a mark that is only used).
    /// </param>
    public sealed record ScopeEntry(
        string Name, string Sigil, string? Origin, string Qualified, bool Needed, bool Local,
        string? Doc = null, string? Fields = null)
    {
        /// <summary>`$Position`.</summary>
        public string SigilName => Sigil + Name;

        /// <summary>What a completion row reads: `$Position   Vein.Transform.Spatial`.</summary>
        public string Label => Origin is null ? SigilName : $"{SigilName}   {Origin}";

        /// <summary>
        /// The tooltip beside the row: what it is, what it holds, and the sentence its author wrote.
        /// </summary>
        /// <remarks>
        /// ALL THREE, because they answer different questions. The kind and name say what you are
        /// looking at; the fields say what you will get and in what order, which is what a `bring` binds
        /// positionally; and the `shared("…")` doc says what it is FOR — the reason that keyword takes
        /// a string instead of being a bare marker. A list of forty names with none of this tells you
        /// what exists and nothing about which one you want.
        /// </remarks>
        public string Describe(string kind)
        {
            var text = new System.Text.StringBuilder(kind).Append(' ').Append(SigilName);
            if (Origin is not null) text.Append("   ").Append(Origin);
            if (Fields is { Length: > 0 }) text.Append('\n').Append(Fields);
            if (Doc is { Length: > 0 }) text.Append('\n').Append(Doc);
            return text.ToString();
        }
    }

    /// <summary>
    /// Everything of one kind reachable from <paramref name="unit"/>, local first.
    /// </summary>
    /// <remarks>
    /// A LOCAL NAME WINS over a cross-bundle one, because that is what the compiler does: `need` only
    /// ever WIDENS what a bare name may mean (RULES 18), so a shape declared here shadows a shared one
    /// of the same name and the editor must not offer the other as if it were reachable.
    ///
    /// `projectDir` null, or an index that cannot be built, degrades to the local half alone — which is
    /// exactly the behaviour this replaces, so a project without a search path is no worse off.
    /// </remarks>
    public static IReadOnlyList<ScopeEntry> For(CompilationUnit unit, SymbolKind kind, string? projectDir)
    {
        string sigil = SigilOf(kind);
        var byName = new Dictionary<string, ScopeEntry>(StringComparer.Ordinal);

        var local = LocalDetail(unit, kind);
        foreach (string name in Locals(unit, kind))
        {
            local.TryGetValue(name, out var d);
            byName[name] = new ScopeEntry(name, sigil, Origin: null, Qualified: sigil + name,
                                          Needed: true, Local: true, Doc: d.Doc, Fields: d.Fields);
        }

        var needs = Needs(unit);
        var index = Index(projectDir);
        foreach (var s in Shared(projectDir, kind))
        {
            if (byName.ContainsKey(s.Name)) continue;   // local wins
            byName[s.Name] = new ScopeEntry(
                s.Name, sigil,
                Origin: string.Join(".", s.PathSegments),
                Qualified: s.QualifiedName,
                Needed: needs.Contains(s.Author + "." + s.Bundle, StringComparer.Ordinal),
                Local: false,
                Doc: s.Doc,
                Fields: SharedFields(index, s));
        }

        // Local, then what a bare name already reaches, then the merely discoverable — and alphabetical
        // inside each, so the list is stable between keystrokes.
        return byName.Values
            .OrderByDescending(e => e.Local)
            .ThenByDescending(e => e.Needed)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static string SigilOf(SymbolKind kind) => kind switch
    {
        SymbolKind.Shape => "$",
        SymbolKind.Mark => "#",
        SymbolKind.Event => "@",
        SymbolKind.Builder => "&",
        _ => "",
    };

    /// <summary>
    /// The `shared("…")` text of this unit's OWN declarations, by name.
    /// </summary>
    /// <remarks>
    /// `SymbolIndex` returns names, because that is all completion needed when it could only see one
    /// file. A local declaration carries a doc as readily as a cross-bundle one — a publicator in the
    /// bundle you are editing is still a publicator — and reading one from the index and not the other
    /// would make the list explain half its rows.
    /// </remarks>
    private static Dictionary<string, (string? Doc, string? Fields)> LocalDetail(
        CompilationUnit unit, SymbolKind kind)
    {
        var found = new Dictionary<string, (string? Doc, string? Fields)>(StringComparer.Ordinal);

        void Walk(IEnumerable<Decl> ds)
        {
            foreach (var d in ds)
            {
                switch (d)
                {
                    case BundleDecl b: Walk(b.Members); continue;
                    case PublicatorDecl p: Walk(p.Members); continue;
                }

                (string Name, string? Fields)? hit = (kind, d) switch
                {
                    (SymbolKind.Shape, ShapeDecl s) => (s.Name, FieldList(s.Members)),
                    (SymbolKind.Mark, MarkDecl m) => (m.Name, null),
                    (SymbolKind.Event, EventDecl e) => (e.Name, FieldList(e.Members)),
                    (SymbolKind.Builder, BuilderDecl bl) => (bl.Name, FieldList(bl.Members)),
                    _ => null,
                };

                if (hit is { } h) found[h.Name] = (d.Doc, h.Fields);
            }
        }

        Walk(unit.Bundles);
        return found;
    }

    /// <summary>The declared fields of a shared shape or event, from the decl the index kept.</summary>
    /// <remarks>
    /// `BundleIndex` holds the real `ShapeDecl`/`EventDecl` as the dictionary VALUE, not merely a name,
    /// which is what makes this a lookup rather than a second parse. Keys are `Author.Bundle[.Pub].Name`
    /// — the same shape `QualifiedSymbol.PathSegments` produces.
    /// </remarks>
    private static string? SharedFields(BundleIndex? index, QualifiedSymbol s)
    {
        if (index is null) return null;
        string key = string.Join(".", s.PathSegments) + "." + s.Name;

        return s.Kind switch
        {
            SymbolKind.Shape => index.Shapes.TryGetValue(key, out var sh) ? FieldList(sh.Members) : null,
            SymbolKind.Event => index.Events.TryGetValue(key, out var ev) ? FieldList(ev.Members) : null,
            SymbolKind.Builder => index.Builders.TryGetValue(key, out var bl) ? FieldList(bl.Members) : null,
            _ => null,   // a mark holds nothing
        };
    }

    /// <summary>`x: float, y: float` — the fields of a body, in declaration order.</summary>
    private static string? FieldList(IEnumerable<Node> members)
    {
        var fields = members.OfType<FieldDecl>()
                            .Select(f => f.Name + ": " + Sig.TypeStr(f.Type))
                            .ToList();
        return fields.Count == 0 ? null : string.Join(", ", fields);
    }

    /// <summary>The index, or null when there is no search path. Cached by `BundleIndex` itself.</summary>
    private static BundleIndex? Index(string? projectDir)
    {
        try { return BundleIndex.For(projectDir); }
        catch (Exception) { return null; }
    }

    /// <summary>What this unit declares or uses. Any visibility — it is all yours in your own bundle.</summary>
    private static IReadOnlyList<string> Locals(CompilationUnit unit, SymbolKind kind)
    {
        var s = SymbolIndex.Collect(unit);
        return kind switch
        {
            SymbolKind.Shape => s.Shapes,
            SymbolKind.Mark => s.Marks,
            SymbolKind.Event => s.Events,
            // `SymbolIndex` has no builder arm, and a local builder is reached by its bare name with no
            // sigil at the `bring`, so the cross-bundle half carries this kind on its own.
            _ => Array.Empty<string>(),
        };
    }

    /// <summary>
    /// The shared declarations of that kind on the search path, filtered by the project's discovery
    /// policy — the same gate `*` completion already respects, so a bundle hidden from discovery stays
    /// hidden here too.
    /// </summary>
    private static IEnumerable<QualifiedSymbol> Shared(string? projectDir, SymbolKind kind)
    {
        BundleIndex index;
        try { index = BundleIndex.For(projectDir); }
        catch (Exception) { yield break; }   // no search path is not an error; it is a project with no bundles

        IEnumerable<QualifiedSymbol> of = index.Symbols.Where(s => s.Kind == kind);

        IEnumerable<QualifiedSymbol> visible;
        try { visible = DiscoveryPolicy.Load(projectDir).Filter(of); }
        catch (Exception) { visible = of; }

        foreach (var s in visible) yield return s;
    }

    /// <summary>
    /// The `Author.Bundle` keys this unit needs. Same walk `EventCatalog` does, and the same key shape
    /// `BundleIndex.Owners` uses, so "already needed" means what the compiler means by it.
    /// </summary>
    private static HashSet<string> Needs(CompilationUnit unit)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        void Walk(IEnumerable<Decl> ds)
        {
            foreach (var d in ds)
                switch (d)
                {
                    case NeedDecl n: keys.Add(n.Key); break;
                    case BundleDecl b: Walk(b.Members); break;
                    case PublicatorDecl p: Walk(p.Members); break;
                }
        }

        Walk(unit.Bundles);
        return keys;
    }
}
