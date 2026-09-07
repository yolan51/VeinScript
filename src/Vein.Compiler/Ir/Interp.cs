using System.Globalization;
using System.IO;

namespace Vein.Compiler.Ir;

/// The interpreter over the HIR. It runs BOTH of the language's loops (docs/RUNTIME.md):
///
///   * the reactive one — fire an event, let shards/ShardViews `hear` it and `emit` more, collect the
///     final @Response body;
///   * the identity one — `run once` / `each tick` / `settled` over the entities in [EntityStore],
///     with `folds` reconciling the writes that several shards make to one field in a frame.
///
/// The two meet at the frame boundary: events emitted by a schedule block drain once the frame's
/// contributions have been folded, so no handler ever sees a half-updated field.
///
/// The clock is explicit (`Ticks`) rather than wall-clock, and `random` is seeded, so any run is
/// reproducible — which is what makes a golden run test possible at all.
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

    /// A behaviour block the CLOCK drives rather than an event: `once`/`tick`/`frame`/`settled`/`every`.
    /// `Seconds` is meaningful only for `every N`; the frame-counted kinds leave it 0.
    private sealed record Schedule(Instance Owner, string Kind, double Seconds, IrBlock Body);

    private readonly List<Schedule> _schedules = new();

    /// User `fn`/`SF` declarations by name. An `fn` returns a value; an `SF` emits and returns null.
    private readonly Dictionary<string, IrFunction> _functions = new(StringComparer.Ordinal);

    /// Unwinds an `fn` body to its call site. Control flow, not an error — caught only in CallUser.
    private sealed class ReturnSignal : Exception
    {
        public readonly object? Value;
        public ReturnSignal(object? value) => Value = value;
    }

    /// `break` and `continue`, unwound to the nearest loop the same way `return` unwinds to its call.
    ///
    /// THESE DID NOTHING AT ALL until they were added here. They lex, they parse, they lower to IrBreak
    /// and IrContinue, and the C# backend emits real `break;`/`continue;` — but this switch had no case
    /// for either, so the interpreter ran straight past them and finished the loop. The two runtimes
    /// disagreed on any program that used them, and nothing caught it because no sample did.
    ///
    /// An exception for control flow is the same trade `return` already makes: threading a status
    /// through every Exec, ExecStmt and ExecLoop would touch every statement in the interpreter to
    /// serve two keywords. They carry no payload and no stack trace is ever read, so one instance of
    /// each is reused rather than allocated per hop.
    private sealed class BreakSignal : Exception
    {
        public static readonly BreakSignal It = new();
    }

    private sealed class ContinueSignal : Exception
    {
        public static readonly ContinueSignal It = new();
    }

    private int _callDepth;
    private readonly Queue<(string Name, Dictionary<string, object?> Payload)> _queue = new();
    private readonly List<string> _log = new();
    private readonly VeinIdentityRegistry _registry = new();   // the Vein First-Class graph (nodes)
    private readonly List<GraphEdge> _edges = new();           //   … and its emit edges
    private readonly EntityStore _store = new();               // entities, components, marks, folds
    private long _currentEntity;                               // the nearest entity in scope; 0 = none

    /// `Index` — the nearest enclosing loop's 0-based iteration counter, saved and restored exactly as
    /// `_currentEntity` is, so a nested loop shadows and then hands back. -1 outside any loop.
    private long _currentIndex = -1;

    /// The values bound by enclosing `target … as <bind>` statements. IrSelfRef means "the innermost
    /// one" — an entity id for a `target $Shape #Mark` query, an arbitrary element for `target <expr>`.
    private readonly List<object?> _targetBinds = new();

    /// Deferred structural changes. mark/attach/destroy queue here and are applied at Commit, AFTER fold
    /// resolution, so one phase has exactly one point where the world changes.
    private readonly List<Action> _commands = new();

    /// One `bring` put aside by an `ordered by`, with everything needed to run it somewhere else later.
    ///
    /// It carries FOUR things because a loop binding lives in four places, and a deferred body that
    /// restored only some of them read blanks: `Locals` for a named binding, `Binds` because `target …
    /// as row` also pushes onto the nameless bind stack that IrSelfRef reads (docs/RULES.md 12c),
    /// `Index` for the loop counter, and `Entity` for an identity query's current entity. All are
    /// COPIES: the loop keeps reassigning them, so a reference would leave every deferred body looking
    /// at the last row.
    private sealed record Deferred(
        object? Key, IrBlock Body, Dictionary<string, object?> Locals,
        List<object?> Binds, long Index, long Entity);

    /// The `ordered by` blocks currently collecting. A stack because they nest — the inner one takes the
    /// brings, and the outer keeps whatever it had already gathered.
    private readonly Stack<List<Deferred>> _orderFrames = new();

    /// Seeded so a program using `chance` is reproducible; without this there is no golden run test.
    private readonly Random _rng = new(0);

    /// The world, for tests that want to assert on state rather than on stdout.
    public EntityStore World => _store;

    /// The writer `@Print` actually goes to. Readable because "which writer did we keep" is precisely
    /// the thing that broke once: switching the console encoding REPLACES Console.Out, so a writer
    /// captured beforehand keeps encoding in the old code page while the property reports the new one.
    ///
    /// SETTABLE FOR A HOST THAT DRIVES `Boot`/`Frame` ITSELF. `Run` assigns this and always did, so a
    /// console program is unchanged — but the stepping path never touched it, and `@Print` writes
    /// through `_out?.WriteLine`, so on that path every print went to a null and vanished. Not dropped
    /// loudly: dropped by a `?.`, with no diagnostic and nothing to grep for. An editor that boots a
    /// program and steps it got a working world and a silent console.
    public TextWriter? Output { get => _out; set => _out = value; }

    /// Frames of the ECS clock to advance after boot. 0 — the default — is a purely reactive program:
    /// nothing drives `each tick`, so a bundle with no schedule blocks behaves exactly as it always has.
    /// There is no wall clock here on purpose; a run must be reproducible. See `veinc run --ticks N`.
    public int Ticks { get; set; }

    private string _bundle = "";
    /// Shapes by name. `IsComponent` is the only thing that asks, and it asks about a component.
    private Dictionary<string, IrType> _components = new(StringComparer.Ordinal);

    /// Event payloads by name — the boot directive and every `emit` fill omitted fields from these.
    private Dictionary<string, IrType> _events = new(StringComparer.Ordinal);
    private int _eventSeq;
    private Dictionary<string, object?>? _current;   // the event currently being handled (for cause/trail)
    private string? _responseBody;
    private long _responseStatus = 200;
    private TextWriter? _out;   // console stdout sink (set in Run); @Print writes here. Null in Render.
    /// The reserved address of the root console — the process you launched, which no `@Console` spawns.
    /// Written `#Main` in source; PascalCase so a mark evaluates to it directly, with no boundary mapping.
    public const string RootConsole = "Main";

    private string _self = RootConsole;   // this console's address (for @Send routing) if not spawned.

    /// The network listener, once `@Listen` has opened one. Null in a program that never listens — which
    /// includes every program that only LINKS peers, since sending needs no port of its own.
    private NetBus? _net;

    /// The live session's work queue, when there is one. A background result (an HTTP reply) is posted
    /// here rather than applied where it completed, which is what keeps the interpreter single-threaded.
    /// Null in one-shot modes (`render`, `serve`), where the same work runs inline instead — see Fetch.
    private System.Collections.Concurrent.BlockingCollection<Action>? _inbox;

    public sealed record GraphEdge(string FromName, string FromKind, string Event);
    public sealed record RenderResult(
        string? Body, long Status, IReadOnlyList<string> Log,
        IReadOnlyList<VeinFirstClass> Nodes, IReadOnlyList<GraphEdge> Edges);

    /// Web mode (one-shot): boot the program, drain the event loop, return the captured @Response.
    public RenderResult Render(IrModule module, string requestPath, IReadOnlyDictionary<string, object?>? inputs = null)
    {
        Setup(module);
        RunOnce();
        FireBoot(module, requestPath, inputs);
        Drain();
        RunFrames();
        return new RenderResult(_responseBody, _responseStatus, _log, _registry.All, _edges);
    }

    /// Console mode (interactive): boot the program, then pump stdin↔stdout — each line from `input`
    /// becomes an @Input event, each @Print event is written to `output`. Runs until EOF on `input`.
    public void Run(IrModule module, TextReader input, TextWriter output, bool messaging = false)
    {
        // Switch the console to UTF-8 BEFORE capturing the writer, and take a fresh one afterwards.
        // Setting Console.OutputEncoding REPLACES Console.Out with a writer bound to the new encoding —
        // a StreamWriter's encoder is fixed when it is built, so a reference grabbed beforehand keeps
        // encoding in the old code page no matter what the property now says. Only when we really are
        // driving a console: a test's StringWriter or an embedding host is none of our business.
        if (ReferenceEquals(output, Console.Out))
        {
            bool fromConsole = ReferenceEquals(input, Console.In);
            ConsoleLauncher.UseUtf8();
            output = Console.Out;
            if (fromConsole) input = Console.In;
        }

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

        if (!messaging)
        {
            Setup(module);
            RunOnce();
            FireBoot(module, "/", null);
            Drain();
            RunFrames();
            // Simple synchronous loop: stdin line → @Input → drain, until EOF.
            string? line;
            while ((line = input.ReadLine()) is not null) { FireInput(line); Drain(); }
            return;
        }

        // Messaging mode: accept input from stdin AND the console bus concurrently. ALL event-loop work
        // runs on this thread via the inbox (the interpreter stays logically single-threaded); the
        // background threads only post actions.
        //
        // The inbox exists BEFORE the program boots, because `@Listen` almost always sits in `run once`:
        // booting first would open the port while there was still nowhere to post to, and the first peer
        // to connect would then be handled on the accept thread — mutating the world from outside the
        // loop the whole design exists to keep single-threaded.
        using var inbox = new System.Collections.Concurrent.BlockingCollection<Action>();
        _inbox = inbox;

        Setup(module);
        RunOnce();
        FireBoot(module, "/", null);
        Drain();
        RunFrames();

        using var bus = ConsoleBus.Start(_self, (from, text) =>
        { try { inbox.Add(() => Receive(from, text)); } catch { /* inbox closed */ } });

        StartEveryTimers(inbox);
        StartFrameTimer(inbox);

        // A spawned console stays alive for messages rather than ending on stdin EOF — but only for as
        // long as there is someone to receive them from. When the program that spawned it exits, this
        // ends the wait too; otherwise a closed window leaves a process nobody can see, still holding the
        // binaries the next build must overwrite.
        ConsoleLauncher.WatchParent(() => { try { inbox.CompleteAdding(); } catch { } });

        var reader = new Thread(() =>
        {
            try { string? l; while ((l = input.ReadLine()) is not null) { var line = l; inbox.Add(() => { FireInput(line); Drain(); }); } }
            catch { /* input closed */ }

            // A root run ends when stdin ends — `veinc run < file` should not hang. Two things keep it
            // alive instead, and both are "there is still someone to hear from":
            //
            //   * a spawned console, which exists to receive from the program that spawned it;
            //   * a program LISTENING on the network, which is the whole shape of a server.
            //
            // Without the second, a built server deployed headless dies instantly: systemd gives it no
            // stdin, EOF arrives before the first client can connect, and the port closes a moment after
            // it opened. It looks like a crash on startup and is not.
            if (ConsoleLauncher.CurrentName is null && _net is null)
            { try { inbox.CompleteAdding(); } catch { } }
        }) { IsBackground = true, Name = "vein-stdin" };
        reader.Start();

        // Each action is guarded SEPARATELY. A single throwing event must not take the console with it:
        // an outer try would end the loop on the first failure and leave a window that is still open but
        // permanently deaf — the worst possible failure mode for a long-running console.
        try
        {
            foreach (var action in inbox.GetConsumingEnumerable())
            {
                try { action(); }
                catch (Exception ex) { _out?.WriteLine($"(runtime error, continuing: {ex.Message})"); }
            }
        }
        catch { /* the inbox completed — the run is over */ }
        bus.Stop();
        _net?.Stop();   // release the port; a listener left bound outlives the run that opened it
        _inbox = null;
    }

    /// Register every First-Class object (shard/view/bridge) and wire its `hear` handlers.
    private void Setup(IrModule module)
    {
        _bundle = module.Name;
        // ONE TABLE PER SIGIL, because a name means a different thing under each of them: `$Switch` is a
        // shape, `#Switch` a mark, `@Switch` an event, `&Switch` a builder, and a program may declare
        // all four. Tags were already excluded here for exactly that reason — but Components and
        // Messages still shared one map, so `shape $Switch` beside `event @Switch` threw
        // `An item with the same key has already been added` out of ToDictionary and took the
        // interpreter down before the program ran a line. A raw ArgumentException, on legal source.
        //
        // Splitting is what the call sites wanted anyway: `IsComponent` asks about a component, and the
        // boot and emit payload lookups ask about an event. Neither was ever served by "whichever type
        // holds this name". Marks need no table — they live in the store's own sets — and builders are
        // gone by this point, desugared into spawn/attach/mark by Lower.
        _components = module.Types.Where(t => t.Kind == IrTypeKind.Component)
                                  .ToDictionary(t => t.Name, StringComparer.Ordinal);
        _events = module.Types.Where(t => t.Kind == IrTypeKind.Message)
                              .ToDictionary(t => t.Name, StringComparer.Ordinal);
        _store.Declare(module.Types);   // field defaults + each field's fold reducer

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
                // A schedule block (`run once` / `each tick` / `settled` / …) is a body the CLOCK calls,
                // not the event loop; it takes no parameters and hears nothing.
                if (m.Attrs.FirstOrDefault(a => a.Name == "schedule") is { } sch)
                {
                    string when = sch.Args.Count > 0 ? sch.Args[0]?.ToString() ?? "" : "";
                    double seconds = sch.Args.Count > 1 && sch.Args[1] is double d ? d : 0;
                    _schedules.Add(new Schedule(inst, when, seconds, m.Body));
                    continue;
                }

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
            if (_events.TryGetValue(bootEvent, out var et))
                foreach (var fld in et.Fields)
                {
                    if (fld.Name is "origin" or "source" || boot.ContainsKey(fld.Name)) continue;
                    if (fld.Default is not null) boot[fld.Name] = Eval(fld.Default, bootInst, noLocals);
                    else if (st.FillRest) boot[fld.Name] = ZeroVal(fld.Type.Name);
                }
        }
        else
        {
            // The built-in boot event when a bundle declares no `start`. It mirrors the stdlib's
            // @Request declaration rather than only its path: a handler reading `r.method` must see
            // "GET" on a one-shot render, not an empty string, or every routing check has to special-case
            // the case where nobody told it the method.
            bootEvent = "Request";
            boot["path"] = requestPath;
            boot["method"] = "GET";
            boot["body"] = "";
        }

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

    /// Deliver a line that arrived from another console, and run the handlers it wakes. The bus calls
    /// this; so can a host (or a test) that owns its own transport.
    public void Receive(string from, string text) { FireMessage(from, text); Drain(); }

    // ---- device input, from a host that owns a window -------------------
    //
    // `Vein.Input`'s occurrences are pure DATA — a key went down, a pointer moved — and the interpreter
    // already delivers them: `samples/stdlib_events.vein` emits them from VeinScript and its `hear`
    // blocks fire. What was missing is a way for a HOST to produce one. An editor with a native window
    // can see a key press and had no method to turn it into `@KeyDown` short of reflecting into the
    // private queue and inventing the provenance envelope itself — which would be a second event
    // transport, free to drift from the one `veinc run` uses.
    //
    // SPECIFIC ENTRY POINTS, NOT A GENERIC `Emit`. A host that could emit anything could emit `@Fetch`
    // or `@ReadFile` — events whose meaning is a real HTTP request and a real file read, implemented by
    // this interpreter. A host-fabricated one would look identical to a program and do nothing, which
    // is the failure `docs/SAMPLES.md` §3 explains at length. Six named methods cannot make that
    // mistake; one `Emit(string, payload)` invites it.
    //
    // Each drains, so a handler runs before the call returns and the world a host renders afterwards is
    // settled. That matches `Receive` above rather than inventing a second delivery rule.

    /// `@KeyDown { key }` — a key went down.
    public void FireKeyDown(string key) => FireDevice("KeyDown", ("key", key));

    /// `@KeyUp { key }` — a key came up.
    public void FireKeyUp(string key) => FireDevice("KeyUp", ("key", key));

    /// `@TextInput { text }` — composed text, not raw key codes. Stdlib says so, and it is the
    /// difference between typing into a field and reading a keyboard.
    public void FireTextInput(string text) => FireDevice("TextInput", ("text", text));

    /// `@MouseDown { x, y, button }` — the position fields come from `*Vein.Math.Values.$Vec2`, which
    /// the event includes, so they are flat `x`/`y` here exactly as a `hear` block reads them.
    public void FireMouseDown(double x, double y, int button) =>
        FireDevice("MouseDown", ("x", x), ("y", y), ("button", (long)button));

    /// `@MouseUp { x, y, button }`.
    public void FireMouseUp(double x, double y, int button) =>
        FireDevice("MouseUp", ("x", x), ("y", y), ("button", (long)button));

    /// `@MouseMove { x, y }`.
    public void FireMouseMove(double x, double y) => FireDevice("MouseMove", ("x", x), ("y", y));

    /// `*Vein.Time.Clock.@Ticked { frame, delta }` — the clock, from a host that owns one.
    ///
    /// stdlib/Time.vein settles whose job this is: "a runtime emits @Ticked when appropriate (a game
    /// every frame, an app on a timer)". `@Ticked` is pure data — `HostEvents` above names it as "an
    /// occurrence and nothing more" — so a program hearing it compiled and ran and heard nothing, with
    /// no error anywhere, because the six entry points above were keyboard and mouse only and the
    /// helper they share is private. The host was doing the `$Clock` half through `World.Write` and had
    /// no way to do this half at all.
    public void FireTicked(int frame, double delta) =>
        FireDevice("Ticked", ("frame", (long)frame), ("delta", delta));

    /// One device occurrence, with the provenance envelope every event carries.
    ///
    /// The sender is a registered runtime identity named `device`, the way stdin is registered as
    /// `stdin` — so `d.from.name` tells a program the key came from the host rather than from another
    /// bundle emitting it, and the audience barrier has something real to test.
    private void FireDevice(string name, params (string Field, object? Value)[] fields)
    {
        var device = _registry.Register("device", VeinKind.Runtime);

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["from"] = FromOf(device), ["origin"] = null, ["bundle"] = _bundle
        };
        foreach (var (field, value) in fields) payload[field] = value;

        Emit(name, payload);
        Drain();
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

    // ---- the network ----------------------------------------------------

    /// `@Listen { as: #Me, at: 9700, key: "…" }` — bind a port and take a NETWORK identity.
    ///
    /// `as` matters more than it looks. On the pipe bus every unnamed root is `Main`, which is harmless
    /// while the namespace is one machine. Across machines it is not: every peer would answer to the same
    /// mark. So a listening program says who it is, and that name is what its frames are signed as.
    private void DoListen(Dictionary<string, object?> payload)
    {
        string key = Str(payload.GetValueOrDefault("key"));
        long port = AsLong(payload.GetValueOrDefault("at"));
        string self = Str(payload.GetValueOrDefault("as"));

        // Refusing an empty key is the one hard stop in the whole bundle. A listener without one accepts
        // any frame that reaches the port, and `audience` — which the language sells as the networking
        // barrier — would silently become decoration. Better a loud no than a quiet lie.
        if (key.Length == 0) { Fail("Listen", "@Listen needs a key — an unauthenticated port would make `audience` meaningless"); return; }
        if (_net is not null) { Fail("Listen", "already listening"); return; }

        if (self.Length > 0) _self = self;
        try
        {
            _net = NetBus.Start(_self, (int)port, key, (from, text) =>
            {
                // Exactly like an arriving console message: the transport only POSTS, and the event loop
                // owns every mutation. A live session has an inbox; a one-shot run handles it inline.
                if (_inbox is { } box) { try { box.Add(() => Receive(from, text)); } catch { } }
                else Receive(from, text);
            });
            _log.Add($"listening as {_self} on port {NetBus.SelfPort}");
        }
        catch (Exception ex) { Fail("Listen", ex.Message); }
    }

    /// `@Link { name: #Peer, at: "host:port", key: "…" }` — teach this process how to reach a peer.
    /// Registers a route only; nothing connects until something is actually sent, so linking a machine
    /// that is not up yet is fine and ordinary.
    private void DoLink(Dictionary<string, object?> payload)
    {
        string name = Str(payload.GetValueOrDefault("name"));
        string at = Str(payload.GetValueOrDefault("at"));
        string key = Str(payload.GetValueOrDefault("key"));
        if (key.Length == 0) { Fail("Link", "@Link needs a key — the peer will refuse an unsigned frame"); return; }
        if (!NetBus.Link(name, at, key)) { Fail("Link", $"cannot parse address \"{at}\" for {name}"); return; }
        _log.Add($"linked {name} at {at}");
    }

    /// `@Fetch { url, method, body, headers }` — the one asymmetric call in Vein.Net, answered by @Fetched (the
    /// service replied, whatever the status) or @Failed (it never replied at all).
    ///
    /// Where the waiting happens depends on who owns the clock. A live console must not block its event
    /// loop for 30 seconds, so the request goes to a worker that posts the result back. A one-shot
    /// `render`/`serve` has no inbox and no loop to protect, and its whole job is to produce ONE response,
    /// so it waits inline — which also keeps a rendered page reproducible.
    private void DoFetch(Dictionary<string, object?> payload)
    {
        string url = Str(payload.GetValueOrDefault("url"));
        string method = Str(payload.GetValueOrDefault("method"));
        string body = Str(payload.GetValueOrDefault("body"));
        // Absent in every program written before headers existed, and `GetValueOrDefault` makes that the
        // empty string rather than a crash — so `@Fetch { url, method, body }` still means what it did.
        string headers = Str(payload.GetValueOrDefault("headers"));

        if (_inbox is not { } box) { Deliver(url, NetHttp.Fetch(url, method, body, headers)); return; }

        new Thread(() =>
        {
            var result = NetHttp.Fetch(url, method, body, headers);
            try { box.Add(() => { Deliver(url, result); Drain(); }); } catch { /* the run ended */ }
        })
        { IsBackground = true, Name = "vein-fetch" }.Start();
    }

    // ---- files ------------------------------------------------------------
    //
    // Same shape as @Fetch, and for the same reason: the request is an event and so is the answer, so a
    // failure is a MESSAGE the program hears rather than an exception that ends the run. That is the
    // language's failure idiom — @Failed for a request, @Undelivered for a send, @FileMissing here.
    //
    // SYNCHRONOUS, unlike @Fetch. Fetch threads because a network round trip is slow enough that
    // blocking the event loop would be felt; a local file is not, and a thread per read would buy
    // nothing while making the order of two reads unpredictable. The answer is queued rather than
    // delivered inline, so it is heard in the same drain as any other event.
    //
    // NO SANDBOX. A path is a path, and the program runs with the permissions you started it with. This
    // is the same trust `veinc run` already implies, but it is worth naming: nothing here stops a
    // program reading outside its folder.

    private void DoReadFile(Dictionary<string, object?> payload)
    {
        string path = Str(payload.GetValueOrDefault("path"));

        // The tag rides the REQUEST through to the ANSWER untouched, so a program with many files routes
        // on what the read was FOR rather than on where it lived. The runtime never reads it.
        string tag = Str(payload.GetValueOrDefault("tag"));

        try
        {
            string text = File.ReadAllText(path);
            Emit("FileLoaded", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = path,
                ["text"] = text,
                ["bytes"] = (long)text.Length,
                ["tag"] = tag
            });
        }
        catch (Exception ex)
        {
            // Missing, locked, no permission, a directory — all one event with the reason, because a
            // program that wants to know WHICH can read the reason, and one that does not can hear the
            // failure without a second handler per cause. The tag comes back here too: a failure has to
            // be routable by the same match that routes the success, or nineteen files means nineteen
            // more path comparisons in the error path.
            Emit("FileMissing", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = path,
                ["reason"] = ex.Message,
                ["tag"] = tag
            });
        }
    }

    private void DoWriteFile(Dictionary<string, object?> payload)
    {
        string path = Str(payload.GetValueOrDefault("path"));
        string text = Str(payload.GetValueOrDefault("text"));
        bool append = payload.GetValueOrDefault("append") is bool b && b;

        // The tag rides along untouched, so a write can be routed by the same `match` as a read.
        string tag = Str(payload.GetValueOrDefault("tag"));

        try
        {
            // The folder is created on the way. Writing to `out/report.txt` when `out/` does not exist
            // is a mistake nobody makes on purpose, and the alternative is an error the program has to
            // handle before it can do the thing it asked for.
            if (Path.GetDirectoryName(Path.GetFullPath(path)) is { Length: > 0 } dir)
                Directory.CreateDirectory(dir);

            if (append) File.AppendAllText(path, text); else File.WriteAllText(path, text);

            Emit("FileWritten", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = path,
                ["bytes"] = (long)text.Length,
                ["tag"] = tag
            });
        }
        catch (Exception ex)
        {
            Emit("FileMissing", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = path,
                ["reason"] = ex.Message,
                ["tag"] = tag
            });
        }
    }

    /// Turn a finished request into the event the program hears.
    private void Deliver(string url, NetHttp.Result r)
    {
        if (r.Error is { } err)
            Emit("Failed", new Dictionary<string, object?>(StringComparer.Ordinal)
            { ["url"] = url, ["reason"] = err, ["origin"] = null, ["bundle"] = _bundle });
        else
            Emit("Fetched", new Dictionary<string, object?>(StringComparer.Ordinal)
            { ["url"] = url, ["status"] = r.Status, ["body"] = r.Body, ["origin"] = null, ["bundle"] = _bundle });
    }

    /// A network setup step that could not be carried out. It goes to the log AND to the console, because
    /// a mistyped address or a missing key otherwise looks exactly like a peer that is merely quiet — the
    /// single most confusing failure this bundle can produce.
    private void Fail(string what, string why)
    {
        _log.Add($"@{what} failed: {why}");
        _out?.WriteLine($"(@{what} failed: {why})");
    }

    // ---- the clock ------------------------------------------------------

    /// `run once` builds the world — spawn/attach/mark — before anything is heard, so the boot event's
    /// handlers already see the entities a `target` query is meant to find.
    private void RunOnce()
    {
        RunPhase("once");
        CommitPhase();
    }

    private void RunFrames() { for (int i = 0; i < Ticks; i++) Frame(); }

    /// Frames per second to advance on the wall clock, for a run that owns a real one. 0 — the default
    /// — means no loop, which is what `veinc run` and every test want: `Ticks` runs a fixed count and
    /// stops, so a run stays reproducible and `check-backend.sh` can diff it frame for frame.
    ///
    /// A BUILT PROGRAM SETS THIS AND NOTHING ELSE DID, which is the whole of the bug. The generated
    /// host ran `new Interp()` with `Ticks` at 0, so `RunFrames` did nothing and the inbox loop never
    /// called `Frame` again: `each tick`, `frame` and `settled` ran zero times, forever. A game built
    /// with `veinc build` booted, printed whatever `run once` printed, and then sat inert while its
    /// `every N` blocks kept firing — because those run on their own timer thread and never needed the
    /// frame loop. Visibly alive, doing nothing.
    ///
    /// Nothing caught it because the two runtimes that work drive frames themselves: `veinc run` from
    /// `--ticks`, and an editor from `Boot`/`Frame` sixty times a second. The one that ships did not.
    public double FrameRate { get; set; }

    /// The frame loop, in the shape `StartEveryTimers` already uses: a thread that sleeps and POSTS
    /// onto the inbox, so the event loop stays single-threaded and a frame is just another turn.
    ///
    /// It starts only when the program HAS frame work. A batch tool or a server that ships this way has
    /// no `each tick` and gets no timer and no cost — the loop is not a behaviour change for programs
    /// that never asked for a frame, and asking is what declaring `each tick` is.
    ///
    /// `FireTicked` goes with it, so `hear @Ticked` works in a built game exactly as it does in an
    /// editor that drives the clock by hand — one clock, one meaning, wherever the program runs.
    private void StartFrameTimer(System.Collections.Concurrent.BlockingCollection<Action> inbox)
    {
        if (FrameRate <= 0) return;
        if (!_schedules.Any(s => s.Kind is "tick" or "frame" or "settled")) return;

        var period = TimeSpan.FromSeconds(1.0 / FrameRate);
        double delta = 1.0 / FrameRate;

        var timer = new Thread(() =>
        {
            int frame = 0;
            while (true)
            {
                Thread.Sleep(period);
                int n = ++frame;
                // Adding to a completed inbox throws — that is the run ending, so the thread ends too.
                try { inbox.Add(() => { FireTicked(n, delta); Frame(); }); } catch { return; }
            }
        })
        { IsBackground = true, Name = "vein-frames" };

        timer.Start();
    }

    /// The WALL clock. `every N` is the one schedule defined in real seconds, so it runs only where a
    /// real clock exists: a live console session (`veinc run`, a built .exe). Each block gets its own
    /// timer thread, and the thread only POSTS the firing onto the inbox — the event loop itself stays
    /// single-threaded, exactly like an arriving console message.
    ///
    /// Every spawned console runs the same program, so its timers fire too; a program that wants only
    /// the root to act asks `here()`.
    private void StartEveryTimers(System.Collections.Concurrent.BlockingCollection<Action> inbox)
    {
        foreach (var sched in _schedules)
        {
            if (sched.Kind != "every" || sched.Seconds <= 0) continue;
            var period = TimeSpan.FromSeconds(sched.Seconds);
            var timer = new Thread(() =>
            {
                while (true)
                {
                    Thread.Sleep(period);
                    // Adding to a completed inbox throws — that is the run ending, so the thread ends too.
                    try { inbox.Add(() => Fire(sched)); } catch { return; }
                }
            })
            { IsBackground = true, Name = "vein-every-" + sched.Owner.Name };
            timer.Start();
        }
    }

    /// Fire every `every N` block once, as one turn of the wall clock. The console run drives each block
    /// from its own timer thread; a host that owns the clock itself (or a test) drives them from here.
    public void FireEvery()
    {
        foreach (var sched in _schedules)
            if (sched.Kind == "every") Fire(sched);
    }

    /// One firing of an `every N` block: it contributes like a tick, the phase commits, and the events
    /// it emitted drain — the same shape as a frame, just triggered by the wall instead of a count.
    private void Fire(Schedule sched)
    {
        RunGuarded(sched.Body, sched.Owner,
                   new Dictionary<string, object?>(StringComparer.Ordinal), sched.Kind);
        CommitPhase();
        Drain();
    }

    /// One frame. Every `tick`/`frame` block contributes, the phase commits, and only THEN does
    /// `settled` run — so a death check reads an hp that every shard has finished subtracting from,
    /// which is the whole reason `settled` exists (docs/RUNTIME.md §4).
    ///
    /// `every N` is not part of a frame: it is defined in real seconds, so the wall clock drives it
    /// instead (StartEveryTimers).
    // ---- stepping and tracing (the Workbench's tick stepper) ---------------
    //
    // An identity-oriented program has no "next line" worth stopping on: the interesting question is
    // never where execution is, it is what the identities look like now and what changed them. So what
    // is exposed is a TICK boundary and a record of which units ran — not a statement stepper.

    /// One unit of user code that ran: which tick, whose block, what kind, and the event that caused it.
    public sealed record TraceEvent(int Tick, string Owner, string Kind, string? Event);

    /// Set to record execution. Null in an ordinary run, and the cost is then one null check per block.
    public Action<TraceEvent>? Trace;

    /// A unit of user code that FAULTED: which tick, whose block, what kind, the identity bound at the
    /// time, and what went wrong.
    ///
    /// `Entity` is the piece a host cannot reconstruct. A fault inside `target $Health #Hurt as h { … }`
    /// happens with one specific identity bound, and the interpreter knows which — but the failure
    /// leaves as a `@DiagnosticRaised` whose message is "{owner} ({what}): {message}" and whose
    /// `origin` is null, so an editor could name the shard and never the entity. Picking one out of the
    /// last world snapshot would be a guess dressed as an answer.
    ///
    /// Null when the fault was not inside a `target` — a `run once` body faulting belongs to no
    /// identity, and saying 0 there would be worse than saying nothing.
    public sealed record InterpFault(int Tick, string Owner, string Kind, long? Entity, Exception Exception);

    /// Set to observe faults. THE FAULT STILL BECOMES `@DiagnosticRaised` exactly as before — this is a
    /// second pair of eyes, not a redirect, because `hear @DiagnosticRaised` is the language's `catch`
    /// and a host must not be able to take it away from the program.
    ///
    /// Same shape as `Trace` above and for the same reason: a callback costs one null check when unset,
    /// and needs no new construct in a language that has no exceptions.
    public Action<InterpFault>? Fault;

    /// Which tick is running. `Frame` advances it, so a trace line can say when it happened.
    private int _tick;
    public int Tick => _tick;

    /// Boot the world and settle it, WITHOUT running any frames — the state a stepper starts from.
    ///
    /// Separate from Run and Render because both of those decide for you how far to go: Run pumps stdin
    /// forever and Render drains once and returns. Stepping needs the boot alone, and then control.
    public void Boot(IrModule module)
    {
        Setup(module);
        RunOnce();
        Drain();
    }

    public void Frame()
    {
        _tick++;

        // USER CODE CANNOT FAULT HERE — `RunGuarded` catches it and raises `@DiagnosticRaised`, and the
        // operations are total besides: `int("abc")` is 0, `substring` clamps, an out-of-range index is
        // null. So anything that reaches this catch came from the machinery BETWEEN the blocks —
        // reconciling folds, applying structural commands, dispatching the queue — or from a host
        // callback like `Trace` that threw.
        //
        // Reported, then RETHROWN. The exception is not this method's to swallow: a host that was
        // driving the loop needs to stop, and pretending the frame completed would leave a world nobody
        // can reason about. What the seam adds is which tick and which phase, which the bare exception
        // does not carry.
        try
        {
            RunPhase("tick");
            RunPhase("frame");
            CommitPhase();
            RunPhase("settled");
            CommitPhase();
            Drain();           // events emitted by either phase are handled against a settled world
        }
        catch (Exception ex)
        {
            Fault?.Invoke(new InterpFault(_tick, _bundle, "frame",
                                          _currentEntity == 0 ? null : _currentEntity, ex));
            throw;
        }
    }

    private void RunPhase(string kind)
    {
        foreach (var sched in _schedules)
            if (sched.Kind == kind)
                RunGuarded(sched.Body, sched.Owner,
                           new Dictionary<string, object?>(StringComparer.Ordinal), kind);
    }

    /// Run one unit of user code — a schedule block or one event handler — and turn a fault into a
    /// DIAGNOSTIC the program can hear, rather than an exception that ends the run.
    ///
    /// THIS IS THE LANGUAGE'S FAILURE IDIOM, not a new construct. Vein.Net already answers a request
    /// that went wrong with @Failed and a send that arrived nowhere with @Undelivered: the failure is a
    /// MESSAGE, heard like any other. A fault is the same shape, so `hear @DiagnosticRaised` is what
    /// VeinScript has instead of `catch` — and it needs no block to encircle, which is just as well,
    /// since `bring` and `emit` cannot fail at the point they are written.
    ///
    /// The unit is the BLOCK, not the statement. A `run once` body or one `hear` handler is what an
    /// author thinks of as a piece of work; resuming mid-block would leave the world in a state no
    /// source line describes.
    ///
    /// `canRaise` is false while a @DiagnosticRaised handler is running. A fault THERE cannot become
    /// another @DiagnosticRaised — that is a loop the queue guard would end at 10,000 iterations, with
    /// the original cause long buried.
    private void RunGuarded(IrBlock body, Instance owner, Dictionary<string, object?> locals,
                            string what, bool canRaise = true)
    {
        // The trace point. RunGuarded is the ONE place a unit of user code runs — a schedule block or a
        // single `hear` handler — so a hook here sees the whole execution without a second traversal,
        // and sees it at the same granularity an author thinks in. Null in an ordinary run; the Workbench
        // sets it to build the timeline.
        Trace?.Invoke(new TraceEvent(_tick, owner.Name, what, _current is null ? null : Str(_current.GetValueOrDefault("__event"))));

        try { Exec(body, owner, locals); }
        catch (ReturnSignal) { /* a `return` that escaped its fn — the block simply ends */ }
        // A `break`/`continue` written outside any loop. It means nothing, and ending the block is the
        // same answer `return` gets here — reporting a runtime error for it would turn a harmless
        // mistake into a failed frame.
        catch (BreakSignal) { }
        catch (ContinueSignal) { }
        catch (Exception ex)
        {
            string where = $"{owner.Name} ({what})";

            // The HOST hears about it first, and unconditionally — including the `!canRaise` case
            // below, where the program cannot be told at all because a @DiagnosticRaised handler is
            // what faulted. An editor watching a run should not go blind exactly when the program's own
            // error reporting is what broke.
            //
            // `_currentEntity` is 0 outside any `target`, so a `run once` fault reports no identity
            // rather than entity zero.
            Fault?.Invoke(new InterpFault(_tick, owner.Name, what,
                                          _currentEntity == 0 ? null : _currentEntity, ex));

            if (!canRaise) { _out?.WriteLine($"(error in {where} while reporting: {ex.Message})"); return; }

            Emit("DiagnosticRaised", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["severity"] = 2L, ["message"] = $"{where}: {ex.Message}",
                ["origin"] = null, ["bundle"] = _bundle,
            });
        }
    }

    /// info / warning / error, matching the `severity` codes stdlib/Diagnostics.vein documents.
    private static string SeverityWord(long s) => s switch { 0 => "info", 1 => "warning", _ => "error" };

    /// The one point in a phase where the world changes: the fold reducers reconcile every activation's
    /// contributions first, then the structural commands (mark/attach/destroy) queued during the phase
    /// apply on top of the reconciled state.
    private void CommitPhase()
    {
        _store.Commit();
        if (_commands.Count == 0) return;
        var pending = _commands.ToList();
        _commands.Clear();
        foreach (var c in pending) c();
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

            // `@PlaySound` is TRANSPORT, like `@Print` — its meaning is an effect only a host can have,
            // and it is in `HostEvents` for that reason: the C# backend refuses it with a note rather
            // than compiling a game that is silent. What the host is, this interpreter does not know;
            // `AudioOut.Hook` is where an editor or a player says. With no hook set — `veinc run` in a
            // terminal — the sound is dropped, which is honest for a console.
            if (name == "PlaySound")
            {
                AudioOut.Play(Str(payload.GetValueOrDefault("source")),
                              AsDouble(payload.GetValueOrDefault("volume") ?? 1.0));
                continue;
            }
            if (name == "Console") { ConsoleLauncher.Spawn(Str(payload.GetValueOrDefault("name")), Str(payload.GetValueOrDefault("firsttext"))); continue; }
            if (name == "Send")
            {
                string to = Str(payload.GetValueOrDefault("to")), body = Str(payload.GetValueOrDefault("text"));
                // The ONE place a transport is chosen, and the program never sees it happen. A mark that
                // has been `@Link`ed names a peer on another machine, so it goes over the wire; anything
                // else is a console on this one, so it goes over the pipe. That is the whole reason
                // console_roles.vein runs cross-machine by editing only its Boot shard: `@Send { to: #X }`
                // addresses an IDENTITY, and where that identity lives is the routing table's business.
                bool sent = NetBus.IsRemote(to) ? NetBus.Send(to, _self, body) : ConsoleBus.Send(to, _self, body);

                // A console that was closed cannot be reached. A peer going away is NORMAL, so this is an
                // event the program can hear rather than an error — and never a silent drop, which would
                // leave a program unable to tell "delivered" from "shouting into a void".
                if (!sent)
                    Emit("Undelivered", new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["to"] = to, ["text"] = body,
                        ["from"] = payload.GetValueOrDefault("from"), ["origin"] = null, ["bundle"] = _bundle
                    });
                continue;
            }
            if (name == "Listen") { DoListen(payload); continue; }
            if (name == "Link") { DoLink(payload); continue; }
            if (name == "Fetch") { DoFetch(payload); continue; }
            if (name == "ReadFile") { DoReadFile(payload); continue; }
            if (name == "WriteFile") { DoWriteFile(payload); continue; }

            // A DIAGNOSTIC IS NEVER DROPPED. Every other unheard event vanishing is the design — an
            // emit is not addressed to anyone. This one is different: a diagnostics library that loses
            // its own diagnostic is the single outcome it must not have, and that is exactly what
            // `*Vein.Diagnostics.Report.warn(…)` used to be in any program with no collector shard —
            // a call that emitted into a queue nobody read, and printed nothing.
            //
            // So: a shard that hears it wins, and the console is the fallback rather than the default.
            if (name == "DiagnosticRaised" && !_handlers.ContainsKey("DiagnosticRaised"))
            {
                _out?.WriteLine($"({SeverityWord(AsLong(payload.GetValueOrDefault("severity")))}: " +
                                Str(payload.GetValueOrDefault("message")) + ")");
                continue;
            }

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
                RunGuarded(h.Body, h.Owner, locals, "hear @" + name,
                           canRaise: name != "DiagnosticRaised");
            }

            // An event is its own commit point.
            //
            // `mark`/`attach`/`destroy` queue into _commands and apply at a commit — which the clock
            // reaches every frame, but a purely REACTIVE program never does. So a handler that built an
            // entity (`let e = spawn()` + `attach $Client to e`) queued the attach and nothing ever
            // applied it: the entity existed, carried nothing, matched no `target`, and the program was
            // silently missing the state it thought it had just created.
            //
            // Committing per EVENT rather than per drain is what a reactive program expects: handlers run
            // sequentially, so the next event sees what the last one built. Inside one event the change is
            // still deferred, which keeps the rule the phase model relies on — no unit observes a
            // half-changed world.
            CommitPhase();
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
    ///
    /// A message that crossed the console bus carries a bare address as its `from` rather than the
    /// structured emitter of an in-process event — deliberately, because `m.from` is a value programs
    /// concatenate and reply to. So a console is read here as what it is: an identity named by its
    /// address, carrying that address as a mark. `hear @Message as m audience #Alpha { … }` therefore
    /// means "only Alpha may reach this handler", using the barrier the language already has.
    ///
    /// A console carries no SHAPES, so a shape requirement refuses every bus message. That is correct
    /// rather than broken: nothing has told the runtime what a console holds.
    ///
    /// This filters at the receiver and is advisory: `ConsoleBus` takes the sender's name from the
    /// message body and nothing authenticates it, so it guards against a mis-wired topology, not against
    /// a hostile process on the same machine.
    private static bool AudiencePermits(Handler h, Dictionary<string, object?> payload)
    {
        if (h.ReqShapes.Count == 0 && h.ReqMarks.Count == 0) return true;

        var raw = payload.GetValueOrDefault("from");
        if (raw is string address)
        {
            // Exactly the rule below, over a mark set of one: the console's own address.
            var carried = new HashSet<string>(StringComparer.Ordinal) { address };
            return h.ReqShapes.Count == 0 && h.ReqMarks.All(carried.Contains);
        }

        var from = raw as Dictionary<string, object?>;
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

            // `ordered by k` — the block runs FIRST with every bring deferred, then the deferred bodies
            // run sorted. Executing as we went would let an earlier bring change what a later key reads,
            // and the order would depend on itself. Same comparer as an ordered query, so both spell
            // "sorted" identically.
            case IrOrdered ord:
            {
                var frame = new List<Deferred>();
                _orderFrames.Push(frame);
                try { Exec(ord.Collect, self, locals); }
                finally { _orderFrames.Pop(); }

                // OrderBy is a STABLE sort, so brings that tie keep the order they were reached in —
                // which for a `target` loop is the order the rows arrived. Ties are common (a rank
                // column with duplicates), and arbitrary tie-breaking would make the output depend on
                // the sort's internals.
                long prevIndex = _currentIndex, prevEntity = _currentEntity;
                var prevBinds = new List<object?>(_targetBinds);
                foreach (var d in frame.OrderBy(d => d.Key, EntityStore.OrderKey.Instance))
                {
                    // RESTORED, not merely saved. The body runs OUTSIDE the loop it was written in, and
                    // the loop has since moved on: `Index` would read the counter's final value, and
                    // `row.title` would read an empty bind stack and come back blank. Putting the
                    // bindings back is what makes a deferred bring mean what it says where it stands.
                    _currentIndex = d.Index;
                    _currentEntity = d.Entity;
                    _targetBinds.Clear();
                    _targetBinds.AddRange(d.Binds);
                    Exec(d.Body, self, d.Locals);
                }
                _currentIndex = prevIndex;
                _currentEntity = prevEntity;
                _targetBinds.Clear();
                _targetBinds.AddRange(prevBinds);
                break;
            }

            // A `bring` inside an `ordered by`. The key is read HERE, with this iteration's bindings; the
            // body is put aside with a copy of them, because by the time it runs the loop has moved on.
            case IrOrderedBring ob:
            {
                if (_orderFrames.Count == 0) { Exec(ob.Body, self, locals); break; }
                _orderFrames.Peek().Add(new Deferred(
                    Eval(ob.Key, self, locals),
                    ob.Body,
                    new Dictionary<string, object?>(locals, locals.Comparer),
                    new List<object?>(_targetBinds),
                    _currentIndex,
                    _currentEntity));
                break;
            }
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
            case IrBreak: throw BreakSignal.It;
            case IrContinue: throw ContinueSignal.It;
            case IrMatch m: ExecMatch(m, self, locals); break;
            // break/continue not exercised by the render path yet
            default: break;
        }
    }

    /// `match subject { when Name { … } else { … } }`. An arm matches by NAME, because that is what both
    /// kinds of pattern evaluate to: an enum case and a mark each yield their own bare name. So
    /// `match here() { when #Alpha { … } }` is a switch on which console this process is running as.
    private void ExecMatch(IrMatch m, Instance self, Dictionary<string, object?> locals)
    {
        string subject = Str(Eval(m.Subject, self, locals));
        foreach (var arm in m.Arms)
            if (arm.CaseName == subject) { Exec(arm.Body, self, locals); return; }
        if (m.Else is not null) Exec(m.Else, self, locals);
    }

    /// One pass of a loop body. False means the loop must stop.
    ///
    /// Every loop kind goes through here so `break` and `continue` mean the same thing in all four —
    /// `while`, `repeat`, an identity `target` and a collection `target`. A `continue` inside a
    /// `target` still ends that entity's activation cleanly, because the caller's `finally` runs
    /// between this returning and the next iteration starting.
    private bool Iterate(IrBlock body, Instance self, Dictionary<string, object?> locals)
    {
        try { Exec(body, self, locals); }
        catch (ContinueSignal) { }
        catch (BreakSignal) { return false; }
        return true;
    }

    private void ExecLoop(IrLoop lp, Instance self, Dictionary<string, object?> locals)
    {
        switch (lp.Kind)
        {
            case IrLoopKind.While:
                { int g = 0; while (Truthy(Eval(lp.Cond!, self, locals)) && g++ < 100_000)
                      if (!Iterate(lp.Body, self, locals)) break; }
                break;
            case IrLoopKind.Repeat:
                { long n = AsLong(Eval(lp.Count!, self, locals));
                  long prevIdx = _currentIndex;
                  for (long i = 0; i < n; i++)
                  {
                      _currentIndex = i;                       // `Index` works here too; `as i` names the same number
                      if (lp.Var is not null) locals[lp.Var] = i;
                      if (!Iterate(lp.Body, self, locals)) break;
                  }
                  _currentIndex = prevIdx; }
                break;

            case IrLoopKind.Target:
            {
                // `target $Shape #Mark as self { … }` — the identity query. One ACTIVATION per entity, so
                // each gets its own overlay and its writes become independent fold contributions.
                if (lp.Query is { } q)
                {
                    long idx = -1, prevIdx = _currentIndex;
                    foreach (long entity in _store.Query(q.Components, q.Tags, q.OrderShape, q.OrderField))
                    {
                        long previous = _currentEntity;
                        _currentEntity = entity;
                        _currentIndex = ++idx;
                        _targetBinds.Add(entity);
                        if (lp.Var is not null) locals[lp.Var] = entity;

                        _store.BeginActivation();
                        bool more;
                        try { more = Iterate(lp.Body, self, locals); }
                        finally
                        {
                            // The activation and the bind stack unwind FIRST, whatever the body did.
                            // A `break` out of a `target` that skipped EndActivation would leave the
                            // store mid-activation for every later shard in the frame.
                            _store.EndActivation();
                            _targetBinds.RemoveAt(_targetBinds.Count - 1);
                            _currentEntity = previous;
                        }
                        if (!more) break;
                    }
                    _currentIndex = prevIdx;
                }
                // `target <expr> as x { … }` — iterate a collection. Not an identity query, so no overlay.
                else if (lp.Source is not null && Eval(lp.Source, self, locals) is System.Collections.IEnumerable src and not string)
                {
                    long idx = -1, prevIdx = _currentIndex;
                    foreach (var item in src.Cast<object?>().ToList())
                    {
                        _currentIndex = ++idx;
                        _targetBinds.Add(item);
                        if (lp.Var is not null) locals[lp.Var] = item;
                        bool more;
                        try { more = Iterate(lp.Body, self, locals); }
                        finally { _targetBinds.RemoveAt(_targetBinds.Count - 1); }
                        if (!more) break;
                    }
                    _currentIndex = prevIdx;
                }
                break;
            }
        }
    }

    private void DoAssign(IrAssign a, Instance self, Dictionary<string, object?> locals)
    {
        var value = Eval(a.Value, self, locals);
        if (a.Target is IrLocalRef r)
        {
            if (self.State.ContainsKey(r.Name)) self.State[r.Name] = value;
            else locals[r.Name] = value;
            return;
        }

        // `self.Health.hp = …` — a component field. The write lands in the activation's overlay, so it is
        // a fold CONTRIBUTION rather than an immediate mutation; see EntityStore.
        if (a.Target is IrFieldAccess { Receiver: var recvExpr } fa
            && Eval(recvExpr, self, locals) is ComponentRef cr)
            _store.Write(cr.Entity, cr.Shape, fa.Field, value);
    }

    /// An entity id, or null when the value is not one. Entities are `long` at runtime.
    private static long? AsEntity(object? v) => v switch { long l => l, int i => i, _ => null };

    private bool IsComponent(string name) =>
        _components.ContainsKey(name);

    /// An entity + component pair, produced by `self.Health` so `self.Health.hp` can resolve.
    private readonly record struct ComponentRef(long Entity, string Shape);

    /// A ComponentRef is a handle, not a value — `toJson` has to read the shape's fields through the
    /// store to see anything. Field ORDER follows the declaration, so a round trip is stable.
    /// Anything else passes through: a dictionary, a list, or a scalar is already what it looks like.
    private object? Expand(object? v)
    {
        if (v is not ComponentRef cr) return v;

        var fields = _store.FieldsOf(cr.Shape);
        if (fields is null) return null;

        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var f in fields) map[f.Name] = _store.Read(cr.Entity, cr.Shape, f.Name);
        return map;
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
            // The value bound by `target … as <bind>` — an entity id in a query, an element in a
            // collection loop. Resolved BY NAME from the locals the loop already fills, so a nested
            // query reads the binding it names rather than the innermost one (RULES.md 12c).
            //
            // The fallback is the shard, for a `self` that names no binding in scope.
            case IrSelfRef sr:
                return locals.TryGetValue(sr.Bind, out var bound) ? bound
                     : _targetBinds.Count > 0 ? _targetBinds[^1]
                     : self.Name;
            case IrEntityRef: return _currentEntity;   // `Entity` — nearest entity's int id (0 = none)
            case IrLoopIndexRef: return _currentIndex; // `Index` — nearest loop's 0-based counter
            case IrFieldAccess f:
            {
                var recv = Eval(f.Receiver, self, locals);

                // `self.Health` — an entity plus a declared component name is a handle, so the outer
                // access (`.hp`) can read or write the field through the store.
                if (AsEntity(recv) is { } entity && IsComponent(f.Field)) return new ComponentRef(entity, f.Field);
                if (recv is ComponentRef cr) return _store.Read(cr.Entity, cr.Shape, f.Field);
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
            case IrIndex ix:
            {
                var recv = Eval(ix.Receiver, self, locals);
                var key = Eval(ix.Index, self, locals);
                // An index truncates toward zero, so `list[random() * len(list)]` picks an element and
                // needs no cast. Out of range reads null rather than throwing — a runtime is not a place
                // to crash a user's console app.
                if (recv is IReadOnlyList<object?> list)
                { long i = AsLong(key); return i >= 0 && i < list.Count ? list[(int)i] : null; }
                if (recv is IDictionary<string, object?> map) return map.TryGetValue(Str(key), out var mv) ? mv : null;
                if (recv is string str)
                { long i = AsLong(key); return i >= 0 && i < str.Length ? str[(int)i].ToString() : null; }
                return null;
            }
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

    /// The events this interpreter IMPLEMENTS rather than merely delivers — the ones whose meaning is
    /// an effect on the world outside the program.
    ///
    /// `Drain` consumes each of these and `continue`s: `@Fetch` performs an HTTP request, `@ReadFile`
    /// reads a file, `@Print` writes to a console, `@Console` launches a process. They are transport,
    /// and the transport is here.
    ///
    /// EVERY OTHER EVENT IS PURE DATA — it queues, it dispatches to `hear` handlers, and nothing
    /// outside the program happens. `@KeyDown`, `@Clicked`, `@Collided`, `@Damaged`, `@Ticked`,
    /// `@Moved`: an occurrence and nothing more.
    ///
    /// That distinction is what the C# backend needed and could not make. It refused to emit ANY
    /// cross-bundle event, correctly for this set — emitting a payload class and a queue for `@Fetch`
    /// would turn it into a program that compiles, runs, and silently never fetches — and needlessly
    /// for all the rest, which is why a compiled game had no input. The list lives here because this is
    /// the file that decides it: an event added to `Drain` and not to this set would be quietly
    /// compiled into a no-op.
    public static readonly IReadOnlySet<string> HostEvents =
        new HashSet<string>(StringComparer.Ordinal)
        { "Response", "Print", "Console", "Send", "Listen", "Link", "Fetch", "ReadFile", "WriteFile", "PlaySound" };

    /// The names `Prebuilt` below answers to. Declared as a set because `Lower` needs the same list: a
    /// built-in already resolves, so a `use`d bundle exporting the same name must not capture it (VS0217).
    /// Keep the two in step — a name added below and not here is silently rebindable by `use`.
    public static readonly IReadOnlySet<string> PrebuiltNames =
        new HashSet<string>(StringComparer.Ordinal)
        { "spawn", "here", "pick", "len", "random", "join", "toJson", "fromJson",
          "split", "lines", "words", "chars", "trim", "code", "chr", "upper", "lower",
          "contains", "startsWith", "endsWith", "indexOf", "substring", "replace",
          "int", "float", "string", "bool", "isNumber" };

    /// Prebuilt (built-in) functions that DO return a value — the only functions that return.
    ///
    /// `spawn()` is here rather than a keyword: `let e = spawn()` already parses and lowers, so creating
    /// an entity costs no lexer, parser or AstTree change — and nothing in the language creates one
    /// otherwise, which would leave every `target` query matching an empty world forever.
    private object? Prebuilt(string name, List<object?> args) => name switch
    {
        "spawn" => _store.Spawn(),
        // This console's own address. Every spawned console runs the SAME program, so `here() == #Main`
        // is how a program says "only the window the user launched does this".
        "here" => _self,
        "pick" => args.Count > 0 && args[0] is IReadOnlyList<object?> { Count: > 0 } items
            ? items[_rng.Next(items.Count)]
            : null,
        "len" => (long)(args.Count > 0 ? args[0] switch { System.Collections.ICollection c => c.Count, string s => s.Length, _ => 0 } : 0),
        "random" => _rng.NextDouble(),
        "join" => args.Count > 1 && args[0] is System.Collections.IEnumerable e ? string.Join(Str(args[1]), e.Cast<object?>().Select(Str)) : "",

        // A whole COMPONENT serialises, not just a scalar: `toJson(r.Row)` reads every field the shape
        // declares. An entity cannot — nothing can ask an identity which shapes it carries (RULES.md 14)
        // — so the thing you name is always a shape on one.
        "toJson" => Json.Write(args.Count > 0 ? Expand(args[0]) : null),

        // An object comes back as a dictionary, and field access already resolves against one, so
        // `fromJson(body).title` needs nothing further. Malformed input is null rather than a crash.
        "fromJson" => Json.Parse(args.Count > 0 ? Str(args[0]) : ""),

        // ---- text into pieces -------------------------------------------------------------------
        //
        // These are built-ins because they CANNOT be written in VeinScript. There is no `s[i]` and no
        // character comparison (stdlib/Rest.vein says so where it explains why it has no URL builder),
        // so a program can compare whole strings and concatenate them and nothing else. Splitting text
        // is therefore a host capability or it does not exist.
        //
        // A list is the right answer rather than a stream of events, because `target <expr> as x` walks
        // any list already — the same statement that walks `row.comments`.

        // Exact separator, exact pieces: `split("a,,b", ",")` is three, the middle one empty. Nothing is
        // discarded, because a caller counting fields in a CSV needs the empty column to still be there.
        "split" => args.Count > 1
            ? Str(args[0]).Split(Str(args[1])).Select(s => (object?)s).ToList()
            : new List<object?>(),

        // Lines, with the line ENDING removed whichever it was. `split(text, "\n")` looks equivalent and
        // is not: a file written on Windows leaves a `\r` on the end of every line, and `line == "end"`
        // then fails against `"end\r"` with both sides looking identical in any output. That is the trap
        // this exists to close.
        "lines" => args.Count > 0
            ? Str(args[0]).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(s => (object?)s).ToList()
            : new List<object?>(),

        // Words: runs of whitespace, with no empties. `split(line, " ")` on "a  b" gives three pieces,
        // the middle one empty, and a program that cannot inspect characters has no good way to tell an
        // empty word from a real one after the fact.
        "words" => args.Count > 0
            ? Str(args[0]).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(s => (object?)s).ToList()
            : new List<object?>(),

        "trim" => args.Count > 0 ? Str(args[0]).Trim() : "",

        // ---- characters -------------------------------------------------------------------------
        //
        // A CHARACTER IS A ONE-CHARACTER STRING. There is no `char` type and no `'a'` literal, and that
        // is the point: `s[0]` already yields one, `==` already compares it, and ordering now works — so
        // a character needs no type, no shape, no builder and no ceremony of its own. Adding a type
        // would buy a second way to say the same thing plus a literal syntax to learn.
        //
        // `chars` completes the family: split by separator, lines, words, characters. All four hand back
        // a list, so `target chars(word) as c { … }` is the same statement that walks anything else.
        "chars" => args.Count > 0
            ? Str(args[0]).Select(c => (object?)c.ToString()).ToList()
            : new List<object?>(),

        // Codepoint out and back. What ordering alone cannot do: arithmetic on letters — `chr(code(c) +
        // 1)` is the next letter, and a shift cipher is that plus a modulo.
        //
        // The FIRST character, not the whole string, so `code(s)` on a longer one reads its head rather
        // than failing — `code(c)` is almost always called on something that came from `s[i]` already.
        "code" => args.Count > 0 && Str(args[0]).Length > 0 ? (long)Str(args[0])[0] : 0L,
        "chr" => args.Count > 0 ? ((char)AsLong(args[0])).ToString() : "",

        // Case, whole-string. Needed because comparison is ORDINAL: "A" and "a" are different characters
        // and 32 apart, so a case-insensitive test is `lower(a) == lower(b)` rather than a second
        // comparison operator that quietly means something else.
        "upper" => args.Count > 0 ? Str(args[0]).ToUpperInvariant() : "",
        "lower" => args.Count > 0 ? Str(args[0]).ToLowerInvariant() : "",

        // ---- looking inside a string ------------------------------------------------------------
        //
        // These are what a file-handling program actually reaches for, and their absence — not any
        // missing syntax — is what made `if the line mentions ERROR` unwritable. Each is ORDINAL, never
        // culture-sensitive, so they agree with `<` and with how the store sorts.
        //
        // `contains` on the empty needle is TRUE, matching every other language and set theory: every
        // string contains the empty string. Worth stating because the opposite reading — "an empty
        // search matches nothing" — silently drops every line when a filter box is blank.
        "contains" => args.Count > 1 && Str(args[0]).Contains(Str(args[1]), StringComparison.Ordinal),
        "startsWith" => args.Count > 1 && Str(args[0]).StartsWith(Str(args[1]), StringComparison.Ordinal),
        "endsWith" => args.Count > 1 && Str(args[0]).EndsWith(Str(args[1]), StringComparison.Ordinal),

        // -1 for absent, which is the one value a valid position can never be — so `indexOf(s, x) >= 0`
        // reads as "is in there" without a second call.
        "indexOf" => args.Count > 1 ? (long)Str(args[0]).IndexOf(Str(args[1]), StringComparison.Ordinal) : -1L,

        // CLAMPED rather than throwing. A runtime is not a place to crash a user's console app over an
        // index, and every other string operation here already answers out-of-range with an empty value.
        "substring" => Substring(args),

        "replace" => args.Count > 2 ? Str(args[0]).Replace(Str(args[1]), Str(args[2]), StringComparison.Ordinal) : "",

        // ---- conversions ------------------------------------------------------------------------
        //
        // NAMED AFTER THE TYPES, which reads as a cast and needed no new syntax at all: `int`, `float`,
        // `string` and `bool` are not keywords — they are ordinary identifiers the parser only ever
        // sees in type position — so `int(x)` already parsed as a call and simply resolved to nothing.
        //
        // TOTAL, NEVER THROWING, which matches `substring` clamping and `indexOf` answering -1. A
        // conversion that could crash would have to be guarded at every use, and a language with no
        // `catch` has nowhere to put the guard.
        //
        // AND THAT IS WHY `isNumber` IS HERE. `int("abc")` is 0, and so is `int("0")` — total functions
        // buy their safety by making failure indistinguishable from a real answer. `isNumber` is how you
        // tell them apart, and without it this pair would be the `fromJson`-returns-null trap again.
        "int" => ToLong(args.Count > 0 ? args[0] : null),
        "float" => ToDouble(args.Count > 0 ? args[0] : null),
        "string" => args.Count > 0 ? Str(args[0]) : "",
        "bool" => args.Count > 0 && ToBool(args[0]),
        "isNumber" => args.Count > 0 && LooksNumeric(args[0]),

        _ => null
    };

    /// Anything to a whole number. A float truncates toward zero rather than rounding, because
    /// `int(x)` is asked for most often to index or count, and 4.9 items is four.
    private static long ToLong(object? v) => v switch
    {
        null => 0L,
        bool b => b ? 1L : 0L,
        long l => l,
        int i => i,
        double d => FromDouble(d),
        string s => long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n
                  : double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double f)
                    ? FromDouble(f)
                  : 0L,
        _ => 0L
    };

    /// Anything to a real number. INVARIANT CULTURE on purpose: `"1.5"` must parse the same on a machine
    /// where the decimal separator is a comma, or a program would read its own saved files differently
    /// depending on where it ran.
    private static double ToDouble(object? v) => v switch
    {
        null => 0d,
        bool b => b ? 1d : 0d,
        long l => l,
        int i => i,
        double d => d,
        string s => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double f)
                    && double.IsFinite(f) ? f : 0d,
        _ => 0d
    };

    /// A real number to a whole one, CLAMPED at the ends of the range.
    ///
    /// `(long)d` in C# is undefined for a value outside long's range and in practice answers
    /// long.MinValue, so `int("999999999999999999999")` used to be -9223372036854775808: a large
    /// positive number silently becoming a large negative one, which then compared and summed as if it
    /// were real. Saturating is the lesser wrong answer — it is at least on the correct side of zero,
    /// and `isNumber` was already the way to ask whether the text was a number at all.
    private static long FromDouble(double d) =>
        !double.IsFinite(d) ? 0L
        : d >= long.MaxValue ? long.MaxValue
        : d <= long.MinValue ? long.MinValue
        : (long)d;

    /// `bool(x)` — a PARSE for text, and truthiness for everything else.
    ///
    /// This is the one conversion that cannot simply call [Truthy]. Truthiness asks "is there anything
    /// here", and every non-empty string answers yes, so `bool("false")` was TRUE and so was `bool("0")`.
    /// That is the right rule for a condition and the wrong one for a conversion: a string being
    /// converted is nearly always something a person typed or a file held, and there "false" means false.
    ///
    /// Words first, then numbers, and only then truthiness — which is what keeps `bool("cat")` true and
    /// agrees with `if` for every string that is not a written answer.
    private static bool ToBool(object? v)
    {
        if (v is not string s) return Truthy(v);

        string t = s.Trim();
        if (t.Length == 0) return false;
        if (IsWord(t, "true", "yes", "on")) return true;
        if (IsWord(t, "false", "no", "off")) return false;
        return LooksNumeric(t) ? ToDouble(t) != 0 : true;
    }

    private static bool IsWord(string t, params string[] words)
    {
        foreach (string w in words)
            if (string.Equals(t, w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// Would `int`/`float` find a number here, rather than falling back to zero?
    ///
    /// Distinct from `IsNumeric` below, which asks whether a value is ALREADY of numeric type and is
    /// what `==` uses to decide between a numeric and a textual comparison. This one parses text, and
    /// is deliberately false for a bool: `bool` converts to 0 or 1, but the question being asked is "is
    /// this text a number", and answering yes for `true` would let a menu accept it as a choice.
    private static bool LooksNumeric(object? v) => v switch
    {
        long or int or double => true,
        string s => s.Trim().Length > 0 &&
                    double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double f) &&
                    double.IsFinite(f),
        _ => false
    };

    /// `substring(s, start)` to the end, or `substring(s, start, length)`. Both clamped to the string:
    /// a start past the end gives "", a length past the end gives what is there.
    private static string Substring(List<object?> args)
    {
        if (args.Count < 2) return "";
        string s = Str(args[0]);

        int start = (int)Math.Clamp(AsLong(args[1]), 0, s.Length);
        int take = args.Count > 2 ? (int)Math.Clamp(AsLong(args[2]), 0, s.Length - start) : s.Length - start;
        return s.Substring(start, take);
    }

    /// Ordinal comparison when BOTH sides are strings, else null so the caller falls back to numbers.
    ///
    /// Both, deliberately: `rank > 3` where rank happens to hold "5" is arithmetic the program means,
    /// and turning one string operand into a string comparison would change what mixed code already does.
    private static int? Both(object? l, object? r) =>
        l is string a && r is string b ? string.CompareOrdinal(a, b) : null;

    private object? EvalRuntime(IrRuntimeCall rc, Instance self, Dictionary<string, object?> locals)
    {
        switch (rc.Name)
        {
            case "random": return _rng.NextDouble();   // `chance 30%` lowers to this; keep it seeded
            case "Emit":
                if (rc.Args.Count == 1 && rc.Args[0] is IrStructInit si)
                {
                    var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var (field, val) in si.Fields) payload[field] = Eval(val, self, locals);
                    // Fill omitted payload fields: a field's default, else the current context (the
                    // event being handled, then locals) by matching name. Explicit fields win.
                    if (_events.TryGetValue(si.TypeName, out var et))
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
                    //   origin = the ECS entity, when emitted inside a `target` (null outside one)
                    payload["from"] = FromOf(self.Fc);
                    payload["origin"] = _currentEntity == 0 ? null : _currentEntity;
                    payload["bundle"] = _bundle;
                    _edges.Add(new GraphEdge(self.Fc.Name, self.Fc.Kind.ToString(), si.TypeName));
                    _log.Add($"{self.Fc} [{self.Fc.Identity}] emits @{si.TypeName}");
                    Emit(si.TypeName, payload);
                }
                return null;
            // Structural change. The arguments are evaluated NOW (they read the current world) but the
            // mutation is queued: it lands at CommitPhase, after the folds, so no unit in this phase can
            // observe a half-changed world and the phase's result stays order-independent.
            case "AddTag":
            case "RemoveTag":
            {
                long e = AsLong(Eval(rc.Args[0], self, locals));
                string mark = Str(Eval(rc.Args[1], self, locals));
                bool add = rc.Name == "AddTag";
                _commands.Add(() => { if (add) _store.AddTag(e, mark); else _store.RemoveTag(e, mark); });
                return null;
            }
            case "AddComponent":
            {
                long e = AsLong(Eval(rc.Args[0], self, locals));
                // `attach e $Shape` lowers the shape to a bare name; `attach e $Shape { … }` to a struct
                // init, whose evaluated fields seed the row over the declared defaults.
                string shape = ShapeNameOf(rc.Args[1], self, locals);
                var init = rc.Args[1] is IrStructInit
                    ? Eval(rc.Args[1], self, locals) as IReadOnlyDictionary<string, object?>
                    : null;
                _commands.Add(() => _store.AddComponent(e, shape, init));
                return null;
            }
            case "RemoveComponent":
            {
                long e = AsLong(Eval(rc.Args[0], self, locals));
                string shape = ShapeNameOf(rc.Args[1], self, locals);
                _commands.Add(() => _store.RemoveComponent(e, shape));
                return null;
            }
            case "DestroyEntity":
            {
                long e = AsLong(Eval(rc.Args[0], self, locals));
                _commands.Add(() => _store.Destroy(e));
                return null;
            }
            default: return null;
        }
    }

    /// The component named by an `attach`/`detach` argument. It arrives as the bare type name, or as a
    /// struct init when the source supplied field values.
    private string ShapeNameOf(IrExpr e, Instance self, Dictionary<string, object?> locals) => e switch
    {
        IrStructInit si => si.TypeName,
        IrTypeNameExpr tn => tn.Name,
        _ => Str(Eval(e, self, locals))
    };

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
            case IrBinOp.Eq: return LooseEq(l, r);
            case IrBinOp.Ne: return !LooseEq(l, r);
            // TWO STRINGS COMPARE ORDINALLY, everything else numerically.
            //
            // Without this every comparison went through AsDouble, which is 0 for a non-numeric string —
            // so `c >= "a" and c <= "z"` was `0 >= 0 and 0 <= 0`, TRUE FOR EVERY STRING. An is-a-letter
            // test written the obvious way said yes to "!" and "5", and said it silently.
            //
            // Ordinal, never culture-sensitive: `EntityStore.OrderKey` already sorts strings that way so
            // the C# backend produces the same sequence, and an operator that disagreed with the sort
            // would be a second answer to the same question.
            case IrBinOp.Lt: return Ordering(l, r) < 0;
            case IrBinOp.Gt: return Ordering(l, r) > 0;
            case IrBinOp.Le: return Ordering(l, r) <= 0;
            case IrBinOp.Ge: return Ordering(l, r) >= 0;
            default: return null;
        }
    }

    // ---- helpers --------------------------------------------------------

    /// Ordering across loosely-typed values, in the same three steps `==` already takes.
    ///
    /// THE MIXED CASE USED TO READ AS ZERO. `AsDouble` answers 0 for anything that is not a number, and
    /// the old fallback ran it on both sides — so `"5" < 3` was `0 < 3`, TRUE, and `"5" > 3` was false.
    /// Every ordering that mixed text and a number silently agreed that the text was zero. That is the
    /// same shape of bug as the one this comparison was written to fix (`c >= "a"` true for every
    /// string), one layer further in, and `int(x)` existing is what makes the honest answer available.
    ///
    ///   both text            → ordinal, and it must stay that way: character comparison depends on it
    ///                          (RULES 28), and EntityStore.OrderKey sorts the same way, so an operator
    ///                          that disagreed would be a second answer to the same question.
    ///   both read as numbers → numeric, text that parses included.
    ///   otherwise            → ordinal on their text, which is where `==` also lands.
    private static int Ordering(object? l, object? r)
    {
        if (l is string a && r is string b) return string.CompareOrdinal(a, b);
        if (AsNumber(l) is { } x && AsNumber(r) is { } y) return x.CompareTo(y);
        return string.CompareOrdinal(Str(l), Str(r));
    }

    /// The value as a number, or null when it is not one — unlike `AsDouble`, which answers 0 and
    /// cannot tell "zero" from "not a number".
    private static double? AsNumber(object? v) => v switch
    {
        long l => l,
        int i => i,
        double d => d,
        bool b => b ? 1 : 0,
        string s => LooksNumeric(s) ? ToDouble(s) : null,
        _ => null
    };

    private static object Num(double v, object? l, object? r) => IsInt(l) && IsInt(r) ? (long)v : v;
    private static bool IsInt(object? o) => o is long or int;
    private static bool Truthy(object? o) => o switch { null => false, bool b => b, long n => n != 0, double d => d != 0, string s => s.Length > 0, _ => true };
    private static long AsLong(object? o) => o switch { long l => l, int i => i, double d => (long)d, _ => 0 };
    /// Equality across loosely-typed runtime values.
    ///
    /// This used to read `Equals(Str(l), Str(r)) || AsDouble(l) == AsDouble(r)`. The numeric arm was
    /// there so `1 == 1.0` holds across a long field and a double literal — but AsDouble answers 0 for
    /// anything it cannot parse, so EVERY pair of non-numeric strings took that arm and compared 0 to 0.
    /// `"cat" == "dog"` was true, and so was `req.path == "/"` for every path a request could name.
    ///
    /// So the operands decide the comparison: two numbers compare numerically, anything else compares as
    /// text. `"5" == 5` still holds — the string arm renders both to "5" — which keeps the loose typing
    /// the language wants without letting an unparseable string collapse into a number.
    private static bool LooseEq(object? l, object? r) =>
        IsNumeric(l) && IsNumeric(r)
            ? AsDouble(l) == AsDouble(r)
            : string.Equals(Str(l), Str(r), StringComparison.Ordinal);

    private static bool IsNumeric(object? o) => o is double or long or int or bool;

    private static double AsDouble(object? o) => o switch { double d => d, long l => l, int i => i, bool b => b ? 1 : 0, _ => 0 };
    /// Any runtime value as text. The list arm is not a nicety: without it `"lines = " + lines` printed
    /// `System.Collections.Generic.List` plus a mangled type argument — a .NET implementation detail
    /// leaking into the output of a program written in a language that has no .NET in it. `join(list,
    /// sep)` is still how you render a list deliberately; this is what it looks like when you did not ask.
    private static string Str(object? o) => o switch
    {
        null => "",
        string s => s,
        double d => d.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        List<object?> xs => "[" + string.Join(", ", xs.ConvertAll(Str)) + "]",
        _ => o.ToString() ?? ""
    };
    private static object? Default(string typeName) => typeName switch { "string" or "Mark" => "", "int" => 0L, "float" => 0.0, "bool" => false, _ => null };

    // Typed zero used to satisfy required fields under `?` (fill-the-rest). A `Mark` (an identity
    // reference — e.g. a console address) has no meaningful zero, so it names the reserved root.
    private static object? ZeroVal(string typeName) => typeName switch
    {
        "int" or "Entity" => 0L, "float" => 0.0, "bool" => false, "percent" => 0.0,
        "Mark" => RootConsole, "string" => "", _ => ""
    };
}
