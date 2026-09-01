using Vein.Compiler.Diagnostics;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;
using Vein.Compiler.Project;

namespace Vein.Compiler.Ir;

/// Lowers the AST (docs/LANGUAGE.md) into the HIR (docs/IR-SPEC.md), folding in the IOP desugaring
/// (docs/DIALECTS.md §1): shape→component type, chance→if, mark/emit/destroy→runtime calls,
/// each tick/settled→shard methods that iterate the query, folds→field metadata.
///
/// This is the "source → IR" transform. Name/type *resolution* is a later pass; here we produce a
/// structurally complete IR.
public sealed class Lower
{
    private readonly DiagnosticBag _diag;
    private readonly SortedSet<string> _tags = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BuilderDecl> _builders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<FieldDecl>> _shapeFields = new(StringComparer.Ordinal);

    // Shared functions pulled in from another bundle by a qualified call, keyed by their mangled name.
    // Appended to the module so the interpreter can dispatch them like any local function.
    private readonly Dictionary<string, IrFunction> _imported = new(StringComparer.Ordinal);

    // The `target … as <bind>` names currently in scope. A reference to one of these is the identity
    // (`IrSelfRef`), not a local — see the NameExpr cases in LowerExpr. A stack, because targets nest.
    private readonly List<string> _targetBinds = new();

    /// `projectDir` anchors cross-bundle resolution: the standard library plus this project's installed
    /// `bundles/`. Null falls back to the ambient BundleSearch scope, then the CWD — which finds the
    /// stdlib but never a user project, so pass it wherever the source file's location is known.
    public Lower(DiagnosticBag diagnostics, string? projectDir = null)
    {
        _diag = diagnostics;
        _projectDir = projectDir;
    }

    private readonly string? _projectDir;

    private BundleIndex Index => _index ??= BundleIndex.For(_projectDir);
    private BundleIndex? _index;

    private bool IsTargetBind(string name) => _targetBinds.Contains(name, StringComparer.Ordinal);

    private TargetScope BindTarget(string bind) => new(_targetBinds, bind);

    /// Pops the binding when the target body is done lowering.
    private readonly struct TargetScope : IDisposable
    {
        private readonly List<string> _binds;
        public TargetScope(List<string> binds, string bind) { _binds = binds; binds.Add(bind); }
        public void Dispose() => _binds.RemoveAt(_binds.Count - 1);
    }

    private static readonly string[] OutputFields = { "markup", "code", "css", "line" };

    /// Bundles this one says `use` on, in declaration order. A bare name that resolves nowhere locally
    /// is looked for in these, which is the whole of what `use` does.
    private readonly List<string> _used = new();

    /// Locally declared `fn`/`SF` names. Needed only to tell "this bare call is local" from "this bare
    /// call resolves nowhere" — nothing tracked that before, because nothing needed to.
    private readonly HashSet<string> _localFuncs = new(StringComparer.Ordinal);

    /// Marks this bundle DECLARES (`mark #Enemy`), as opposed to `_tags`, which is every mark it USES.
    ///
    /// Empty means the bundle never opted in, and nothing is checked — the additive rule `use` was built
    /// on. Non-empty means the author asked for the names to be checked, so a used-but-undeclared mark is
    /// reported (VS0218).
    private readonly SortedSet<string> _declaredMarks = new(StringComparer.Ordinal);

    /// Where each mark was FIRST used, so VS0218 can point at the source rather than at the bundle.
    private readonly Dictionary<string, SourceSpan> _markUses = new(StringComparer.Ordinal);

    /// Record a mark use: it becomes a Tag type, and it is a candidate for the declared-mark check.
    /// Every place a `#Mark` can appear routes through here, which is what keeps the two in step.
    private void UseMark(string name, SourceSpan span)
    {
        _tags.Add(name);
        if (!_markUses.ContainsKey(name)) _markUses[name] = span;
    }

    public IrModule LowerBundle(BundleDecl bundle)
    {
        var types = new List<IrType>();
        var funcs = new List<IrFunction>();
        var shards = new List<IrShard>();

        // First pass: collect builders (for `bring`), shape fields (for `$Shape` include expansion), the
        // `use` list and local function names (both for bare-name resolution) — including inside
        // publicators. All of it has to exist before any body is lowered, because a bare reference may
        // appear above the declaration it resolves to.
        void Collect(IEnumerable<Decl> ms)
        {
            foreach (var m in ms)
                switch (m)
                {
                    case BuilderDecl bd: _builders[bd.Name] = bd; break;
                    case ShapeDecl s: _shapeFields[s.Name] = s.Members.OfType<FieldDecl>().ToList(); break;
                    case FuncDecl fd: _localFuncs.Add(fd.Name); break;
                    case MarkDecl md: _declaredMarks.Add(md.Name); break;
                    case UseDecl ud: if (!_used.Contains(ud.Name, StringComparer.Ordinal)) _used.Add(ud.Name); break;
                    case PublicatorDecl pub: Collect(pub.Members); break;
                }
        }
        Collect(bundle.Members);

        // `publicator` is flattened here (its members are already Exported) — HIR is identical whether
        // or not the AST retained the grouping.
        void LowerMember(Decl m)
        {
            switch (m)
            {
                case PublicatorDecl pub: foreach (var sub in pub.Members) LowerMember(sub); break;
                case BuilderDecl: break;   // templates, not emitted to IR directly
                case ShapeDecl s: types.AddRange(LowerShape(s)); break;
                case TypeDecl t: types.Add(LowerType(t)); break;
                case EventDecl e: types.Add(LowerEvent(e)); break;
                case FuncDecl f: funcs.Add(LowerFunc(f)); break;
                case ShardDecl sh: shards.Add(LowerShardLike(sh.Name, sh.Members, "system", sh.Doc, sh.CarriedShapes, sh.CarriedMarks)); break;
                case ViewDecl vw: shards.Add(LowerView(vw)); break;
                case BridgeDecl br: shards.Add(LowerShardLike(br.Name, br.Members, "bridge", br.Doc, br.CarriedShapes, br.CarriedMarks)); break;
                // Consumed by the Collect pass above, which builds the bare-name fallback list; there is
                // nothing to lower, because `use` adds no IR — it only widens what a bare name may mean.
                case UseDecl: break;
                case VarDecl: break;                 // module-level state: not modeled yet
                case StartDecl: break;               // captured separately below (module boot)
                default: break;
            }
        }
        foreach (var m in bundle.Members) LowerMember(m);

        // Boot event: a bundle has AT MOST one entry point — its `start @E { … }`. Zero is fine (a
        // purely reactive bundle that only `hear`s events others emit). Null → @Request fallback.
        var startDecls = bundle.Members.OfType<StartDecl>().ToList();
        if (startDecls.Count > 1)
            // VS0219, not VS0210: that code already means "unknown shape in an include" (below, and in
            // LANGUAGE.md), and one code cannot identify two unrelated conditions.
            _diag.Error("VS0219", $"bundle '{bundle.Name}' has {startDecls.Count} `start` entries; a bundle has at most one entry point.", startDecls[1].Span);
        var startDecl = startDecls.FirstOrDefault();
        var start = startDecl is null ? null : new IrStart(
            startDecl.Event,
            startDecl.Fields.Select(f => (f.Name, LowerExpr(f.Value))).ToList(),
            startDecl.FillRest);

        // A DECLARED mark exists whether or not this bundle uses it: the tag type is emitted so the engine
        // can query `Marks.X` for identities another bundle marked, and so `veinc symbols` has something
        // to list. Declaring is the assertion that the name is real; using it is a separate question.
        foreach (var declared in _declaredMarks) _tags.Add(declared);

        CheckDeclaredMarks();
        CheckShapeConvergence(bundle);

        // Marks discovered while lowering become Tag types, deduped BY KIND AS WELL AS NAME.
        //
        // `$Enemy` and `#Enemy` are different things — different keyword, different sigil — and a program
        // may use both. Deduping on the name alone let a shape swallow the mark: the Tag was never added,
        // so anything reading the IR for marks simply did not see one. That was invisible while the
        // backend emitted marks as strings and ignored Tag types entirely; it stops being invisible the
        // moment a mark has to become a real type.
        foreach (var tag in _tags)
            if (!types.Any(t => t.Name == tag && t.Kind == IrTypeKind.Tag))
                types.Add(new IrType(tag, IrTypeKind.Tag,
                    Array.Empty<IrField>(), Array.Empty<IrEnumCase>(), null,
                    new[] { IrAttr.Of("tag") }));

        CheckConsoleAddresses(bundle);
        CheckDuplicateBundles(bundle);

        // Cross-bundle functions reached by a qualified call, resolved while lowering the bodies above.
        funcs.AddRange(_imported.Values.Where(f => f is not null));

        return new IrModule(bundle.Name, types, funcs, shards) { Start = start };
    }

    /// The same `Author.Bundle` in two search roots — e.g. an installed `bundles/` copy alongside the
    /// stdlib's. `*Author.Bundle` carries no version, so a qualified reference silently resolves to
    /// whichever root wins and you get "why am I getting the old version of this function". An ERROR, not a
    /// warning: there is no spelling that disambiguates them, so the duplicate must go.
    private void CheckDuplicateBundles(BundleDecl bundle)
    {
        foreach (var (name, first, second) in Index.Duplicates)
            _diag.Error("VS0310",
                $"bundle '{name}' is declared in two places on the search path — "
                + $"'{first}' and '{second}'. A `*` reference carries no version and cannot choose between them.",
                bundle.Span);
    }

    /// A console address that no `@Console { name: … }` spawns routes to a pipe nobody listens on, and the
    /// runtime drops it silently (ConsoleBus.Send's `false` is discarded). Catch it here instead.
    /// A WARNING, not an error: a console may legitimately be spawned by another bundle or at runtime, and
    /// this lowers one bundle at a time so a cross-bundle spawn is invisible to it.
    /// A bundle that DECLARES marks has asked for its mark names to be checked; one that declares none is
    /// untouched. That is the same additive rule `use` was built on — a new check must not change what an
    /// existing program means, and every sample in the repo uses marks without declaring any.
    ///
    /// A warning, not an error, and it names what IS known — the shape VS0212 already uses for console
    /// addresses, which is the narrower version of this same check.
    /// A locally declared shape whose name is already a SHARED shape elsewhere, with different fields.
    ///
    /// Components unify by BARE NAME. `attach $Position` lowers to the name alone, and `AppLinker.Merge`
    /// folds every linked bundle's types into one table keyed by it — deliberately, since that is how a
    /// capability bundle sees the principal's data. Two bundles that both say `$Position` therefore share
    /// one component whether or not they agree on what it holds.
    ///
    /// VS0332 already reports that, but only at app link, and only between bundles that actually meet.
    /// The stdlib never triggers it: a shape is not linked, it is a declaration you copy. And copy is the
    /// operative word — a `shape` body takes fields, not `$Shape` includes (only builders and events can
    /// include one), so an author reusing `$Position` retypes its fields by hand. That is precisely where
    /// a field goes missing, and nothing said so until the two halves met in some app months later.
    ///
    /// Silent when the fields agree: matching by hand is the intended way to reuse a shape, not a
    /// mistake. Only DIVERGENCE is worth a word.
    private void CheckShapeConvergence(BundleDecl bundle)
    {
        // A bundle sitting inside an indexed root finds ITSELF. Skip its own qualified prefix, or every
        // stdlib file would report each of its own shapes.
        string ownPrefix = (bundle.Author ?? "local") + "." + bundle.Name + ".";

        void Walk(IEnumerable<Decl> members)
        {
            foreach (var m in members)
            {
                if (m is PublicatorDecl p) { Walk(p.Members); continue; }
                if (m is not ShapeDecl s) continue;

                var mine = s.Members.OfType<FieldDecl>().ToList();

                // Sorted, and every divergent declaration named. Reporting only the first hit would pick
                // one by dictionary order, so the same source could report a different bundle run to run.
                var clashes = Index.Shapes
                    .Where(kv => !kv.Key.StartsWith(ownPrefix, StringComparison.Ordinal)
                              && kv.Key.EndsWith("." + s.Name, StringComparison.Ordinal)
                              && !SameFields(mine, kv.Value.Members.OfType<FieldDecl>().ToList()))
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .ToList();
                if (clashes.Count == 0) continue;

                string others = string.Join(" and ", clashes.Select(
                    kv => $"*{kv.Key} {Fields(kv.Value.Members.OfType<FieldDecl>().ToList())}"));

                _diag.Warning("VS0220",
                    $"'${s.Name}' is {Fields(mine)}, but is also declared as {others}. Components unify " +
                    $"by name, so linking both into one app makes them one component with two meanings " +
                    $"(VS0332). Match the fields, or rename.",
                    s.Span);
            }
        }
        Walk(bundle.Members);

        static bool SameFields(List<FieldDecl> a, List<FieldDecl> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (!string.Equals(a[i].Name, b[i].Name, StringComparison.Ordinal) ||
                    !string.Equals(a[i].Type?.Name, b[i].Type?.Name, StringComparison.Ordinal))
                    return false;
            return true;
        }

        static string Fields(List<FieldDecl> f) =>
            f.Count == 0 ? "{ }" : "{ " + string.Join(", ", f.Select(x => x.Name + ": " + (x.Type?.Name ?? "infer"))) + " }";
    }

    /// A mark shared by a `use`d bundle counts as declared. The gate above stays on THIS bundle's own
    /// declarations — widening it would switch checking on for a file that never opted in, merely because
    /// something it uses declares marks. Only what counts as *known* widens.
    private void CheckDeclaredMarks()
    {
        if (_declaredMarks.Count == 0) return;

        string known = string.Join(" ", _declaredMarks.Select(m => "#" + m));
        foreach (var (name, span) in _markUses)
            if (!_declaredMarks.Contains(name) && ResolveUsed(Index.Marks, "#", name, span) is null)
                _diag.Warning("VS0218",
                    $"Mark #{name} is not declared in this bundle. known: {known}", span);
    }

    private void CheckConsoleAddresses(BundleDecl bundle)
    {
        var graph = Tooling.ConsoleGraph.Analyze(bundle);
        var unresolved = graph.Unresolved.ToList();
        if (unresolved.Count == 0) return;

        string known = string.Join(" ", graph.Known.Select(a => "#" + a));
        foreach (var a in unresolved)
            _diag.Warning("VS0212",
                $"Console address #{a.Target} is never spawned (no `@Console {{ name: #{a.Target} }}` in this bundle). known: {known}",
                a.Span);
    }

    // ---- data -----------------------------------------------------------

    private IEnumerable<IrType> LowerShape(ShapeDecl s)
    {
        var fields = new List<IrField>();
        var extraEnums = new List<IrType>();
        foreach (var member in s.Members)
        {
            if (member is FieldDecl f)
                fields.Add(new IrField(f.Name, Ty(f.Type), ParseFold(f.Fold, f.Span), LowerDefault(f.Default)));
            else if (member is EnumDecl en)
                extraEnums.Add(LowerEnum(en, scope: s.Name));
        }
        yield return new IrType(s.Name, IrTypeKind.Component, fields, Array.Empty<IrEnumCase>(),
            s.Doc, new[] { IrAttr.Of("component") });
        foreach (var e in extraEnums) yield return e;
    }

    private IrType LowerType(TypeDecl t) => new(
        t.Name, IrTypeKind.Struct,
        t.Fields.Select(f => new IrField(f.Name, Ty(f.Type), ParseFold(f.Fold, f.Span), LowerDefault(f.Default))).ToList(),
        Array.Empty<IrEnumCase>(), t.Doc, Array.Empty<IrAttr>());

    private IrExpr? LowerDefault(Expr? e) => e is null ? null : LowerExpr(e);
    private IrTypeRef Ty(TypeRef? t) => t is null ? IrTypeRef.Of("infer") : LowerTypeRef(t);

    /// Expand an event/builder body to ordered (name, type, default) fields — `$Shape` includes pull
    /// in the shape's fields; `$Shape.field` pulls one.
    ///
    /// `ownerKey` names the bundle this body was IMPORTED from, and is null for a body declared here.
    /// It matters because a bare include means "a shape beside me", and for an imported builder that is
    /// its bundle's shapes rather than the consumer's.
    private List<(string Name, TypeRef? Type, Expr? Default)> ExpandMembers(
        IEnumerable<Node> members, string? ownerKey = null)
    {
        var list = new List<(string, TypeRef?, Expr?)>();
        foreach (var m in members)
        {
            if (m is FieldDecl f) list.Add((f.Name, f.Type, f.Default));
            else if (m is ShapeInclude si)
            {
                // A qualified include reaches another bundle's SHARED shapes. A bare one resolves beside
                // its own declaration: the owner's bundle when this body was imported, otherwise local
                // first and then whatever this bundle `use`s.
                var fs = si.Path.Count > 0 ? ResolveExternalShape(si.Path, si.Shape)
                       : ownerKey is not null ? ResolveOwnedShape(ownerKey, si.Shape)
                       : _shapeFields.TryGetValue(si.Shape, out var local) ? local
                       : ResolveUsed(Index.Shapes, "$", si.Shape, si.Span)?.Value.Members.OfType<FieldDecl>().ToList();

                if (fs is not null)
                {
                    if (si.Field is not null)
                    {
                        var one = fs.FirstOrDefault(x => x.Name == si.Field);
                        if (one is not null) list.Add((one.Name, one.Type, si.Default ?? one.Default));
                        else _diag.Warning("VS0211", $"Shape '{RefText(si)}' has no field '{si.Field}'.", si.Span);
                    }
                    else foreach (var sf in fs) list.Add((sf.Name, sf.Type, sf.Default));
                }
                else _diag.Warning("VS0210", $"Unknown shape '{RefText(si)}' in include.", si.Span);
            }
        }
        return list;
    }

    /// Resolve a BARE name against the bundles this one `use`s — the whole of what `use` does.
    ///
    /// Called only after every local lookup has missed, so a local declaration always wins and no
    /// existing program can change meaning by this being added. `use` names a bundle, not a publicator,
    /// so the publicator segment is skipped: index keys are `Author.Bundle[.Publicator].Name`, and a
    /// match needs the bundle segment and the member to line up. That is the same "qualify only as far
    /// as you need" rule the `*` matchers already use.
    ///
    /// Ambiguity is reported rather than guessed at. It can only arise in code that says `use`, so the
    /// warning cannot reach a program that compiles today.
    /// Returns the index KEY as well as the value: a `fn`/`SF` is not used directly but re-resolved by
    /// path through ImportExternalFunction, which needs the key to build one.
    private (string Key, T Value)? ResolveUsed<T>(
        IReadOnlyDictionary<string, T> index, string sigil, string name, SourceSpan span)
        where T : class
    {
        if (_used.Count == 0) return null;

        var hits = new List<(string Key, T Value)>();
        foreach (var kv in index)
        {
            var parts = kv.Key.Split('.');
            if (parts.Length < 3 || !string.Equals(parts[^1], name, StringComparison.Ordinal)) continue;
            if (_used.Contains(parts[1], StringComparer.Ordinal)) hits.Add((kv.Key, kv.Value));
        }

        if (hits.Count == 0) return null;
        if (hits.Count == 1) return hits[0];

        _diag.Warning("VS0216",
            $"'{sigil}{name}' is ambiguous across the bundles in scope — {string.Join(" and ", hits.Select(h => "*" + h.Key))}. "
            + "Qualify the reference to choose one.", span);
        return null;
    }

    /// A qualified include's target: the stdlib's SHARED shapes, matched on a path suffix so you qualify
    /// only as far as you need to be unique — the same rule as `bring *Author.Bundle.&Builder(…)`.
    private List<FieldDecl>? ResolveExternalShape(IReadOnlyList<string> path, string name)
    {
        string refKey = string.Join(".", path) + "." + name;
        foreach (var kv in Index.Shapes)
            if (kv.Key == refKey || kv.Key.EndsWith("." + refKey, StringComparison.Ordinal))
                return kv.Value.Members.OfType<FieldDecl>().ToList();
        return null;
    }

    /// Resolve a shared cross-bundle `fn`/`SF` and lower it into THIS module under a collision-proof name,
    /// returning that name (null when it doesn't resolve). Matched on a path suffix like every other
    /// qualified reference. Recursive: an imported function may itself call another.
    private string? ImportExternalFunction(IReadOnlyList<string> path, string name, SourceSpan span)
    {
        string refKey = string.Join(".", path) + "." + name;
        var hit = Index.Functions
            .FirstOrDefault(kv => kv.Key == refKey || kv.Key.EndsWith("." + refKey, StringComparison.Ordinal));
        if (hit.Value is null)
        {
            _diag.Warning("VS0213", $"Unknown function '*{refKey}' — the call will do nothing.", span);
            return null;
        }

        string mangled = hit.Key.Replace('.', '_');
        if (_imported.ContainsKey(mangled)) return mangled;

        _imported[mangled] = null!;                       // reserve first: the body may recurse into itself
        var lowered = LowerFunc(hit.Value) with { Name = mangled };
        _imported[mangled] = lowered;
        return mangled;
    }

    private static string RefText(ShapeInclude si) =>
        si.Path.Count > 0 ? "*" + string.Join(".", si.Path) + ".$" + si.Shape : "$" + si.Shape;

    private IrType LowerEvent(EventDecl e)
    {
        var fields = ExpandMembers(e.Members)
            .Select(m => new IrField(m.Name, Ty(m.Type), null, LowerDefault(m.Default))).ToList();
        // Every event is auto-tagged with its emitter's identity on emit (origin/source).
        fields.Add(new IrField("origin", IrTypeRef.Of("Entity"), null));
        fields.Add(new IrField("source", IrTypeRef.Of("Entity"), null));
        return new IrType(e.Name, IrTypeKind.Message, fields, Array.Empty<IrEnumCase>(), e.Doc,
            new[] { IrAttr.Of("message"), IrAttr.Of("origin", "auto") });
    }

    private IrType LowerEnum(EnumDecl e, string? scope)
    {
        string name = scope is null ? e.Name : $"{scope}.{e.Name}";
        var cases = e.Cases.Select((c, i) => new IrEnumCase(c, i)).ToList();
        return new IrType(name, IrTypeKind.Enum, Array.Empty<IrField>(), cases, null, Array.Empty<IrAttr>());
    }

    private FoldReducer? ParseFold(string? fold, SourceSpan span)
    {
        if (fold is null) return null;
        return fold switch
        {
            "sum" => FoldReducer.Sum, "min" => FoldReducer.Min, "max" => FoldReducer.Max,
            "replace" => FoldReducer.Replace, "first" => FoldReducer.First,
            "all" => FoldReducer.All, "any" => FoldReducer.Any,
            _ => Unknown()
        };
        FoldReducer? Unknown()
        {
            _diag.Error("VS0200", $"Unknown fold reducer '{fold}'. Use sum/min/max/replace/first/all/any.", span);
            return FoldReducer.Replace;
        }
    }

    private IrTypeRef LowerTypeRef(TypeRef t)
        => new(t.Name, t.Args.Select(LowerTypeRef).ToList(), t.Nullable);

    // ---- functions ------------------------------------------------------

    private IrFunction LowerFunc(FuncDecl f) => new(
        f.Name,
        f.Params.Select(p => new IrParam(p.Name, LowerTypeRef(p.Type))).ToList(),
        f.Return is null ? IrTypeRef.Of("void") : LowerTypeRef(f.Return),
        LowerBlock(f.Body), f.IsPure, f.Doc,
        f.IsPure ? new[] { IrAttr.Of("sf") } : Array.Empty<IrAttr>());   // SF: emit-only, no return

    // ---- shards ---------------------------------------------------------

    /// Shared lowering for shard-like First-Class objects (shard/bridge). `kind` becomes the base
    /// attribute (@system / @bridge). `carriedShapes`/`carriedMarks` become @carries metadata that an
    /// `audience` barrier matches against.
    private IrShard LowerShardLike(string name, IReadOnlyList<Node> members, string kind, string? doc,
        IReadOnlyList<string>? carriedShapes = null, IReadOnlyList<string>? carriedMarks = null)
    {
        // A shard is a set of scheduled behaviour blocks; each `target` query nests inside a schedule.
        var state = new List<IrField>();
        var methods = new List<IrFunction>();
        var attrs = new List<IrAttr> { IrAttr.Of(kind) };
        // A mark a First-Class object CARRIES is a use — `shard Combat $Session #Trusted` names #Trusted.
        foreach (var cm in carriedMarks ?? (IReadOnlyList<string>)Array.Empty<string>()) UseMark(cm, default);
        if ((carriedShapes?.Count ?? 0) > 0 || (carriedMarks?.Count ?? 0) > 0)
            attrs.Add(IrAttr.Of("carries",
                carriedShapes ?? (IReadOnlyList<string>)Array.Empty<string>(),
                carriedMarks ?? (IReadOnlyList<string>)Array.Empty<string>()));

        foreach (var m in members)
        {
            switch (m)
            {
                case ScheduleBlock sb: methods.Add(LowerSchedule(sb)); break;
                case HearBlock hb: methods.Add(LowerHear(hb)); break;
                case FuncDecl f: methods.Add(LowerFunc(f)); break;
                case VarDecl v: state.Add(new IrField(v.Name, v.Type is null ? IrTypeRef.Of("infer") : LowerTypeRef(v.Type), null)); break;
            }
        }

        return new IrShard(name, state, null, methods, doc, attrs);
    }

    private IrShard LowerView(ViewDecl vw)
    {
        var state = new List<IrField>();
        var methods = new List<IrFunction>();
        foreach (var m in vw.Members)
        {
            switch (m)
            {
                case HearBlock hb: methods.Add(LowerHear(hb)); break;
                case FuncDecl f: methods.Add(LowerFunc(f)); break;
                case VarDecl v: state.Add(new IrField(v.Name, v.Type is null ? IrTypeRef.Of("infer") : LowerTypeRef(v.Type), null)); break;
            }
        }
        var attrs = new List<IrAttr> { IrAttr.Of("view") };
        if (vw.CarriedShapes.Count > 0 || vw.CarriedMarks.Count > 0)
            attrs.Add(IrAttr.Of("carries", vw.CarriedShapes, vw.CarriedMarks));
        return new IrShard(vw.Name, state, null, methods, vw.Doc, attrs);
    }

    /// A schedule block becomes a shard method tagged with a `schedule` attr (once/tick/frame/every/
    /// settled; the interval seconds for `every`). Any `target` query in the body lowers to a target
    /// `IrLoop` (see the `QueryStmt` case in LowerStmt).
    private IrFunction LowerSchedule(ScheduleBlock sb)
    {
        string name = sb.Kind switch
        {
            ScheduleKind.Tick => "tick",
            ScheduleKind.Frame => "frame",
            ScheduleKind.Once => "once",
            ScheduleKind.Every => "every",
            _ => "settled"
        };
        var attr = sb.Kind == ScheduleKind.Every
            ? IrAttr.Of("schedule", name, sb.IntervalSeconds ?? 0.0)
            : IrAttr.Of("schedule", name);
        return new IrFunction(name, Array.Empty<IrParam>(), IrTypeRef.Of("void"), LowerBlock(sb.Body),
            false, null, new[] { attr });
    }

    private IrFunction LowerHear(HearBlock hb)
    {
        var attrs = new List<IrAttr> { IrAttr.Of("hear", hb.Event) };
        // An `audience #Mark` barrier names a mark too, and it is where a typo costs most: a misspelt
        // audience admits nobody, which reads exactly like a barrier doing its job.
        foreach (var am in hb.AudienceMarks) UseMark(am, hb.Span);
        if (hb.AudienceShapes.Count > 0 || hb.AudienceMarks.Count > 0)
            attrs.Add(IrAttr.Of("audience", hb.AudienceShapes, hb.AudienceMarks));
        return new IrFunction(
            $"hear_{hb.Event}",
            new[] { new IrParam(hb.Bind, IrTypeRef.Of(hb.Event)) },
            IrTypeRef.Of("void"), LowerBlock(hb.Body), false, null, attrs);
    }

    // ---- statements -----------------------------------------------------

    private IrBlock LowerBlock(Block b) => new(b.Statements.Select(LowerStmt).ToList());

    private IrStmt LowerStmt(Stmt s)
    {
        switch (s)
        {
            case Block b: return LowerBlock(b);
            case LocalVarStmt lv:
                return new IrLet(lv.Decl.Name, lv.Decl.Type is null ? null : LowerTypeRef(lv.Decl.Type),
                    lv.Decl.Init is null ? null : LowerExpr(lv.Decl.Init), lv.Decl.Mutable);
            case IfStmt i:
            {
                IrBlock? els = i.Else switch
                {
                    Block eb => LowerBlock(eb),
                    IfStmt ei => new IrBlock(new IrStmt[] { LowerStmt(ei) }),
                    _ => null
                };
                return new IrIf(LowerExpr(i.Cond), LowerBlock(i.Then), els);
            }
            case WhileStmt w:
                return new IrLoop(IrLoopKind.While, LowerExpr(w.Cond), null, null, null, null, LowerBlock(w.Body));
            case TargetStmt t:
            {
                var src = LowerExpr(t.Source);          // evaluated OUTSIDE the binding
                using var _ = BindTarget(t.Bind);
                return new IrLoop(IrLoopKind.Target, null, t.Bind, src, null, null, LowerBlock(t.Body));
            }
            case QueryStmt q:
            {
                // A mark a query MATCHES on is a use like any other. Recording it was not merely tidy
                // once marks became types: `target $H #Ghost` with nothing ever marking #Ghost emitted a
                // reference to `Marks.Ghost` and no `Marks` class to hold it, so the generated C# did not
                // compile. Querying a mark nothing sets is legitimate — the query is simply always empty.
                foreach (var tag in q.Tags) UseMark(tag, q.Span);
                using var _ = BindTarget(q.Bind);
                return new IrLoop(IrLoopKind.Target, null, q.Bind, null, new IrQuery(q.Components, q.Tags, q.Bind), null, LowerBlock(q.Body));
            }
            case RepeatStmt r:
                return new IrLoop(IrLoopKind.Repeat, null, r.Var, null, null, LowerExpr(r.Count), LowerBlock(r.Body));
            case MatchStmt m:
                return new IrMatch(LowerExpr(m.Subject),
                    m.Arms.Select(a => { if (a.IsMark) UseMark(a.CaseName, a.Span); return new IrMatchArm(a.CaseName, LowerBlock(a.Body)); }).ToList(),
                    m.Else is null ? null : LowerBlock(m.Else));
            case ReturnStmt r: return new IrReturn(r.Value is null ? null : LowerExpr(r.Value));
            case BreakStmt: return new IrBreak();
            case ContinueStmt: return new IrContinue();
            case AssignStmt a: return LowerAssign(a);
            case ExprStmt e: return new IrExprStmt(LowerExpr(e.Expr));

            // IOP surface sugar → runtime calls / if
            case MarkStmt mk:
            {
                UseMark(mk.Mark, mk.Span);
                string fn = mk.Remove ? "RemoveTag" : "AddTag";
                return new IrExprStmt(new IrRuntimeCall(fn,
                    new IrExpr[] { LowerExpr(mk.Target), new IrTypeNameExpr(mk.Mark) }));
            }
            case EmitStmt em:
                return new IrExprStmt(new IrRuntimeCall("Emit",
                    new IrExpr[] { new IrStructInit(em.Event, em.Fields.Select(LowerFieldInit).ToList(), em.FillRest) }));
            case DestroyStmt d:
                return new IrExprStmt(new IrRuntimeCall("DestroyEntity", new[] { LowerExpr(d.Target) }));
            case AttachStmt at:
            {
                if (at.Remove)
                    return new IrExprStmt(new IrRuntimeCall("RemoveComponent",
                        new IrExpr[] { LowerExpr(at.Target), new IrTypeNameExpr(at.Shape) }));
                IrExpr init = at.Init is null
                    ? new IrTypeNameExpr(at.Shape)
                    : new IrStructInit(at.Shape, at.Init.Select(LowerFieldInit).ToList());
                return new IrExprStmt(new IrRuntimeCall("AddComponent",
                    new IrExpr[] { LowerExpr(at.Target), init }));
            }
            case ChanceStmt c:
                return new IrIf(
                    new IrBinary(IrBinOp.Lt, new IrRuntimeCall("random", Array.Empty<IrExpr>()),
                        new IrLiteral(c.Probability, IrLiteralKind.Percent)),
                    LowerBlock(c.Body), null);

            case BringStmt br: return LowerBring(br);

            default:
                _diag.Error("VS0201", $"Cannot lower statement {s.GetType().Name}.", s.Span);
                return new IrExprStmt(new IrLiteral(null, IrLiteralKind.Int));
        }
    }

    /// `bring [N] Builder(args)` desugars to: bind params, then emit the builder's output fragment
    /// event — repeated N times. Params are the builder's members minus its output field (markup/
    /// code/css); that output field's `=` value is the template. No new IR node.
    // Resolve a qualified `*Author.Bundle.Publicator.&Builder` against the stdlib builders, matching by
    // trailing segments (so a shorter qualifier still resolves, like the qualified event refs).
    //
    // Returns the index KEY as well, because the builder's own `$Shape` includes have to be resolved
    // against the bundle that DECLARED it, not the one bringing it — see ResolveOwnedShape.
    private (string Key, BuilderDecl Value)? ResolveExternalBuilder(IReadOnlyList<string> path, string name)
    {
        string refKey = string.Join(".", path) + "." + name;
        foreach (var kv in Index.Builders)
            if (kv.Key == refKey || kv.Key.EndsWith("." + refKey, StringComparison.Ordinal)) return (kv.Key, kv.Value);
        return null;
    }

    /// A bare `$Shape` include inside a builder that came from ANOTHER bundle resolves against that
    /// bundle, never against the consumer's shapes.
    ///
    /// Without this, `ExpandMembers` looked the name up in `_shapeFields` — which holds only the bundle
    /// being lowered — so an imported builder's include found nothing and expanded to zero fields. The
    /// symptom pointed everywhere but the cause: a VS0210 *warning* against the library's source, then a
    /// VS0204 *error* at every call site in the consumer saying the builder takes 0 params.
    ///
    /// `ownerKey` is `Author.Bundle.Publicator.Member`. The shape may sit in any publicator of that
    /// bundle, so the search is on the `Author.Bundle.` prefix — the same "a bundle's publicators are one
    /// vocabulary" rule `use` follows.
    private List<FieldDecl>? ResolveOwnedShape(string? ownerKey, string name)
    {
        if (ownerKey is null) return null;

        int firstDot = ownerKey.IndexOf('.');
        int secondDot = firstDot < 0 ? -1 : ownerKey.IndexOf('.', firstDot + 1);
        if (secondDot < 0) return null;
        string bundlePrefix = ownerKey[..(secondDot + 1)];          // "Author.Bundle."

        foreach (var kv in Index.Shapes)
            if (kv.Key.StartsWith(bundlePrefix, StringComparison.Ordinal) &&
                kv.Key.EndsWith("." + name, StringComparison.Ordinal))
                return kv.Value.Members.OfType<FieldDecl>().ToList();
        return null;
    }

    /// `bring Unit(10, 6)` where `Unit` is an identity template — a builder carrying a `mark` member.
    ///
    /// Desugars to the spawn/attach/mark sequence an author would write by hand, and to the SAME IR
    /// nodes, so the two forms cannot drift apart: `spawn()` is immediate, `attach` and `mark` are
    /// deferred to the commit point exactly as the statements are. `bring N Unit(…)` wraps the whole
    /// sequence in the repeat `LowerBring` already builds, so it makes N separate identities.
    ///
    /// Arguments bind positionally across the includes in declaration order — `$Health` takes hp, then
    /// `$Shield` takes sp — which is the same rule that flattens includes into a fragment builder's
    /// parameter list, just grouped back into one `attach` per shape.
    private IrStmt LowerIdentityBring(BringStmt br, BuilderDecl b, string? ownerKey)
    {
        var stmts = new List<IrStmt>();

        // `bring X(…) as out` names the identity instead of hiding it in a generated local. A count
        // rebinds on every iteration, so the name would mean only the last one — refused rather than
        // silently kept.
        if (br.Bind is not null && br.Count is not null)
            _diag.Error("VS0222",
                $"`bring {b.Name}(…) as {br.Bind}` cannot take a count — the name would bind only the last one.",
                br.Span);

        // Only the GENERATED name consumes a depth slot; `as` names it instead. Decrementing
        // unconditionally at the end drove the counter negative and produced `__ent-1`.
        bool generated = br.Bind is null;
        string ent = generated ? "__ent" + _identityDepth++ : br.Bind;
        stmts.Add(new IrLet(ent, null, new IrCall(new IrLocalRef("spawn"), Array.Empty<IrExpr>()), false));

        int arg = 0;
        foreach (var m in b.Members)
        {
            switch (m)
            {
                case ShapeInclude si:
                {
                    var fields = si.Path.Count > 0 ? ResolveExternalShape(si.Path, si.Shape)
                               : ownerKey is not null ? ResolveOwnedShape(ownerKey, si.Shape)
                               : _shapeFields.TryGetValue(si.Shape, out var local) ? local
                               : ResolveUsed(Index.Shapes, "$", si.Shape, si.Span)?.Value.Members.OfType<FieldDecl>().ToList();
                    if (fields is null)
                    {
                        _diag.Warning("VS0210", $"Unknown shape '${si.Shape}' in include.", si.Span);
                        continue;
                    }

                    // `$Shape.field` includes ONE field; the rest of the shape keeps its declared default.
                    var take = si.Field is null ? fields : fields.Where(f => f.Name == si.Field).ToList();
                    var init = new List<(string, IrExpr)>();
                    foreach (var f in take)
                    {
                        IrExpr value = arg < br.Args.Count ? LowerExpr(br.Args[arg])
                                     : f.Default is not null ? LowerExpr(f.Default)
                                     : br.FillRest ? ZeroLiteral(f.Type?.Name ?? "string")
                                     : new IrLiteral(null, IrLiteralKind.Int);
                        arg++;
                        init.Add((f.Name, value));
                    }
                    stmts.Add(new IrExprStmt(new IrRuntimeCall("AddComponent",
                        new IrExpr[] { new IrLocalRef(ent), new IrStructInit(si.Shape, init) })));
                    break;
                }

                case MarkMember mm:
                    foreach (var tag in mm.Marks)
                    {
                        UseMark(tag, mm.Span);
                        stmts.Add(new IrExprStmt(new IrRuntimeCall("AddTag",
                            new IrExpr[] { new IrLocalRef(ent), new IrTypeNameExpr(tag) })));
                    }
                    break;

                // A loose field has no component to land in. Silently dropping it would take an argument
                // and put it nowhere, so it is reported at the declaration that caused it.
                case FieldDecl fd:
                    _diag.Error("VS0206",
                        $"Identity template '{b.Name}' cannot carry the field '{fd.Name}' — a `mark` member " +
                        "makes it build an identity, and every value it takes has to belong to a $Shape it " +
                        "attaches. Move the field into a shape, or drop the `mark` to make this a builder " +
                        "that emits.", fd.Span);
                    break;
            }
        }

        if (arg < br.Args.Count && !br.FillRest)
            _diag.Error("VS0204", $"Builder '{b.Name}' takes {arg} param(s), got {br.Args.Count}.", br.Span);

        if (generated) _identityDepth--;
        // Transparent when it binds a name: the C# backend braces a block, and `out` must outlive it.
        var body = new IrBlock(stmts, Transparent: br.Bind is not null);
        return br.Count is null ? body
             : new IrLoop(IrLoopKind.Repeat, null, null, null, null, LowerExpr(br.Count), body);
    }

    /// Nesting depth of identity templates being lowered, so two in one scope get distinct entity locals.
    private int _identityDepth;

    private IrStmt LowerBring(BringStmt br)
    {
        // Qualified `bring *Author.Bundle.Publicator.&Builder(…)` resolves against the stdlib's builders;
        // a bare/local `bring Builder(…)` resolves against this bundle. Either way we get a BuilderDecl and
        // desugar it identically (the qualifier is dropped — like emit — since the runtime keys on the name).
        // `ownerKey` stays null for a builder declared HERE and is the index key for one that came from
        // another bundle — by qualified path or by `use`. Both are imports, and both need it, or the
        // builder's own `$Shape` includes are resolved against the wrong bundle.
        BuilderDecl? b;
        string? ownerKey = null;
        if (br.BuilderPath.Count > 0)
        {
            var hit = ResolveExternalBuilder(br.BuilderPath, br.Builder);
            (ownerKey, b) = (hit?.Key, hit?.Value);
        }
        else if (!_builders.TryGetValue(br.Builder, out b))
        {
            var hit = ResolveUsed(Index.Builders, "&", br.Builder, br.Span);    // local first, then `use`
            (ownerKey, b) = (hit?.Key, hit?.Value);
        }

        if (b is null)
        {
            string reff = br.BuilderPath.Count > 0 ? "*" + string.Join(".", br.BuilderPath) + ".&" + br.Builder : br.Builder;
            _diag.Error("VS0203", $"Unknown builder '{reff}'.", br.Span);
            return new IrExprStmt(new IrLiteral(null, IrLiteralKind.Int));
        }

        // An IDENTITY template: a `mark` member says this builder constructs an identity rather than
        // emitting anything, because only an identity can be marked. `bring Unit(10, 6)` then means
        // exactly what the hand-written form means — and desugars to the same IR, so it cannot drift:
        //
        //     let e = spawn()
        //     attach $Health to e { hp: 10 }
        //     attach $Shield to e { sp: 6 }
        //     mark e #Unit
        if (b.Members.OfType<MarkMember>().Any()) return LowerIdentityBring(br, b, ownerKey);

        // Past here the builder emits a FRAGMENT or an event — it constructs no identity, so there is
        // nothing for `as` to name. Silently ignoring the binding would leave a name that reads like an
        // entity and holds nothing.
        if (br.Bind is not null)
            _diag.Error("VS0221",
                $"`as {br.Bind}` needs an identity to bind, and builder '{b.Name}' builds a fragment. "
                + "Only a builder with a `mark` member constructs an identity.", br.Span);

        // Otherwise a builder either has a fragment output channel (markup/code/css/line — emits that one
        // field to @Html/@Script/@Style/@Print) OR no channel at all, in which case it constructs and emits
        // an event named after the builder carrying ALL its params (`builder Console { name, firsttext }` →
        // emit @Console { name, firsttext }).
        var output = b.Members.OfType<FieldDecl>().FirstOrDefault(f => OutputFields.Contains(f.Name));
        if (output is not null && output.Default is null)
        {
            _diag.Error("VS0205", $"Builder '{b.Name}' output field '{output.Name}' needs a value.", br.Span);
            return new IrExprStmt(new IrLiteral(null, IrLiteralKind.Int));
        }

        // Parameters = every member except the (optional) output channel field, with $Shape includes expanded.
        var prms = ExpandMembers(b.Members.Where(m => !ReferenceEquals(m, output)), ownerKey);
        if (!br.FillRest && br.Args.Count > prms.Count)
            _diag.Error("VS0204", $"Builder '{b.Name}' takes {prms.Count} param(s), got {br.Args.Count}.", br.Span);

        var stmts = new List<IrStmt>();
        for (int i = 0; i < prms.Count; i++)
        {
            var (name, type, def) = prms[i];
            IrExpr value = i < br.Args.Count ? LowerExpr(br.Args[i])
                         : def is not null ? LowerExpr(def)
                         : br.FillRest ? ZeroLiteral(type?.Name ?? "string")
                         : new IrLiteral(null, IrLiteralKind.Int);
            stmts.Add(new IrLet(name, null, value, false));
        }

        (string ev, (string, IrExpr)[] fields) = output is null
            ? (b.Name, prms.Select(p => (p.Name, (IrExpr)new IrLocalRef(p.Name))).ToArray())   // construct @<Builder> from params
            : (output.Name switch { "code" => "Script", "css" => "Style", "line" => "Print", _ => "Html" },
               new[] { (output.Name switch { "code" => "code", "css" => "css", "line" => "text", _ => "markup" }, LowerExpr(output.Default!)) });
        stmts.Add(new IrExprStmt(new IrRuntimeCall("Emit", new IrExpr[] { new IrStructInit(ev, fields) })));
        var body = new IrBlock(stmts);

        return br.Count is null
            ? body
            : new IrLoop(IrLoopKind.Repeat, null, null, null, null, LowerExpr(br.Count), body);
    }

    private static string SigilChar(MemberSigil s) => s switch
    { MemberSigil.Event => "@", MemberSigil.Shape => "$", MemberSigil.Mark => "#", _ => "" };

    /// A typed zero placeholder used to satisfy required fields/params under `?` (for testing).
    private static IrExpr ZeroLiteral(string typeName) => typeName switch
    {
        "int" or "Entity" => new IrLiteral(0L, IrLiteralKind.Int),   // Entity is an int id
        "float" => new IrLiteral(0.0, IrLiteralKind.Float),
        "bool" => new IrLiteral(false, IrLiteralKind.Bool),
        "percent" => new IrLiteral(0.0, IrLiteralKind.Percent),
        "Mark" => new IrLiteral(Interp.RootConsole, IrLiteralKind.String),  // an identity ref: name the root
        _ => new IrLiteral("", IrLiteralKind.String)   // string, shapes, … → placeholder
    };

    private IrStmt LowerAssign(AssignStmt a)
    {
        var target = LowerExpr(a.Target);
        var value = LowerExpr(a.Value);
        IrBinOp? compound = a.Op switch
        {
            AssignOp.PlusEq => IrBinOp.Add,
            AssignOp.MinusEq => IrBinOp.Sub,
            AssignOp.StarEq => IrBinOp.Mul,
            AssignOp.SlashEq => IrBinOp.Div,
            _ => null
        };
        return compound is null
            ? new IrAssign(target, value)
            : new IrAssign(target, new IrBinary(compound.Value, target, value));
    }

    private (string, IrExpr) LowerFieldInit(FieldInit f) => (f.Name, LowerExpr(f.Value));

    // ---- expressions ----------------------------------------------------

    private IrExpr LowerExpr(Expr e)
    {
        switch (e)
        {
            case LiteralExpr l:
                if (l.Kind == LiteralKind.Percent)
                {
                    double frac = l.Value is double d ? d / 100.0 : 0;
                    return new IrLiteral(frac, IrLiteralKind.Percent);
                }
                return new IrLiteral(l.Value, MapLit(l.Kind));
            // The name bound by the enclosing `target … as <bind>` IS the identity, not an ordinary local —
            // that is what makes `self.Health.hp -= 1` a fold contribution rather than a plain assignment.
            case NameExpr n when IsTargetBind(n.Name): return new IrSelfRef();
            case NameExpr n: return new IrLocalRef(n.Name);
            case EntityExpr: return new IrEntityRef();
            // `*` qualified cross-bundle ref. Not linked/resolved at runtime yet (surface + tooling
            // pass): lower to a scope ref carrying the dotted path + sigil'd member so the IR is
            // representable; project tooling does the real resolution/collision checks.
            case StarRefExpr star: return new IrScopeRef(string.Join(".", star.Path), SigilChar(star.Sigil) + star.Member);
            case ShapeRefExpr sr: return new IrTypeNameExpr(sr.Name);
            case EventRefExpr er: return new IrTypeNameExpr(er.Name);
            case MarkRefExpr mr: UseMark(mr.Name, mr.Span); return new IrTypeNameExpr(mr.Name);
            case MemberExpr me: return new IrFieldAccess(LowerExpr(me.Receiver), me.Name);
            case IndexExpr ix: return new IrIndex(LowerExpr(ix.Receiver), LowerExpr(ix.Index));
            // `*Author.Bundle.Publicator.name(…)` — a cross-bundle call. The callee resolves to a shared
            // `fn`/`SF`, which is IMPORTED into this module under a mangled name so the runtime can find
            // it, exactly as a qualified `bring` pulls in a builder. Without this the call would lower to
            // an IrScopeRef, which the interpreter cannot evaluate, and silently do nothing.
            case CallExpr { Callee: StarRefExpr star } c when star.Sigil == MemberSigil.None:
            {
                string? imported = ImportExternalFunction(star.Path, star.Member, c.Span);
                var args = c.Args.Select(LowerExpr).ToList();
                return imported is null
                    ? new IrCall(new IrScopeRef(string.Join(".", star.Path), star.Member), args)
                    : new IrCall(new IrLocalRef(imported), args);
            }
            // A BARE call that is not local: if a `use`d bundle exports it, import it exactly as the
            // qualified form above does — same mangling, same recursion — so `use Console` makes
            // `print("hi")` mean `*Vein.Console.Io.print("hi")`. Anything still unresolved falls through
            // unchanged, so a call to a genuinely local name (or a prebuilt like `spawn`) is untouched.
            case CallExpr { Callee: NameExpr n } c when !_localFuncs.Contains(n.Name)
                                                    && ResolveUsed(Index.Functions, "", n.Name, c.Span) is { } found:
            {
                // A BUILT-IN already resolves, and `use` only ever WIDENS what a bare name may mean — it
                // must never change one that already meant something. So the built-in wins here and the
                // used bundle's member stays reachable by its qualified path.
                //
                // The silence was the bug. `use Console` bound bare `spawn` to *Vein.Console.Io.spawn —
                // a two-parameter console launcher — so `let e = spawn()` built no entity, reported
                // nothing, and every `target` in the program then matched an empty world. The symptom
                // appeared nowhere near the cause, which is why this warns rather than quietly winning.
                if (Interp.PrebuiltNames.Contains(n.Name))
                {
                    _diag.Warning("VS0217",
                        $"'{n.Name}' is built in, so `use` cannot rebind it — '*{found.Key}' is shadowed here. " +
                        $"Call it by its qualified path to reach it.", c.Span);
                    return new IrCall(LowerExpr(c.Callee), c.Args.Select(LowerExpr).ToList());
                }

                var path = found.Key.Split('.')[..^1];
                string? imported = ImportExternalFunction(path, n.Name, c.Span);
                var args = c.Args.Select(LowerExpr).ToList();
                return imported is null ? new IrCall(LowerExpr(c.Callee), args)
                                        : new IrCall(new IrLocalRef(imported), args);
            }
            case CallExpr c: return new IrCall(LowerExpr(c.Callee), c.Args.Select(LowerExpr).ToList());
            case BinaryExpr b: return new IrBinary(MapBin(b.Op), LowerExpr(b.Left), LowerExpr(b.Right));
            case UnaryExpr u: return new IrUnary(u.Op == UnOp.Neg ? IrUnOp.Neg : IrUnOp.Not, LowerExpr(u.Operand));
            case StructLitExpr sl: return new IrStructInit(sl.TypeName, sl.Fields.Select(LowerFieldInit).ToList());
            case ListLitExpr ll: return new IrList(ll.Items.Select(LowerExpr).ToList());
            default:
                _diag.Error("VS0202", $"Cannot lower expression {e.GetType().Name}.", e.Span);
                return new IrLiteral(null, IrLiteralKind.Int);
        }
    }

    private static IrLiteralKind MapLit(LiteralKind k) => k switch
    {
        LiteralKind.Int => IrLiteralKind.Int,
        LiteralKind.Float => IrLiteralKind.Float,
        LiteralKind.String => IrLiteralKind.String,
        LiteralKind.Bool => IrLiteralKind.Bool,
        _ => IrLiteralKind.Percent
    };

    private static IrBinOp MapBin(BinOp op) => op switch
    {
        BinOp.Or => IrBinOp.Or, BinOp.And => IrBinOp.And,
        BinOp.Eq => IrBinOp.Eq, BinOp.Ne => IrBinOp.Ne,
        BinOp.Lt => IrBinOp.Lt, BinOp.Gt => IrBinOp.Gt,
        BinOp.Le => IrBinOp.Le, BinOp.Ge => IrBinOp.Ge,
        BinOp.Add => IrBinOp.Add, BinOp.Sub => IrBinOp.Sub,
        BinOp.Mul => IrBinOp.Mul, BinOp.Div => IrBinOp.Div,
        _ => IrBinOp.Mod
    };
}
