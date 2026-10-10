using Vein.Compiler.Diagnostics;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;
using Vein.Compiler.Project;

namespace Vein.Compiler.Ir;

// vein.lock, from the lowering side: record what this bundle uses from `bundles/`, and compare each use
// with the line the project recorded for it. See Project/SignatureLock.cs for why the file exists.
//
// Kept beside Lower rather than in it because it is one concern with one vocabulary — slots, entries,
// verdicts — and Lower's job is lowering. The hooks in Lower.cs are single calls at the points where a
// use is already resolved: a qualified or `need`-widened fn/SF call, a `bring` (both binding paths), and
// an event that is heard, emitted or booted.
public sealed partial class Lower
{
    /// One parameter a builder binds, and where it came from: `ShapeKey` is the index key of the
    /// `$Shape` include that contributed it (null for a field declared inline, or a shape declared here),
    /// `Whole` is false for a `$Shape.field` include, `ShapeFields` is that shape's full field list, and
    /// `Member` numbers the builder member it came from so a list can be regrouped by include.
    private sealed record Slot(
        string Name, TypeRef? Type, bool Optional, string? ShapeKey, bool Whole,
        IReadOnlyList<FieldDecl>? ShapeFields, int Member, bool Qualified = false);

    /// Nonzero while lowering the body of an IMPORTED fn. That body is another bundle's code — what it
    /// calls is not a use this program made, and a drift inside it is not this program's to fix.
    private int _importDepth;

    private readonly Dictionary<string, LockEntry> _outsideUses = new(StringComparer.Ordinal);

    /// (entry or field, site) pairs already reported, so one call is never reported twice however many
    /// times its expression is visited.
    private readonly HashSet<(string, SourceSpan)> _lockReported = new();

    private SignatureLock? _lock;
    private bool _lockLoaded;

    /// Every member this bundle used from `bundles/`, as the line `vein.lock` would record for it. The
    /// commands that RUN a program hand these to `SignatureLock.Baseline`; compiling never writes.
    public IReadOnlyCollection<LockEntry> OutsideUses => _outsideUses.Values;

    /// Read lazily: a program that uses nothing from `bundles/` never touches the disk for it.
    private SignatureLock? Lock
    {
        get
        {
            if (_lockLoaded) return _lock;
            _lockLoaded = true;
            return _lock = SignatureLock.Load(Index, _projectDir ?? BundleSearch.Current);
        }
    }

    /// Whether uses in the bundle being lowered count at all. Not inside an imported body, not when the
    /// project installed nothing, and not when this bundle is ITSELF on the search path — a kit linked
    /// into an app gets its own Lower, and its calls into another kit are the kit's business.
    private bool Recording =>
        _importDepth == 0 && Index.Installed.Count > 0 && !Index.Owners.ContainsKey(_ownerKey);

    /// `kit.Geo.Calc.clamp` → is `kit.Geo` an installed bundle?
    private bool IsOutside(string key)
    {
        int first = key.IndexOf('.');
        int second = first < 0 ? -1 : key.IndexOf('.', first + 1);
        return second > 0 && Index.Installed.Contains(key[..second]);
    }

    // ---- fn / SF -----------------------------------------------------------------------------------------

    private void NoteFunction(string key, FuncDecl decl, SourceSpan site)
    {
        if (!Recording || !IsOutside(key)) return;
        Note(LockEntry.ForFunction(key, decl), site, null);
    }

    // ---- builders ------------------------------------------------------------------------------------

    /// A `bring`, with the slots exactly as its binding path visited them.
    ///
    /// An INSTALLED builder is recorded under its own key, flattened — includes expanded, a variant's
    /// fixed parameters removed — because that list is what the call's arguments bind to, and a shape
    /// swap three bundles away changes it just as surely as editing the builder.
    ///
    /// A LOCAL builder is this bundle's own order, except where an installed `$Shape` fills slots: kitdemo's
    /// `builder Player { $Position $Mover $Score }` binds `bring Player(…)` straight into the kit's
    /// `$Mover`. So the shape is recorded, and the check rebuilds the builder's slot list with each
    /// installed shape as the lock remembers it. Comparing that with the live list catches what comparing
    /// the shape alone would miss — `$Mover` gaining a field shifts `$Score`'s argument too.
    private void NoteBuilderUse(string? outsideKey, string builder, IReadOnlyList<Slot> slots,
                                IReadOnlyDictionary<string, Expr>? fixedArgs, SourceSpan site)
    {
        if (!Recording) return;
        bool Fixed(string name) => fixedArgs is not null && fixedArgs.ContainsKey(name);
        var callable = slots.Where(s => !Fixed(s.Name)).ToList();

        if (outsideKey is not null)
        {
            if (!IsOutside(outsideKey)) return;
            var live = new LockEntry(LockKind.Builder, outsideKey,
                callable.Select(s => new LockParam(s.Name, SignatureLock.TypeText(s.Type))).ToList());
            Note(live, site, callable.Select(s => s.Optional).ToList());
            return;
        }

        var shapes = new Dictionary<string, LockEntry>(StringComparer.Ordinal);
        foreach (var s in slots)
        {
            if (!s.Whole || s.ShapeKey is not { } key || !IsOutside(key) || shapes.ContainsKey(key)) continue;
            var entry = new LockEntry(LockKind.Shape, key,
                s.ShapeFields!.Select(f => new LockParam(f.Name, SignatureLock.TypeText(f.Type))).ToList());
            _outsideUses.TryAdd(entry.Id, entry);
            shapes[key] = entry;
        }
        if (shapes.Count == 0 || Lock is not { } locked) return;

        var drifted = shapes.Values.Where(e => locked.Find(e.Id) is { } l && l.Line != e.Line).ToList();
        if (drifted.Count == 0) return;

        // The slot list as it was when the lock was written: the same includes, each installed shape
        // contributing its LOCKED fields, everything else as it is now.
        var then = new List<LockParam>();
        foreach (var group in slots.GroupBy(s => s.Member))
        {
            var first = group.First();
            var src = first.Whole && first.ShapeKey is { } k && shapes.ContainsKey(k) && locked.Find("shape " + k) is { } ls
                ? ls.Params
                : group.Select(s => new LockParam(s.Name, SignatureLock.TypeText(s.Type)));
            then.AddRange(src.Where(p => !Fixed(p.Name)));
        }
        var nowParams = callable.Select(s => new LockParam(s.Name, SignatureLock.TypeText(s.Type))).ToList();

        var verdict = SignatureLock.Compare(
            new LockEntry(LockKind.Builder, builder, then),
            new LockEntry(LockKind.Builder, builder, nowParams),
            callable.Select(s => s.Optional).ToList());
        if (verdict.Change is LockChange.Same or LockChange.Compatible) return;
        if (!_lockReported.Add(("local " + builder, site))) return;

        string which = string.Join("; ", drifted.Select(e =>
            $"{e.Reference} was {locked.Find(e.Id)!.Signature}, is now {e.Signature}"));
        if (verdict.Change == LockChange.Moved)
            _diag.Error("VS0344",
                $"'{builder}' includes a shape that changed since vein.lock recorded it — {which}. " +
                $"Arguments bind by position, so this call now passes its values to different parameters " +
                $"({verdict.Detail}). Fix the arguments, then accept the new order with `veinc lock`.", site);
        else
            Changed($"'{builder}' includes a shape that changed since vein.lock recorded it — {which} " +
                    $"({verdict.Detail}). Check this call still means what you wrote, then accept it with `veinc lock`.",
                    site, locked, drifted);
    }

    // ---- events --------------------------------------------------------------------------------------

    /// Record an installed event this bundle hears, emits or boots with, and return the fields that
    /// drifted since the lock — null when nothing did. Only a `hear` acts on the answer: an emit naming a
    /// field the event lost is VS0227 already, and a retyped literal is VS0230.
    ///
    /// Looked up QUIETLY (`report: false`): the same reference was already resolved, with its warnings,
    /// by the code that lowers it.
    private (LockEntry Locked, LockEntry Live, IReadOnlyDictionary<string, string> Drift)? NoteEventUse(
        IReadOnlyList<string> path, string name)
    {
        if (!Recording) return null;
        var (key, decl) = ResolveEventOwned(path, name, report: false);
        if (key is null || decl is null || !IsOutside(key)) return null;

        var fields = ExpandMembers(decl.Members, key, report: false);
        var live = LockEntry.ForEvent(key, fields.Select(f => new LockParam(f.Name, SignatureLock.TypeText(f.Type))));
        _outsideUses.TryAdd(live.Id, live);

        if (Lock?.Find(live.Id) is not { } locked) return null;
        var drift = SignatureLock.EventDrift(locked, live);
        return drift.Count == 0 ? null : (locked, live, drift);
    }

    private (string Bind, LockEntry Locked, LockEntry Live, IReadOnlyDictionary<string, string> Drift)? _hearDrift;

    /// The scope a `hear` body is lowered in: which of its binding's fields the lock says drifted.
    private HearScope HearReads(HearBlock hb)
    {
        var saved = _hearDrift;
        _hearDrift = NoteEventUse(hb.EventPath, hb.Event) is { } d ? (hb.Bind, d.Locked, d.Live, d.Drift) : null;
        return new HearScope(this, saved);
    }

    private readonly struct HearScope : IDisposable
    {
        private readonly Lower _owner;
        private readonly (string, LockEntry, LockEntry, IReadOnlyDictionary<string, string>)? _saved;
        public HearScope(Lower owner, (string, LockEntry, LockEntry, IReadOnlyDictionary<string, string>)? saved)
        {
            _owner = owner;
            _saved = saved;
        }
        public void Dispose() => _owner._hearDrift = _saved;
    }

    /// `e.field` inside a `hear` whose event lost or retyped `field` since the lock. Reported at the READ,
    /// because a handler reading a field the event no longer has silently gets empty — and the read is the
    /// line that has to change.
    private void CheckHearRead(MemberExpr me)
    {
        if (_hearDrift is not { } h || me.Receiver is not NameExpr n || n.Name != h.Bind || IsTargetBind(n.Name)) return;
        if (!h.Drift.TryGetValue(me.Name, out var what)) return;
        if (!_lockReported.Add((h.Live.Id + "." + me.Name, me.Span))) return;

        bool gone = what == "is gone";
        Changed($"'{h.Bind}.{me.Name}' reads a field of {h.Live.Reference} that {(gone ? "it no longer has" : "changed type")} " +
                $"since vein.lock recorded it — '{me.Name}' {what} (was {h.Locked.Signature}; now {h.Live.Signature})" +
                (gone ? ", so it reads as empty" : "") +
                ". Update the handler, then accept it with `veinc lock`.",
                me.Span, Lock!, new[] { h.Live });
    }

    /// VS0345, carrying what its Quick Fix needs: the lock, and the line(s) that accepting writes.
    private void Changed(string message, SourceSpan site, SignatureLock locked, IEnumerable<LockEntry> accept) =>
        _diag.Add(new Diagnostic(Severity.Warning, "VS0345", message, site)
        {
            Data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["lock"] = locked.Path,
                ["lines"] = string.Join("\n", accept.Select(e => e.Line)),
            },
        });

    // ---- the comparison ------------------------------------------------------------------------------

    private void Note(LockEntry live, SourceSpan site, IReadOnlyList<bool>? liveOptional)
    {
        _outsideUses.TryAdd(live.Id, live);
        if (Lock?.Find(live.Id) is not { } locked) return;

        var verdict = SignatureLock.Compare(locked, live, liveOptional);
        if (verdict.Change is LockChange.Same or LockChange.Compatible) return;
        if (!_lockReported.Add((live.Id, site))) return;

        if (verdict.Change == LockChange.Moved)
            _diag.Error("VS0344",
                $"{live.Reference} has moved its parameters since vein.lock recorded it: {locked.Signature} → " +
                $"{live.Signature}. Arguments bind by position, so this call now passes its values to different " +
                $"parameters ({verdict.Detail}). Fix the arguments, then accept the new order with `veinc lock`.", site);
        else
            Changed($"{live.Reference} has changed since vein.lock recorded it: {locked.Signature} → {live.Signature} " +
                    $"({verdict.Detail}). Check this call still means what you wrote, then accept it with `veinc lock`.",
                    site, Lock!, new[] { live });
    }
}
