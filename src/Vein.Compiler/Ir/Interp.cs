using System.Globalization;
using System.IO;

namespace Vein.Compiler.Ir;

/// A minimal reactive interpreter over the HIR. It runs the emit/hear event loop: fire an event,
/// let shards/ShardViews `hear` it and `emit` more events, and collect the final @Response body.
///
/// Scope (first cut): global shards + ShardViews (state vars + hear handlers), emit, if/while/repeat,
/// expression evaluation, and the `+` operator over strings/numbers. Identity queries (`target`),
/// folds, and SF invocation are not executed yet — they need the full runtime. Enough to render a
/// reactive page end-to-end.
public sealed class Interp
{
    private sealed class Instance
    {
        public required string Name;
        public required VeinFirstClass Fc;                 // its unified runtime identity
        public readonly Dictionary<string, object?> State = new();
    }

    private sealed record Handler(Instance Owner, string BindName, IrBlock Body,
        IReadOnlyList<string> ReqShapes, IReadOnlyList<string> ReqMarks);

    private readonly Dictionary<string, List<Handler>> _handlers = new(StringComparer.Ordinal);

    /// User `fn`/`SF` declarations by name. An `fn` returns a value; an `SF` emits and returns null.
    private readonly Dictionary<string, IrFunction> _functions = new(StringComparer.Ordinal);

    /// Unwinds an `fn` body to its call site. Control flow, not an error — caught only in CallUser.
    private sealed class ReturnSignal : Exception
    {
        public readonly object? Value;
        public ReturnSignal(object? value) => Value = value;
    }

    private int _callDepth;
    private readonly Queue<(string Name, Dictionary<string, object?> Payload)> _queue = new();
    private readonly List<string> _log = new();
    private readonly VeinIdentityRegistry _registry = new();   // the Vein First-Class graph (nodes)
    private readonly VeinEntityRegistry _entities = new();     // ECS entity ids (second identity layer)
    private long _currentEntity;                               // the nearest entity in scope; 0 = none
    private readonly List<GraphEdge> _edges = new();           //   … and its emit edges
    private string _bundle = "";
    private Dictionary<string, IrType> _types = new(StringComparer.Ordinal);
    private int _eventSeq;
    private Dictionary<string, object?>? _current;   // the event currently being handled (for cause/trail)
    private string? _responseBody;
    private long _responseStatus = 200;
    private TextWriter? _out;   // console stdout sink (set in Run); @Print writes here. Null in Render.
    /// The reserved address of the root console — the process you launched, which no `@Console` spawns.
    /// Written `#Main` in source; PascalCase so a mark evaluates to it directly, with no boundary mapping.
    public const string RootConsole = "Main";

    private string _self = RootConsole;   // this console's address (for @Send routing) if not spawned.

    public sealed record GraphEdge(string FromName, string FromKind, string Event);
    public sealed record RenderResult(
        string? Body, long Status, IReadOnlyList<string> Log,
        IReadOnlyList<VeinFirstClass> Nodes, IReadOnlyList<GraphEdge> Edges);

    /// Web mode (one-shot): boot the program, drain the event loop, return the captured @Response.
    public RenderResult Render(IrModule module, string requestPath, IReadOnlyDictionary<string, object?>? inputs = null)
    {
        Setup(module);
        FireBoot(module, requestPath, inputs);
        Drain();
        return new RenderResult(_responseBody, _responseStatus, _log, _registry.All, _edges);
    }

    /// Console mode (interactive): boot the program, then pump stdin↔stdout — each line from `input`
    /// becomes an @Input event, each @Print event is written to `output`. Runs until EOF on `input`.
    public void Run(IrModule module, TextReader input, TextWriter output, bool messaging = false)
    {
        _out = output;
        _self = ConsoleLauncher.CurrentName ?? RootConsole;

        // If this process was spawned as a named console (`bring Console`), announce itself: title the
        // window and print its first line before booting.
        if (ConsoleLauncher.CurrentName is { } consoleName)
        {
            if (OperatingSystem.IsWindows()) { try { Console.Title = consoleName; } catch { } }
            if (Environment.GetEnvironmentVariable(ConsoleLauncher.FirstVar) is { Length: > 0 } first)
                _out.WriteLine(first);
        }

        Setup(module);
        FireBoot(module, "/", null);
        Drain();

        if (!messaging)
        {
            // Simple synchronous loop: stdin line → @Input → drain, until EOF.
            string? line;
            while ((line = input.ReadLine()) is not null) { FireInput(line); Drain(); }
            return;
        }

        // Messaging mode: accept input from stdin AND the console bus concurrently. ALL event-loop work
        // runs on this thread via the inbox (the interpreter stays logically single-threaded); the
        // background threads only post actions.
        using var inbox = new System.Collections.Concurrent.BlockingCollection<Action>();
        using var bus = ConsoleBus.Start(_self, (from, text) =>
        { try { inbox.Add(() => { FireMessage(from, text); Drain(); }); } catch { /* inbox closed */ } });

        var reader = new Thread(() =>
        {
            try { string? l; while ((l = input.ReadLine()) is not null) { var line = l; inbox.Add(() => { FireInput(line); Drain(); }); } }
            catch { /* input closed */ }
            // A root run (piped stdin) ends when stdin ends; a spawned console stays alive for messages.
            if (ConsoleLauncher.CurrentName is null) { try { inbox.CompleteAdding(); } catch { } }
        }) { IsBackground = true, Name = "vein-stdin" };
        reader.Start();

        try { foreach (var action in inbox.GetConsumingEnumerable()) action(); }
        catch { /* completed */ }
        bus.Stop();
    }

    /// Register every First-Class object (shard/view/bridge) and wire its `hear` handlers.
    private void Setup(IrModule module)
    {
        _bundle = module.Name;
        _types = module.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);

        // Module-level `fn`/`SF` declarations, callable by name from any body. Shard-local ones are
        // registered too — VeinScript has no nested scope for them, so one flat table matches lookup.
        foreach (var f in module.Functions) _functions[f.Name] = f;
        foreach (var shard in module.Shards)
            foreach (var m in shard.Methods)
                if (m.Attrs.Any(a => a.Name == "sf") || m.Return.Name != "void") _functions[m.Name] = m;
        foreach (var shard in module.Shards)
        {
            var kind = shard.Attrs.Any(a => a.Name == "view") ? VeinKind.ShardView
                     : shard.Attrs.Any(a => a.Name == "bridge") ? VeinKind.Bridge
                     : VeinKind.Shard;
            var (carriedShapes, carriedMarks) = AttrLists(shard.Attrs, "carries");
            var fc = _registry.Register(shard.Name, kind, carriedShapes, carriedMarks);
            var inst = new Instance { Name = shard.Name, Fc = fc };
            foreach (var f in shard.State) inst.State[f.Name] = Default(f.Type.Name);
            foreach (var m in shard.Methods)
            {
                var hear = m.Attrs.FirstOrDefault(a => a.Name == "hear");
                if (hear is null) continue;
                string ev = hear.Args.Count > 0 ? hear.Args[0]?.ToString() ?? "" : "";
                string bind = m.Params.Count > 0 ? m.Params[0].Name : "_";
                var (reqShapes, reqMarks) = AttrLists(m.Attrs, "audience");
                _handlers.TryAdd(ev, new List<Handler>());
                _handlers[ev].Add(new Handler(inst, bind, m.Body, reqShapes, reqMarks));
            }
        }
    }

    /// Fire the boot event: the declared `start` event + payload, else the default @Request { path }.
    private void FireBoot(IrModule module, string requestPath, IReadOnlyDictionary<string, object?>? inputs)
    {
        var reqFc = _registry.Register("request", VeinKind.Runtime);
        var bootInst = new Instance { Name = "boot", Fc = reqFc };
        var noLocals = new Dictionary<string, object?>();
        string bootEvent;
        var boot = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (module.Start is { } st)
        {
            bootEvent = st.Event;
            foreach (var (field, val) in st.Fields) boot[field] = Eval(val, bootInst, noLocals);
            // Fill remaining declared fields from their defaults (or a zero placeholder under `?`).
            if (_types.TryGetValue(bootEvent, out var et))
                foreach (var fld in et.Fields)
                {
                    if (fld.Name is "origin" or "source" || boot.ContainsKey(fld.Name)) continue;
                    if (fld.Default is not null) boot[fld.Name] = Eval(fld.Default, bootInst, noLocals);
                    else if (st.FillRest) boot[fld.Name] = ZeroVal(fld.Type.Name);
                }
        }
        else { bootEvent = "Request"; boot["path"] = requestPath; }

        // Run inputs override named payload fields (e.g. CLI --set path=/home). See docs/RUNTIME.md.
        if (inputs is not null) foreach (var kv in inputs) boot[kv.Key] = kv.Value;

        boot["from"] = FromOf(reqFc); boot["origin"] = null; boot["bundle"] = _bundle;
        Emit(bootEvent, boot);
    }

    /// Fire an @Input event carrying a line the user typed at the console.
    private void FireInput(string text)
    {
        var stdin = _registry.Register("stdin", VeinKind.Runtime);
        Emit("Input", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["text"] = text,
            ["from"] = FromOf(stdin), ["origin"] = null, ["bundle"] = _bundle
        });
    }

    /// A message that arrived from another console (via the bus) becomes an @Message event: `from` is the
    /// sending console's name, `text` the body.
    private void FireMessage(string from, string text)
    {
        Emit("Message", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["from"] = from, ["text"] = text, ["origin"] = null, ["bundle"] = _bundle
        });
    }

    /// Process the event queue until empty. @Response is captured; @Print (console mode) is written to
    /// the output sink; anything else dispatches to its `hear` handlers (subject to the audience barrier).
    private void Drain()
    {
        int guard = 0;
        while (_queue.Count > 0 && guard++ < 10_000)
        {
            var (name, payload) = _queue.Dequeue();
            _current = payload;   // emits inside handlers inherit this event's id into their trail
            if (name == "Response") { _responseBody = Str(payload.GetValueOrDefault("body")); _responseStatus = AsLong(payload.GetValueOrDefault("status")); continue; }
            if (name == "Print") { _out?.WriteLine(Str(payload.GetValueOrDefault("text"))); continue; }
            if (name == "Console") { ConsoleLauncher.Spawn(Str(payload.GetValueOrDefault("name")), Str(payload.GetValueOrDefault("firsttext"))); continue; }
            if (name == "Send") { ConsoleBus.Send(Str(payload.GetValueOrDefault("to")), _self, Str(payload.GetValueOrDefault("text"))); continue; }
            if (!_handlers.TryGetValue(name, out var hs)) continue;
            foreach (var h in hs)
            {
                if (!AudiencePermits(h, payload))
                {
                    var f = payload.GetValueOrDefault("from") as Dictionary<string, object?>;
                    _log.Add($"{h.Owner.Fc} BLOCKS @{name} (audience) from {f?.GetValueOrDefault("kind")}.{f?.GetValueOrDefault("name")}");
                    continue;
                }
                var locals = new Dictionary<string, object?> { [h.BindName] = payload };
                Exec(h.Body, h.Owner, locals);
            }
        }
    }

    /// A `from` value exposes the emitter's name/kind, its opaque identity (display only), and the
    /// shapes/marks it carries (so an `audience` barrier can filter on them).
    private static Dictionary<string, object?> FromOf(VeinFirstClass fc) => new(StringComparer.Ordinal)
    {
        ["name"] = fc.Name, ["kind"] = fc.Kind.ToString(), ["identity"] = fc.Identity.ToString(),
        ["shapes"] = fc.CarriedShapes.Cast<object?>().ToList(),
        ["marks"] = fc.CarriedMarks.Cast<object?>().ToList()
    };

    /// Read a two-list attribute (e.g. @audience(shapes, marks) / @carries(shapes, marks)).
    private static (IReadOnlyList<string> Shapes, IReadOnlyList<string> Marks) AttrLists(IReadOnlyList<IrAttr> attrs, string name)
    {
        var a = attrs.FirstOrDefault(x => x.Name == name);
        if (a is null || a.Args.Count < 2) return (Array.Empty<string>(), Array.Empty<string>());
        return (a.Args[0] as IReadOnlyList<string> ?? Array.Empty<string>(),
                a.Args[1] as IReadOnlyList<string> ?? Array.Empty<string>());
    }

    /// The audience barrier: the emitter must carry ALL required shapes/marks.
    private static bool AudiencePermits(Handler h, Dictionary<string, object?> payload)
    {
        if (h.ReqShapes.Count == 0 && h.ReqMarks.Count == 0) return true;
        var from = payload.GetValueOrDefault("from") as Dictionary<string, object?>;
        var haveShapes = (from?.GetValueOrDefault("shapes") as IEnumerable<object?>)?.Select(Str).ToHashSet() ?? new();
        var haveMarks = (from?.GetValueOrDefault("marks") as IEnumerable<object?>)?.Select(Str).ToHashSet() ?? new();
        return h.ReqShapes.All(haveShapes.Contains) && h.ReqMarks.All(haveMarks.Contains);
    }

    private void Emit(string ev, Dictionary<string, object?> payload)
    {
        // Every event gets its own id (not an entity id) and a causation chain.
        payload["id"] = "ev" + (++_eventSeq);
        if (_current is not null)
        {
            payload["cause"] = _current.TryGetValue("id", out var cid) ? cid : "";
            var trail = new List<object?>();
            if (_current.TryGetValue("trail", out var t) && t is List<object?> prev) trail.AddRange(prev);
            if (_current.TryGetValue("id", out var pid)) trail.Add(pid);
            payload["trail"] = trail;                 // id[] — the whole chain that led here
        }
        else { payload["cause"] = ""; payload["trail"] = new List<object?>(); }
        _queue.Enqueue((ev, payload));
    }

    // ---- statements -----------------------------------------------------

    private void Exec(IrBlock block, Instance self, Dictionary<string, object?> locals)
    {
        foreach (var s in block.Statements) ExecStmt(s, self, locals);
    }

    private void ExecStmt(IrStmt s, Instance self, Dictionary<string, object?> locals)
    {
        switch (s)
        {
            case IrBlock b: Exec(b, self, locals); break;
            case IrLet l: locals[l.Name] = l.Init is null ? null : Eval(l.Init, self, locals); break;
            case IrAssign a: DoAssign(a, self, locals); break;
            case IrIf i:
                if (Truthy(Eval(i.Cond, self, locals))) Exec(i.Then, self, locals);
                else if (i.Else is not null) Exec(i.Else, self, locals);
                break;
            case IrLoop lp: ExecLoop(lp, self, locals); break;
            case IrExprStmt e: Eval(e.Expr, self, locals); break;
            // Unwinds to the enclosing CallUser. The parser only admits `return` inside an `fn`, so a
            // stray signal cannot escape into the event loop.
            case IrReturn r: throw new ReturnSignal(r.Value is null ? null : Eval(r.Value, self, locals));
            // break/continue/match/target not exercised by the render path yet
            default: break;
        }
    }

    private void ExecLoop(IrLoop lp, Instance self, Dictionary<string, object?> locals)
    {
        switch (lp.Kind)
        {
            case IrLoopKind.While:
                { int g = 0; while (Truthy(Eval(lp.Cond!, self, locals)) && g++ < 100_000) Exec(lp.Body, self, locals); }
                break;
            case IrLoopKind.Repeat:
                { long n = AsLong(Eval(lp.Count!, self, locals));
                  for (long i = 0; i < n; i++) { if (lp.Var is not null) locals[lp.Var] = i; Exec(lp.Body, self, locals); } }
                break;
            default: break; // Target (identity query) needs the full runtime
        }
    }

    private void DoAssign(IrAssign a, Instance self, Dictionary<string, object?> locals)
    {
        var value = Eval(a.Value, self, locals);
        if (a.Target is IrLocalRef r)
        {
            if (self.State.ContainsKey(r.Name)) self.State[r.Name] = value;
            else locals[r.Name] = value;
        }
        // field assignment (component) not modeled in this cut
    }

    // ---- expressions ----------------------------------------------------

    private object? Eval(IrExpr e, Instance self, Dictionary<string, object?> locals)
    {
        switch (e)
        {
            case IrLiteral l: return l.Value;
            case IrLocalRef r:
                if (locals.TryGetValue(r.Name, out var lv)) return lv;
                if (self.State.TryGetValue(r.Name, out var sv)) return sv;
                // A bare `self` used as a value = the nearest entity's id (aligns with `Entity`).
                if (r.Name == "self") return _currentEntity;
                return null;
            case IrSelfRef: return self.Name;
            case IrEntityRef: return _currentEntity;   // `Entity` — nearest entity's int id (0 = none)
            case IrFieldAccess f:
            {
                var recv = Eval(f.Receiver, self, locals);
                if (recv is IDictionary<string, object?> d) return d.TryGetValue(f.Field, out var fv) ? fv : null;
                return null;
            }
            case IrBinary b: return EvalBinary(b, self, locals);
            case IrUnary u:
            {
                var v = Eval(u.Operand, self, locals);
                return u.Op == IrUnOp.Not ? !Truthy(v) : -AsDouble(v);
            }
            case IrRuntimeCall rc: return EvalRuntime(rc, self, locals);
            case IrList li: return li.Items.Select(i => Eval(i, self, locals)).ToList();
            case IrCall call:
                if (call.Callee is IrLocalRef fn)
                {
                    var args = call.Args.Select(a => Eval(a, self, locals)).ToList();
                    // A user `fn`/`SF` shadows nothing built-in: look it up first, then the prebuilts.
                    return _functions.TryGetValue(fn.Name, out var decl)
                        ? CallUser(decl, args, self)
                        : Prebuilt(fn.Name, args);
                }
                return null;
            case IrStructInit si:
            {
                var d = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var (field, val) in si.Fields) d[field] = Eval(val, self, locals);
                return d;
            }
            case IrTypeNameExpr t: return t.Name;
            default: return null;
        }
    }

    /// Invoke a user `fn` (returns its value) or `SF` (emits; yields null). Params bind positionally into
    /// a fresh local scope — a function never sees its caller's locals.
    private object? CallUser(IrFunction f, List<object?> args, Instance self)
    {
        if (_callDepth >= 256) return null;   // runaway recursion: stop rather than blow the stack
        _callDepth++;
        try
        {
            var scope = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int i = 0; i < f.Params.Count; i++)
                scope[f.Params[i].Name] = i < args.Count ? args[i] : null;

            try { Exec(f.Body, self, scope); }
            catch (ReturnSignal r) { return r.Value; }
            return null;                       // fell off the end (an SF, or an fn with no return)
        }
        finally { _callDepth--; }
    }

    /// Prebuilt (built-in) functions that DO return a value — the only functions that return.
    private static object? Prebuilt(string name, List<object?> args) => name switch
    {
        "len" => (long)(args.Count > 0 ? args[0] switch { System.Collections.ICollection c => c.Count, string s => s.Length, _ => 0 } : 0),
        "random" => Random.Shared.NextDouble(),
        "join" => args.Count > 1 && args[0] is System.Collections.IEnumerable e ? string.Join(Str(args[1]), e.Cast<object?>().Select(Str)) : "",
        _ => null
    };

    private object? EvalRuntime(IrRuntimeCall rc, Instance self, Dictionary<string, object?> locals)
    {
        switch (rc.Name)
        {
            case "random": return Random.Shared.NextDouble();
            case "Emit":
                if (rc.Args.Count == 1 && rc.Args[0] is IrStructInit si)
                {
                    var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var (field, val) in si.Fields) payload[field] = Eval(val, self, locals);
                    // Fill omitted payload fields: a field's default, else the current context (the
                    // event being handled, then locals) by matching name. Explicit fields win.
                    if (_types.TryGetValue(si.TypeName, out var et))
                        foreach (var fld in et.Fields)
                        {
                            if (fld.Name is "origin" or "source" || payload.ContainsKey(fld.Name)) continue;
                            if (fld.Default is not null) payload[fld.Name] = Eval(fld.Default, self, locals);
                            else if (_current is not null && _current.TryGetValue(fld.Name, out var cv)) payload[fld.Name] = cv;
                            else if (locals.TryGetValue(fld.Name, out var lv)) payload[fld.Name] = lv;
                            else if (si.FillRest) payload[fld.Name] = ZeroVal(fld.Type.Name);   // `?` placeholder
                        }
                    // Auto provenance. Two layers kept separate:
                    //   from   = the First-Class object that emitted (runtime identity)
                    //   origin = the ECS entity, when emitted in an entity context (none here yet)
                    payload["from"] = FromOf(self.Fc);
                    payload["origin"] = null;
                    payload["bundle"] = _bundle;
                    _edges.Add(new GraphEdge(self.Fc.Name, self.Fc.Kind.ToString(), si.TypeName));
                    _log.Add($"{self.Fc} [{self.Fc.Identity}] emits @{si.TypeName}");
                    Emit(si.TypeName, payload);
                }
                return null;
            default: return null;   // AddTag/DestroyEntity/etc. need the identity runtime
        }
    }

    private object? EvalBinary(IrBinary b, Instance self, Dictionary<string, object?> locals)
    {
        if (b.Op == IrBinOp.And) return Truthy(Eval(b.Left, self, locals)) && Truthy(Eval(b.Right, self, locals));
        if (b.Op == IrBinOp.Or) return Truthy(Eval(b.Left, self, locals)) || Truthy(Eval(b.Right, self, locals));

        var l = Eval(b.Left, self, locals);
        var r = Eval(b.Right, self, locals);
        switch (b.Op)
        {
            case IrBinOp.Add:
                if (l is string || r is string) return Str(l) + Str(r);   // string concat (prebuilt +)
                return AsDouble(l) + AsDouble(r) is var sum && IsInt(l) && IsInt(r) ? (object)(long)sum : sum;
            case IrBinOp.Sub: return Num(AsDouble(l) - AsDouble(r), l, r);
            case IrBinOp.Mul: return Num(AsDouble(l) * AsDouble(r), l, r);
            case IrBinOp.Div: return Num(AsDouble(l) / AsDouble(r), l, r);
            case IrBinOp.Mod: return (long)AsDouble(l) % (long)AsDouble(r);
            case IrBinOp.Eq: return Equals(Str(l), Str(r)) || AsDouble(l) == AsDouble(r);
            case IrBinOp.Ne: return !(Equals(Str(l), Str(r)) || AsDouble(l) == AsDouble(r));
            case IrBinOp.Lt: return AsDouble(l) < AsDouble(r);
            case IrBinOp.Gt: return AsDouble(l) > AsDouble(r);
            case IrBinOp.Le: return AsDouble(l) <= AsDouble(r);
            case IrBinOp.Ge: return AsDouble(l) >= AsDouble(r);
            default: return null;
        }
    }

    // ---- helpers --------------------------------------------------------

    private static object Num(double v, object? l, object? r) => IsInt(l) && IsInt(r) ? (long)v : v;
    private static bool IsInt(object? o) => o is long or int;
    private static bool Truthy(object? o) => o switch { null => false, bool b => b, long n => n != 0, double d => d != 0, string s => s.Length > 0, _ => true };
    private static long AsLong(object? o) => o switch { long l => l, int i => i, double d => (long)d, _ => 0 };
    private static double AsDouble(object? o) => o switch { double d => d, long l => l, int i => i, bool b => b ? 1 : 0, _ => 0 };
    private static string Str(object? o) => o switch { null => "", string s => s, double d => d.ToString(CultureInfo.InvariantCulture), bool b => b ? "true" : "false", _ => o.ToString() ?? "" };
    // Entity scope plumbing. The target/tick runtime (follow-on) brackets each targeted entity with
    // Enter/Leave so `Entity` and a bare `self` resolve to that entity's freshly-allocated id. Until
    // that runtime lands, no entity scope is entered, so `_currentEntity` stays 0 ("no entity").
    private long EnterEntity() => _currentEntity = _entities.Allocate();
    private void LeaveEntity(long previous) => _currentEntity = previous;

    private static object? Default(string typeName) => typeName switch { "string" or "Mark" => "", "int" => 0L, "float" => 0.0, "bool" => false, _ => null };

    // Typed zero used to satisfy required fields under `?` (fill-the-rest). A `Mark` (an identity
    // reference — e.g. a console address) has no meaningful zero, so it names the reserved root.
    private static object? ZeroVal(string typeName) => typeName switch
    {
        "int" or "Entity" => 0L, "float" => 0.0, "bool" => false, "percent" => 0.0,
        "Mark" => RootConsole, "string" => "", _ => ""
    };
}
