using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;
using Vein.Compiler.Semantics;

namespace Vein.Compiler.Ir;

// A use checked against what its declaration says. Each check here closes a case that compiled clean and then
// ran with a plausible wrong answer — the bug class ROADMAP's Tier 1 is about.
//
//   VS0390  an assignment to a `let`
//   VS0391  a field the shape or event does not declare (`row.Task.titel`, `ev.feild`)
//   VS0392  a type name nothing declares (`x: strng`)
//   VS0393  a field read straight off an identity, with its shape left out (`row.title`)
//
// Kept beside Lower rather than in it for the reason Lower.Lock.cs is: one concern, one vocabulary. The hooks
// in Lower.cs are single calls where a use is lowered.
public sealed partial class Lower
{
    // ---- `let` --------------------------------------------------------------------------------------

    /// The names the body being lowered has declared so far, and whether each is a `let`. FLAT, because the
    /// interpreter's locals are: an `IrBlock` shares its parent's scope, so a `let` inside an `if` is still
    /// that name after it. Parameters and bindings (`as x`) are names too, and assignable. Null outside a body.
    private Dictionary<string, (bool Let, SourceSpan At)>? _bodyNames;

    /// The shard, ShardView or bridge whose members are being lowered: its state is what an assigned name
    /// reaches when no local has it. Null in a bundle-level `fn`/`SF`, whose state is whichever shard CALLED
    /// it, and in an imported body.
    private string? _stateOwner;

    private void DeclareName(string name, bool isLet, SourceSpan at)
    {
        if (_bodyNames is not null) _bodyNames[name] = (isLet, at);
    }

    /// One body's names, from its parameters on; the enclosing body's come back when it ends. A body can
    /// start inside another — an imported fn is lowered at the first call to it — so this nests.
    private IDisposable BodyNames(IEnumerable<string> parameters, bool imported = false)
    {
        var (outerNames, outerOwner) = (_bodyNames, _stateOwner);
        _bodyNames = new(StringComparer.Ordinal);
        foreach (var p in parameters) _bodyNames[p] = (false, default);
        if (imported) _stateOwner = null;
        return new Scope(() => (_bodyNames, _stateOwner) = (outerNames, outerOwner));
    }

    /// The members of one shard-like object are lowered with its state in reach.
    private IDisposable StateOf(string shard)
    {
        var outer = _stateOwner;
        _stateOwner = shard;
        return new Scope(() => _stateOwner = outer);
    }

    /// VS0390: `k = …` (or `+=`, `-=`, `*=`, `/=`) where `k` is a `let`. `IrLet.Mutable` was carried into the
    /// IR and never read, so `let k = 1  k = 2` compiled and printed 2 — and a reader trusting the `let` was
    /// told something false about every line after it.
    ///
    /// THE NAME IS RESOLVED AS THE INTERPRETER RESOLVES IT: the body's own names first, then the state of the
    /// shard it belongs to (`Interp.DoAssign`, local first since d7fddc0). So a function's own `var cats`
    /// shadows the shard's `let cats`, and a `let cats` of its own makes `cats = …` an error even where the
    /// shard's `cats` is a `var`. A bundle-level fn is checked against its own names only: the shard whose
    /// state it would reach is the caller, unknown here.
    ///
    /// Only the NAME is fixed. `xs[i] = v` and `row.done = true` change what a `let` holds, not what it names —
    /// a list is the same list everywhere it is held (RULES 28h), so `let` could not promise its contents were
    /// fixed anyway. `xs += 2` is a reassignment, though: growing a list makes a new one and rebinds the name.
    private void CheckLetAssign(AssignStmt a)
    {
        if (_importDepth > 0 || a.Target is not NameExpr { Name: var name } || IsTargetBind(name)) return;

        string? whose = null;
        SourceSpan at = default;
        if (_bodyNames is not null && _bodyNames.TryGetValue(name, out var local))
        {
            if (!local.Let) return;
            (whose, at) = ("a", local.At);
        }
        else if (_stateOwner is { } shard && _shardVars.GetValueOrDefault(shard)?.GetValueOrDefault(name) is { Mutable: false } state)
            (whose, at) = ($"{shard}'s", state.Span);
        if (whose is null) return;

        string op = a.Op switch
        {
            AssignOp.PlusEq => "+=", AssignOp.MinusEq => "-=", AssignOp.StarEq => "*=", AssignOp.SlashEq => "/=", _ => "=",
        };
        _diag.Error("VS0390",
            $"`{name}` is {whose} `let` (line {at.Line}), so `{name} {op} …` cannot change it. Declare it `var {name}` " +
            "if it is meant to change, or give the new value a name of its own.", a.Span);
    }

    // ---- fields -------------------------------------------------------------------------------------

    /// Where each field access the program WROTE came from, so a field Resolve finds no declaration for is
    /// reported at the line that reads it. By reference: two `row.titel`s are two records that compare equal.
    ///
    /// Only this bundle's own source is recorded. An imported body is another bundle's code, checked when that
    /// bundle compiles — reporting it here would point the author at a line in a library.
    private readonly Dictionary<IrFieldAccess, (SourceSpan Span, bool Write)> _fieldSites =
        new(ReferenceEqualityComparer.Instance);

    private IrFieldAccess NoteField(IrFieldAccess access, SourceSpan span)
    {
        if (_importDepth == 0) _fieldSites[access] = (span, false);
        return access;
    }

    /// The target of an assignment is a write: the message says the value is lost, not that a read is empty.
    private void NoteFieldWrite(IrExpr target)
    {
        if (target is IrFieldAccess fa && _fieldSites.TryGetValue(fa, out var site)) _fieldSites[fa] = (site.Span, true);
    }

    /// VS0391 / VS0393 for every field access Resolve could not find on a declaration it knows.
    ///
    /// AN ERROR, because the declaration is all there is: a component holds the fields its shape declares, an
    /// event payload the fields its event declares plus the envelope every event carries, and a record an
    /// ascription describes is said by its author to be that shape. A field outside that list reads as empty
    /// on every run — RULES 27 promised the ascription would catch `row.titel`, and nothing did.
    ///
    /// THE ONE PAYLOAD THAT IS OPEN is the boot event's — `@Request`, or this bundle's `start @E`. `veinc serve`
    /// puts every query-string and form field on it (`?name=x` reads as `r.name`, stdlib/Web.vein) and
    /// `--set k=v` does the same from the command line, so a field it does not declare may still arrive. There
    /// only a near miss of a declared field is said, and as a warning: `r.pth` is a typo far more often than a
    /// query parameter called `pth`.
    private void ReportUnknownFields(IReadOnlyList<Resolve.UnknownField> unknown, string? bootKey)
    {
        var said = new HashSet<(SourceSpan, string)>();
        foreach (var u in unknown)
        {
            if (!_fieldSites.TryGetValue(u.Access, out var site) || !said.Add((site.Span, u.Access.Field))) continue;
            string field = u.Access.Field;

            if (u.OffIdentity)
            {
                string bind = u.Access.Receiver is IrSelfRef sr ? sr.Bind : "it";
                _diag.Error("VS0393",
                    $"`{bind}` is an identity, and an identity has no fields of its own — its data is on its shapes, so " +
                    $"`{bind}.{field}` {(site.Write ? "writes nothing" : "reads as empty")}. Write `{bind}.{u.Owner.Name}.{field}`.",
                    site.Span);
                continue;
            }

            var declared = u.Owner.Fields.Select(f => f.Name).Where(n => u.Owner.Kind != IrTypeKind.Message || !IsTail(n)).ToList();
            string owner = Display(u.Owner);
            string? near = Nearest(field, declared);
            bool open = u.Owner.Kind == IrTypeKind.Message && (u.Owner.Name == "Request" || u.Owner.Name == bootKey);
            if (open && near is null) continue;

            string what = site.Write
                ? "so this write lands in a field no other part of the program knows about"
                : "so this reads as empty";
            string hint = near is not null ? $" Did you mean '{near}'?" : "";
            string list = declared.Count == 0 ? $" {owner} declares no fields." : $" Its fields: {string.Join(", ", declared)}.";

            // `row.Task.title` on a `row: $Task` record — the ascription makes `row` the record itself (RULES 27).
            if (u.Owner.Kind == IrTypeKind.Component && field == u.Owner.Name)
            {
                string recv = Receiver(u.Access), first = declared.FirstOrDefault() ?? "field";
                _diag.Error("VS0391",
                    $"{owner} has no field '{field}' — `{recv}` is a {owner} value itself, not an identity carrying one, " +
                    $"so its fields are read directly: `{recv}.{first}`, not `{recv}.{field}.{first}`.", site.Span);
                continue;
            }

            if (open)
                _diag.Warning("VS0391",
                    $"{owner} declares no field '{field}'.{hint} A request can carry fields of its own — a query-string or " +
                    $"form field, or `--set {field}=…` — and only one of those would make this read anything.", site.Span);
            else
                _diag.Error("VS0391", $"{owner} has no field '{field}', {what}.{hint}{list}", site.Span);
        }

        static bool IsTail(string n) => n is "origin" or "source" or "from";
    }

    // ---- type names ---------------------------------------------------------------------------------

    /// Every type name this bundle WROTE — fields, parameters, results, `let`/`var` — checked once the whole
    /// bundle is lowered, because a shape a later `target` imports is a type from then on.
    private readonly List<TypeRef> _typeUses = new();

    /// `type Num { … }` names, which `CollectDecls` has no table for.
    private readonly HashSet<string> _typeDecls = new(StringComparer.Ordinal);

    private void UseType(TypeRef? t)
    {
        if (t is null || _importDepth > 0) return;
        _typeUses.Add(t);
        foreach (var arg in t.Args) UseType(arg);
    }

    private void UseTypes(IEnumerable<Node> members)
    {
        foreach (var f in members.OfType<FieldDecl>()) UseType(f.Type);
    }

    /// VS0392: a type name that names nothing. Any word used to pass `veinc check` (Vein Cards' report #13),
    /// and a type is not decoration: it decides a field's starting value and which checks run on what is
    /// passed. `var n: itn` started EMPTY rather than 0, and `x: strng` let `f(5)` through VS0230 unremarked.
    ///
    /// AN ERROR when the name is one slip from a type that exists (`strng`, `flaot`, `Entiy`, a shape spelt
    /// `Tsak`): nothing else could have been meant, and a typo changes defaults without a word.
    ///
    /// A WARNING otherwise (`any`, `Vector3`): the name may be a shape another bundle of an app declares, and
    /// what an author usually means by one is a value read from JSON, which now has names — `json`, and
    /// `dictionary` for a parsed object (`TypeNames`). The warning says to write them.
    private void CheckTypeNames()
    {
        var reported = new HashSet<SourceSpan>();
        List<string>? candidates = null;
        foreach (var t in _typeUses)
        {
            if (IsTypeName(t.Name) || !reported.Add(t.Span)) continue;
            candidates ??= TypeNames.Concat(_shapeFields.Keys).Concat(_typeDecls).Concat(_events.Keys)
                .Concat(_enums.Keys).Concat(_importedShapes.Keys).Concat(_importedEvents.Keys).Distinct().ToList();

            if (Slips(t.Name, candidates) is { Count: > 0 } meant)
                _diag.Error("VS0392",
                    $"Unknown type '{t.Name}'. Did you mean {string.Join(" or ", meant.Select(m => $"'{m}'"))}? A misspelt " +
                    "type is not just a label: it changes what the value starts as and switches off the checks on what " +
                    "is passed in.", t.Span);
            else
                _diag.Warning("VS0392",
                    $"'{t.Name}' is not a type here, so nothing about this value is checked. A type is int, float, bool, " +
                    "string, percent, Entity, Mark, list<…>, or a shape, enum, `type` or event declared here or shared by " +
                    "a bundle this one needs. For a value read from JSON whose shape the source does not say, write " +
                    "`json` (`dictionary` for a parsed object); for a record whose fields you know, declare a shape and " +
                    "name it.", t.Span);
        }
    }

    /// A built-in, or a shape, `type`, enum or event this bundle can see: its own, one it imported, one a
    /// needed bundle shares — or one ANY bundle on the search path declares, since components unify by bare
    /// name across an app (RULES 15b) and a capability bundle may name the principal's.
    private bool IsTypeName(string name) =>
        TypeNames.Contains(name) || _shapeFields.ContainsKey(name) || _typeDecls.Contains(name)
        || _events.ContainsKey(name) || _enums.ContainsKey(name)
        || _importedShapes.ContainsKey(name) || _importedEvents.ContainsKey(name)
        || Index.Shapes.Keys.Any(k => k.EndsWith("." + name, StringComparison.Ordinal))
        || Index.Events.Keys.Any(k => k.EndsWith("." + name, StringComparison.Ordinal))
        || Index.Shapes.Values.Any(s => s.Members.OfType<EnumDecl>().Any(e => e.Name == name));

    /// The known types `name` is a slip of, nearest first: one edit away (a swap of two neighbours counts as
    /// one), or two for names of five letters or more. Two edits on a short name is a different word —
    /// `any` is two from `int` and was never meant as one.
    private static List<string> Slips(string name, IEnumerable<string> known)
    {
        var near = known.Select(k => (Name: k, D: Edits(name, k)))
                        .Where(x => x.D == 1 || (x.D == 2 && name.Length >= 5 && x.Name.Length >= 5))
                        .ToList();
        int best = near.Count == 0 ? 0 : near.Min(x => x.D);
        return near.Where(x => x.D == best).Select(x => x.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    /// Edit distance where swapping two neighbouring letters is one edit (`itn` → `int`), as a typing slip is.
    private static int Edits(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }

    /// `$Task`, `@Picked` (an event key's readable half), or a `type`'s bare name.
    private static string Display(IrType t) => t.Kind switch
    {
        IrTypeKind.Component => "$" + t.Name,
        IrTypeKind.Message => "@" + Interp.EventName(t.Name),
        _ => t.Name,
    };

    /// The receiver of an access as the author wrote it, for a message that quotes the line.
    private static string Receiver(IrFieldAccess fa) => fa.Receiver switch
    {
        IrSelfRef s => s.Bind,
        IrLocalRef l => l.Name,
        IrFieldAccess inner => Receiver(inner) + "." + inner.Field,
        _ => "it",
    };
}
