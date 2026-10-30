using Vein.Compiler.Ir;

namespace Vein.Compiler.Semantics;

/// The pass IR-SPEC.md describes and the tree never had: types on every expression.
///
/// **Why it exists.** IR-SPEC.md promises a backend two things — *"Fully typed"* and *"Names resolved"*
/// — and `IrExpr.ResolvedType` was assigned nowhere. So every consumer re-derived what the IR was
/// supposed to state, and they drifted. Concretely, all of these were one missing fact:
///
///   * the C# backend guessed syntactically whether a `+` built TEXT, so bools printed "True" against
///     the interpreter's "true", and a double formatted in the machine's culture — "3,5" in France;
///   * VS0230 could check only literal arguments, and its own comment says why;
///   * `veinc ir` renders every `+` as `Concat`, which VEINIR-FORMAT.md lists as a known gap for the
///     same reason.
///
/// One consumer re-deriving is a duplication. Two is a divergence. The stated goal is a third runtime,
/// and the Workbench is a fourth — which is the wall this removes.
///
/// **What it is not.** Not a type CHECKER. It infers and records; it reports nothing. Diagnostics stay
/// in `Lower`, which already owns them, and can be widened to use these types now that they exist.
/// Nothing here rejects a program that used to compile.
///
/// **Partial by design.** A null `ResolvedType` means "not known yet", never "error". VeinScript is
/// dynamically evaluated — a `hear` payload field, a `fromJson` result, an untyped local — and some
/// expressions genuinely have no static type. Consumers must keep their fallbacks; what they gain is
/// that the fallback is now the exception rather than the rule.
public sealed class Resolve
{
    private readonly IrModule _module;

    /// Component and event declarations by name, for typing `self.Comp.field` and `payload.field`.
    private readonly Dictionary<string, IrType> _types = new(StringComparer.Ordinal);

    /// Module-level `fn`/`SF` return types, for typing a call.
    private readonly Dictionary<string, IrTypeRef> _funcs = new(StringComparer.Ordinal);

    /// The innermost scope's locals and params. A block does not introduce a scope in this IR (an
    /// `IrBlock` shares its parent's locals — see Hir.cs), so one flat map per function matches how the
    /// interpreter actually resolves names.
    private readonly Dictionary<string, IrTypeRef> _locals = new(StringComparer.Ordinal);

    /// The component each `target $Shape … as x` bound, keyed by BINDING — so the bare `self.field` form,
    /// where the shape is implied rather than written, resolves against the right loop.
    ///
    /// This was a single value, and a single value was all a nameless `IrSelfRef` could support. It also
    /// meant a nested loop overwrote its parent's, which is the type-level shadow of the bug RULES.md 12c
    /// described at runtime.
    private readonly Dictionary<string, string> _selfComps = new(StringComparer.Ordinal);

    /// Every shape a `target $A $B … as x` query matched, by binding — what `x.field` with no shape named
    /// could have meant (see `UnknownField.OffIdentity`).
    private readonly Dictionary<string, IReadOnlyList<string>> _queryComps = new(StringComparer.Ordinal);

    /// Locals whose type was INFERRED for a `var` — `var cur = row.Task` — and may be anything after the next
    /// assignment. Typed for the backends as before, but no field read through one is called unknown: a `let`
    /// cannot change what it holds (VS0390), and a declared type is the author's own word.
    private readonly HashSet<string> _guessed = new(StringComparer.Ordinal);

    private readonly List<UnknownField> _unknown = new();

    /// A field read or written through a receiver whose DECLARATION is known here — a component handle, an
    /// event payload, a record an ascription or a declared type describes — and that the declaration does
    /// not have. At run time every one of these is an empty read or a write nothing reads back.
    ///
    /// Recorded, not reported: this pass still reports nothing. `Lower` owns the source spans and turns each
    /// into VS0391 (or VS0393 when `OffIdentity`).
    ///
    /// `OffIdentity` — `row.title` where `row` is a `target $Task` binding: an IDENTITY, which has no fields
    /// of its own. The interpreter reads it as empty while the C# backend quietly read `$Task`'s `title`,
    /// so the two runtimes disagreed about a line that is simply missing its shape (`row.Task.title`).
    public sealed record UnknownField(IrFieldAccess Access, IrType Owner, bool OffIdentity = false);

    /// Every `UnknownField` in the module, in the order met.
    public IReadOnlyList<UnknownField> Unknown => _unknown;

    public Resolve(IrModule module)
    {
        _module = module;

        // TAGS ARE SKIPPED, and that is the whole subtlety. `$Row` and `#Row` are different things
        // (docs/RULES.md 15) and a module holds BOTH under the same name — a Component and a Tag. Keyed
        // by name alone, whichever came last won, and the Tag comes last: `r.Row.rank` then resolved
        // against a type with no fields and went untyped, 56 times in one sample.
        //
        // Lower carries the mirror image of this warning — deduping by name there let a shape swallow a
        // mark, so the Tag was never emitted at all. Same name, two meanings, in both directions.
        //
        // A Tag can never be a receiver: it has no fields, and `self.Row` means the COMPONENT.
        foreach (var t in module.Types)
            if (t.Kind != IrTypeKind.Tag) _types[t.Name] = t;
        foreach (var f in module.Functions) _funcs[f.Name] = f.Return;
    }

    /// Annotate the whole module in place. Returns it for chaining.
    public static IrModule Run(IrModule module) => Run(module, out _);

    /// …and hand back the field accesses no declaration has (`UnknownField`), for `Lower` to report.
    public static IrModule Run(IrModule module, out IReadOnlyList<UnknownField> unknown)
    {
        var r = new Resolve(module);
        foreach (var f in module.Functions) r.Function(f, null);
        foreach (var s in module.Shards)
            foreach (var m in s.Methods)
                r.Function(m, s);
        unknown = r.Unknown;
        return module;
    }

    /// `owner` is the shard a method belongs to, or null for a module-level `fn`. A shard's STATE is
    /// referenced by bare name from its own methods — a `var` declared on the shard is a local as far as
    /// the body is concerned — so it has to be in scope or every read of it goes untyped.
    private void Function(IrFunction f, IrShard? owner)
    {
        _locals.Clear();
        _selfComps.Clear();
        _queryComps.Clear();
        _guessed.Clear();
        if (owner is not null)
            foreach (var st in owner.State) _locals[st.Name] = st.Type;
        foreach (var p in f.Params) _locals[p.Name] = p.Type;
        Block(f.Body);
    }

    // ---- statements -------------------------------------------------------

    private void Block(IrBlock b) { foreach (var s in b.Statements) Stmt(s); }

    private void Stmt(IrStmt? s)
    {
        switch (s)
        {
            case null: return;
            case IrBlock b: Block(b); return;

            // A `let` with an initialiser TAKES its type, which is what makes chains work:
            // `let n = row.rank` then `n + 1` both resolve.
            case IrLet l:
            {
                var t = Expr(l.Init);
                if (l.Type is not null) _locals[l.Name] = l.Type;
                else if (t is not null) _locals[l.Name] = t;
                if (l.Mutable && l.Type is null) _guessed.Add(l.Name); else _guessed.Remove(l.Name);
                return;
            }

            case IrAssign a: Expr(a.Target); Expr(a.Value); return;
            case IrIf i: Expr(i.Cond); Block(i.Then); if (i.Else is not null) Block(i.Else); return;
            case IrExprStmt e: Expr(e.Expr); return;
            case IrReturn r: Expr(r.Value); return;
            case IrOrdered o: Block(o.Collect); return;
            case IrOrderedBring ob: Expr(ob.Key); Block(ob.Body); return;

            // `weave X(text) as x` — `x` is the identity the one record became (D23).
            case IrWeave w:
                Expr(w.Source);
                Block(w.Record);
                if (w.Bind is not null) _locals[w.Bind] = IrTypeRef.Of("Entity");
                return;

            case IrMatch m:
                Expr(m.Subject);
                foreach (var arm in m.Arms) Block(arm.Body);
                if (m.Else is not null) Block(m.Else);
                return;

            case IrLoop lp: Loop(lp); return;
            default: return;   // break / continue carry nothing
        }
    }

    private void Loop(IrLoop lp)
    {
        Expr(lp.Cond);
        Expr(lp.Count);
        Expr(lp.Source);
        if (lp.Var is not null) _guessed.Remove(lp.Var);

        // Every branch below writes ONLY `_locals[lp.Var]`, and `IrSelfRef` reads back out of it by name.
        // There is no separate "what does self mean here" state to push and pop any more: the binding is
        // named, so the ordinary scope IS the answer, and a nested loop adds a second name beside the
        // first instead of shadowing it.

        // An identity query binds an ENTITY, and names the component whose fields `self.f` reads.
        if (lp.Query is { } q)
        {
            if (lp.Var is not null)
            {
                _locals[lp.Var] = IrTypeRef.Of("Entity");
                if (q.Components.FirstOrDefault() is { } c) _selfComps[lp.Var] = c;
                _queryComps[lp.Var] = q.Components;
            }
        }
        else if (lp.Kind == IrLoopKind.Repeat && lp.Var is not null)
        {
            _locals[lp.Var] = IrTypeRef.Of("int");
        }
        // `target rows as row: $Row` — the AUTHOR said what the elements are. This wins over inference
        // because it is the only thing that can speak for data crossing a boundary: a `fromJson` result
        // has no shape to read, and the author is the one who knows which columns they asked for.
        else if (lp.ElementShape is { } shape)
        {
            if (lp.Var is not null) _locals[lp.Var] = IrTypeRef.Of(shape);
        }

        // Otherwise typed only when the SOURCE says what it holds. A `list<int>` literal does; a
        // `fromJson` result and a field read off a parsed document do not, and those stay UNSET rather
        // than guessed — an absent local reads back as null, which is the honest answer.
        else if (lp.Var is not null && lp.Source is not null
                 && lp.Source.ResolvedType is { Name: "list", Args.Count: 1 } lt)
        {
            _locals[lp.Var] = lt.Args[0];
        }

        Block(lp.Body);
    }

    // ---- expressions ------------------------------------------------------

    /// Type an expression, record it on the node, and return it. Null means "not known".
    private IrTypeRef? Expr(IrExpr? e)
    {
        if (e is null) return null;
        var t = Infer(e);
        e.ResolvedType = t;
        return t;
    }

    private IrTypeRef? Infer(IrExpr e)
    {
        switch (e)
        {
            case IrLiteral l:
                return IrTypeRef.Of(l.Kind switch
                {
                    IrLiteralKind.Int => "int",
                    IrLiteralKind.Float or IrLiteralKind.Percent => "float",
                    IrLiteralKind.Bool => "bool",
                    _ => "string",
                });

            case IrLocalRef r: return _locals.GetValueOrDefault(r.Name);
            case IrEntityRef: return IrTypeRef.Of("Entity");
            case IrLoopIndexRef: return IrTypeRef.Of("int");

            // The innermost `target` binding, which is nameless in this IR (docs/RULES.md 12c) — so what
            // it means depends entirely on the loop enclosing it. An identity query binds an ENTITY; a
            // collection loop binds an element, and only an ascription can say what that is.
            case IrSelfRef sr: return _locals.GetValueOrDefault(sr.Bind);

            case IrUnary u:
                Expr(u.Operand);
                return u.Op == IrUnOp.Not ? IrTypeRef.Of("bool") : Expr(u.Operand);

            case IrBinary b: return Binary(b);
            case IrFieldAccess fa: return Field(fa);
            // `xs[i]` is an element, when the list says what its elements are. A bare `list` (mixed, or a
            // `fromJson` result) stays unknown.
            case IrIndex ix:
            {
                var rt = Expr(ix.Receiver);
                Expr(ix.Index);
                return rt is { Name: "list", Args.Count: 1 } ? rt.Args[0] : null;
            }

            // `list<T>` when every item agrees, bare `list` when they do not. The element type is what
            // lets `target ranks as r { … r … }` type its binding, which in turn types everything read
            // through it — a list literal is the one collection whose contents are visible here.
            case IrList li:
            {
                IrTypeRef? elem = null;
                bool uniform = li.Items.Count > 0;
                foreach (var it in li.Items)
                {
                    var t = Expr(it);
                    if (t is null) uniform = false;
                    else if (elem is null) elem = t;
                    else if (elem.Name != t.Name) uniform = false;
                }
                return uniform && elem is not null
                    ? new IrTypeRef("list", new[] { elem }, false)
                    : IrTypeRef.Of("list");
            }

            case IrStructInit si:
                foreach (var (_, v) in si.Fields) Expr(v);
                return IrTypeRef.Of(si.TypeName);

            // A bare shape/event/mark reference — `attach $Shape to e` lowers the shape to one of these.
            // It names a type, so that is its type.
            case IrTypeNameExpr tn: return IrTypeRef.Of(tn.Name);

            case IrCall c:
                // The CALLEE is an expression too, and skipping it left every `spawn`/`len`/`join`
                // reference untyped even when the call itself resolved.
                Expr(c.Callee);
                foreach (var a in c.Args) Expr(a);
                return c.Callee is IrLocalRef { Name: var fn }
                    ? _funcs.GetValueOrDefault(fn) ?? Builtin(fn)
                    : null;

            case IrRuntimeCall rc:
                foreach (var a in rc.Args) Expr(a);
                // `!Boards.hideDone` (D28) carries the setting's type as its last argument.
                if (rc.Name == "ShardSetting" && rc.Args[^1] is IrLiteral { Value: string settingType })
                    return IrTypeRef.Of(settingType);
                return Runtime(rc.Name);

            case IrToText: return IrTypeRef.Of("string");

            default: return null;
        }
    }

    /// The one inference that pays for itself immediately: whether `+` builds TEXT or adds numbers.
    ///
    /// Both runtimes decide this, and before this pass they decided it differently — the interpreter at
    /// runtime by looking at the values, the C# backend at compile time by looking for a string literal.
    /// A `+` whose operands are a string field and an int had no literal in it and came out as C#
    /// arithmetic on mismatched types, or as `ToString()` in the wrong culture.
    private IrTypeRef? Binary(IrBinary b)
    {
        var l = Expr(b.Left);
        var r = Expr(b.Right);

        switch (b.Op)
        {
            case IrBinOp.And or IrBinOp.Or or IrBinOp.Eq or IrBinOp.Ne
              or IrBinOp.Lt or IrBinOp.Gt or IrBinOp.Le or IrBinOp.Ge:
                return IrTypeRef.Of("bool");

            case IrBinOp.Add:
                // String on either side makes it concatenation — the rule the interpreter applies to
                // values, applied here to types.
                if (l?.Name == "string" || r?.Name == "string") return IrTypeRef.Of("string");
                return Arith(l, r);

            default:
                return Arith(l, r);
        }
    }

    /// int + int stays int; anything with a float becomes float. Mirrors the interpreter's `Num`, which
    /// keeps int-ness when both operands are ints — the reason `rank` prints as `4` and not `4.0`.
    private static IrTypeRef? Arith(IrTypeRef? l, IrTypeRef? r)
    {
        if (l is null || r is null) return null;
        if (l.Name == "float" || r.Name == "float") return IrTypeRef.Of("float");
        if (l.Name == "int" && r.Name == "int") return IrTypeRef.Of("int");
        return null;
    }

    /// What the runtime puts on EVERY event beyond its declared fields (LANGUAGE.md §3.7, `Interp.Emit`):
    /// `origin`, `source` and `from` are typed on the payload already, the rest are always there to read.
    private static readonly HashSet<string> Envelope = new(StringComparer.Ordinal)
        { "id", "from", "origin", "source", "bundle", "cause", "trail" };

    /// `x.field`, in the three shapes this IR produces:
    ///   `self.Comp`        — an entity plus a component name; the pair is a handle, not a value
    ///   `self.Comp.field`  — that handle's field, typed from the component declaration
    ///   `payload.field`    — an event field, typed from the event declaration
    private IrTypeRef? Field(IrFieldAccess fa)
    {
        var recv = Expr(fa.Receiver);

        // `<something typed>.field` — look the field up on the receiver's declaration. Covers a component
        // handle, an event payload and a record an ascription describes with one rule, because all of them
        // are IrTypes with fields.
        //
        // BEFORE the component rule below, because a record is not an identity. `row.Task.title` on a
        // `target rows as row: $Task` row reads a key called "Task", which no $Task has — it used to be typed
        // as the component and read as empty.
        if (recv is not null && _types.TryGetValue(recv.Name, out var decl)
            && decl.Kind is IrTypeKind.Component or IrTypeKind.Message or IrTypeKind.Struct)
        {
            var field = decl.Fields.FirstOrDefault(f => f.Name == fa.Field);
            if (field is null && Known(decl, fa.Receiver) && !(decl.Kind == IrTypeKind.Message && Envelope.Contains(fa.Field)))
                _unknown.Add(new UnknownField(fa, decl));
            return field?.Type;
        }

        // `<entity>.Comp` — naming a component off any entity-valued expression, which the interpreter
        // allows from a `target` binding and from a plain Entity field alike.
        if (_types.TryGetValue(fa.Field, out var comp) && comp.Kind == IrTypeKind.Component)
            return IrTypeRef.Of(fa.Field);

        // `self.field` with no component named — the query bound one, so try it. The C# backend reads that
        // component's field; the interpreter reads an identity, which has no fields, and answers empty. So
        // it is typed for the backend as before, and recorded: the line is missing its shape.
        if (fa.Receiver is IrSelfRef sr && _selfComps.TryGetValue(sr.Bind, out var sc)
            && _types.TryGetValue(sc, out var self))
        {
            if (_queryComps.GetValueOrDefault(sr.Bind)?.Select(c => _types.GetValueOrDefault(c))
                    .FirstOrDefault(t => t?.Fields.Any(f => f.Name == fa.Field) == true) is { } carrier)
                _unknown.Add(new UnknownField(fa, carrier, OffIdentity: true));
            return self.Fields.FirstOrDefault(f => f.Name == fa.Field)?.Type;
        }

        return null;
    }

    /// Whether the receiver's type is the program's own word rather than a guess: anything but a `var`
    /// whose type was inferred from its first value. The provenance record is left alone — the IR declares
    /// two of the fields the runtime fills (`from.identity`, `from.marks`, … are read too).
    private bool Known(IrType decl, IrExpr receiver) =>
        !decl.Attrs.Any(a => a.Name == "provenance")
        && !(receiver is IrLocalRef { Name: var n } && _guessed.Contains(n));

    /// The desugared runtime calls. All but `random` are STATEMENTS — `emit @X { … }` and
    /// `attach $C to e` produce a value nobody reads — so their type is `void`, and saying so is not a
    /// technicality: these were two thirds of every untyped node in the tree, which made the coverage
    /// number look like a typing problem when most of it was a vocabulary gap.
    private static IrTypeRef? Runtime(string name) => name switch
    {
        "Emit" or "AddTag" or "RemoveTag" or "AddComponent" or "RemoveComponent" or "DestroyEntity"
            or "Mute" or "Unmute" or "MuteMark" or "UnmuteMark" or "MarkShard" or "UnmarkShard" or "SetShardSetting"
            => IrTypeRef.Of("void"),
        "random" => IrTypeRef.Of("float"),
        "Carries" => IrTypeRef.Of("bool"),
        "CollHas" => IrTypeRef.Of("bool"),
        _ => null,
    };

    /// The built-in functions that return a value — `Interp.PrebuiltNames`, minus the ones whose result
    /// genuinely has no static type. `fromJson` returns whatever the JSON held and `pick` returns an
    /// element of a list that may be mixed; both stay null, which is the honest answer rather than a
    /// guess a consumer would then trust.
    /// The return type of a built-in.
    ///
    /// Worth keeping COMPLETE, because every name missing from here is an expression the rest of the
    /// compiler has to guess about — and the C# backend's guesses are what produced "True" for a bool
    /// and a comma for a decimal point. The text built-ins were absent for a long time simply because
    /// no sample used them; samples/text_search.vein and text_split_join.vein are what surfaced it.
    ///
    /// The list-returning four — `split`, `lines`, `words`, `chars` — are `list<string>`, the same type
    /// a list literal of strings gets above. No type reference can SPELL it yet (there is no `list<T>`
    /// field type, D9), but every element is a string by construction, and knowing that is what types a
    /// `target words(line) as w` binding and an `xs[0]` read.
    private static IrTypeRef? Builtin(string name) => name switch
    {
        "spawn" => IrTypeRef.Of("Entity"),
        "len" or "int" or "code" or "indexOf" => IrTypeRef.Of("int"),
        "random" or "float" => IrTypeRef.Of("float"),
        "bool" or "isNumber" or "contains" or "startsWith" or "endsWith" or "cryptoVerify" => IrTypeRef.Of("bool"),
        "cryptoKeys" or "cryptoPublic" or "cryptoSeal" or "cryptoOpen" or "cryptoFingerprint" or "cryptoLock"
            or "cryptoUnlock" or "cryptoHash" or "cryptoToken" or "cryptoSign" => IrTypeRef.Of("string"),
        "join" or "toJson" or "string" or "trim" or "upper" or "lower" or "now"
            or "substring" or "replace" or "chr" => IrTypeRef.Of("string"),
        "here" => IrTypeRef.Of("Mark"),
        "split" or "lines" or "words" or "chars" => new IrTypeRef("list", new[] { IrTypeRef.Of("string") }, false),
        _ => null,
    };
}
