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
        /// Set by `mute` and cleared by `unmute`, both at a commit (D18): none of its schedules or
        /// handlers run while it is set. Its State is untouched, so `unmute` resumes where it left off.
        public bool Muted;
        /// The marks it carries NOW: seeded from `shard X #M`, changed by `mark !X #M` at a commit (D20).
        /// What `audience`, `carries` and `mute #M` read — `Fc.CarriedMarks` is only where it started.
        public readonly HashSet<string> Marks = new(StringComparer.Ordinal);
    }

    /// Each shard by the `@key` Lower stamped on it — `Author.Bundle.Name`, the name `mute` resolves to.
    /// Not `Name`: the app linker renames a duplicated shard, and the key is what survives that.
    private readonly Dictionary<string, Instance> _byKey = new(StringComparer.Ordinal);

    /// Marks under a standing `mute #M` (D20). A shard carrying any of them is silent, including one that
    /// gains the mark after the rule was made; `unmute #M` lifts the rule, not anyone's own `mute`.
    private readonly HashSet<string> _mutedMarks = new(StringComparer.Ordinal);

    /// D18 + D20: muted by name, or carrying a muted mark. Every skip asks this, never `Muted` alone.
    private bool IsMuted(Instance inst) => inst.Muted || (_mutedMarks.Count > 0 && inst.Marks.Overlaps(_mutedMarks));

    /// What is paused right now, for a host's debugger: the marks under a standing `mute #M`, and the keys
    /// (`Author.Bundle.Name`) of every shard currently silent — by name or by carrying a muted mark.
    public IReadOnlyCollection<string> MutedMarks => _mutedMarks;
    public IReadOnlyList<string> MutedShards => _byKey.Where(kv => IsMuted(kv.Value)).Select(kv => kv.Key).ToList();

    private sealed record Handler(Instance Owner, string BindName, IrBlock Body, Audience Aud)
    {
        /// Where the `hear` is written (Lower's `@at`), for a fault to say so.
        public SourceAt? At { get; init; }
    }

    /// A unit's place in the source: the file name and the line it starts on.
    private sealed record SourceAt(string File, long Line);

    private static SourceAt? AtOf(IReadOnlyList<IrAttr> attrs) =>
        attrs.FirstOrDefault(a => a.Name == "at") is { Args: [string file, long line] } ? new SourceAt(file, line) : null;

    /// A `hear`'s `audience` barrier, read from its `@audience` attr. The sender must carry every
    /// required shape and mark and none of the excluded ones; when `Shards` is non-empty it must BE one of
    /// them (a sender is one shard, so "all of" could never pass), and it must be none of `NotShards`.
    /// Shards are `Author.Bundle.Name` keys (D19).
    private sealed record Audience(
        IReadOnlyList<string> Shapes, IReadOnlyList<string> Marks,
        IReadOnlyList<string> NotShapes, IReadOnlyList<string> NotMarks,
        IReadOnlyList<string> Shards, IReadOnlyList<string> NotShards)
    {
        public bool IsEmpty => Shapes.Count + Marks.Count + NotShapes.Count + NotMarks.Count
                               + Shards.Count + NotShards.Count == 0;
    }

    private readonly Dictionary<string, List<Handler>> _handlers = new(StringComparer.Ordinal);

    /// A behaviour block the CLOCK drives rather than an event: `once`/`tick`/`frame`/`settled`/`every`.
    /// `Seconds` is meaningful only for `every N`; the frame-counted kinds leave it 0.
    private sealed record Schedule(Instance Owner, string Kind, double Seconds, IrBlock Body)
    {
        /// `each tick as t` — the name the block gives the clock (D22); null when it asks for none.
        public string? Clock { get; init; }
        /// Where the block is written (Lower's `@at`).
        public SourceAt? At { get; init; }
    }

    private readonly List<Schedule> _schedules = new();

    /// User `fn`/`SF` declarations by name. An `fn` returns a value; an `SF` emits and returns null.
    private readonly Dictionary<string, IrFunction> _functions = new(StringComparer.Ordinal);

    /// How a statement finished: on to the next one, or unwinding to the nearest call (`return`) or loop
    /// (`break`, `continue`). Every Exec, ExecStmt and ExecLoop hands it back up; CallUser and Iterate
    /// consume it.
    ///
    /// THESE WERE EXCEPTIONS, thrown per `return` and caught in CallUser. A .NET throw costs microseconds,
    /// so every call cost ~12.5 µs and `fib(25)` took 3.5 s where GDScript takes 70 ms (VeinCards'
    /// VEINSCRIPT_SPEED.md). Real programs are made of small `fn`s, so it was the biggest single cost.
    ///
    /// `break` and `continue` DID NOTHING AT ALL before they were added: they lowered to IrBreak and
    /// IrContinue, the C# backend emitted them, and this switch ran straight past them.
    private enum Flow : byte { Next, Return, Break, Continue }

    /// The value a `return` carries, read by CallUser the moment the Flow.Return reaches it.
    private object? _returned;

    private int _callDepth;

    /// One queued event: its payload, and the envelope the RUNTIME keeps for it beside the payload — its id
    /// and causation chain, the sender the `audience` barrier tests, and where it was emitted.
    ///
    /// Beside, not only in, because an event may DECLARE a field named like the envelope. `event @HostMatch
    /// { id: string }` read back "ev50", the runtime's id, where the emit had said "M1" (Vein Cards #18), and
    /// `@Attack { from: Entity }` read the sender's provenance in place of the attacker, so
    /// samples/RpgDemoCopyPaste's hero never landed a blow. A declared field now keeps what the emit gave it
    /// (Lower.AddPayloadTail's rule), and the chain and the barrier read their own copies here.
    private sealed record Queued(
        string Name, Dictionary<string, object?> Payload, string Id, List<object?> Trail, object? Sender,
        (string Shard, string Phase, SourceAt? At)? Unit, IrLine? Stmt);

    private readonly Queue<Queued> _queue = new();
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
    /// at the last row. `At` is the bring's own line, which a fault in its body reports.
    private sealed record Deferred(
        object? Key, IrBlock Body, Dictionary<string, object?> Locals,
        List<object?> Binds, long Index, long Entity, IrLine? At = null);

    /// The `ordered by` blocks currently collecting. A stack because they nest — the inner one takes the
    /// brings, and the outer keeps whatever it had already gathered.
    private readonly Stack<List<Deferred>> _orderFrames = new();

    /// Seeded so a program using `chance` is reproducible; without this there is no golden run test.
    private Random _rng = new(0);

    /// Start `random()`, `pick` and `chance` over from `seed` — a host replaying a block reseeds with
    /// something every replica agrees on (the previous block's hash), so draws are unpredictable in advance
    /// and identical everywhere. 0 is the seed every run starts with.
    public void Reseed(int seed) => _rng = new Random(seed);

    // ---- gas: a budget that FAILS rather than truncates ------------------------------------------------
    //
    // The safety caps below (a `while` at 100,000 turns, recursion at 256, the queue at 10,000 events)
    // stop a runaway program — a frame must end. They used to stop it SILENTLY: recursion answered null, the
    // `while` simply ended, and the block carried on with whatever it had, which is a wrong answer nobody is
    // told about (ROADMAP Tier 1, row 7). Now a cap is a FAULT of the unit that hit it: the unit stops and
    // @DiagnosticRaised says which cap, where, and what to write instead (Capped, DrainCapped).
    //
    // With a budget set, every statement and expression costs one step, and running out — or hitting a cap —
    // throws OutOfGas out of the host call, through every handler, so a host that has to agree with other
    // machines about whether a piece of work finished knows it did NOT, and can roll it back.

    /// Steps allowed per host call (FireHost); 0, the default, is no budget at all.
    public long Budget { get; set; }

    /// Steps used since the last ResetGas.
    public long GasUsed { get; private set; }

    public void ResetGas() => GasUsed = 0;

    private void Burn()
    {
        if (Budget <= 0) return;
        if (++GasUsed > Budget) throw new OutOfGas($"out of gas after {Budget} steps");
    }

    /// A safety cap was reached. With a budget it is OutOfGas, the host's to hear. Without one it is a fault
    /// of the unit running (RunUnit turns it into @DiagnosticRaised, at the statement that hit it), so the
    /// unit stops rather than carrying on past a cut-short loop or a null that stood in for an answer.
    ///
    /// Outside any unit — a shard `var` whose starting value recurses, a `start` field — there is nothing to
    /// stop and nobody to throw to, so the diagnostic is queued and the caller carries on as it always did.
    private void Capped(string what, string fix)
    {
        if (Budget > 0) throw new OutOfGas(what);
        if (_unit is not null) throw new CapReached($"{what} — {fix}");
        Emit("DiagnosticRaised", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["severity"] = 2L, ["message"] = $"{what} — {fix}",
            ["shard"] = "", ["phase"] = "", ["file"] = _at?.File ?? "", ["line"] = _at?.Line ?? 0L,
            ["origin"] = null, ["bundle"] = _bundle,
        });
    }

    /// A cap, as a fault: not OutOfGas (a budget's), and not a signal — it unwinds to RunUnit like any error.
    private sealed class CapReached(string message) : Exception(message);

    /// A pure program (Purity) is one whose every run agrees: nothing reads the clock, the environment or
    /// the machine, and nothing reaches outside — files, network, consoles. A host that asked for one has
    /// already refused a program that does any of it at compile time; this is the defence in depth for
    /// what slips past a static look (a host event reached through a name the check did not see).
    public bool Pure { get; set; }

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

    /// The envelope names each event DECLARES as fields of its own, by event key — what `Owns` answers.
    private readonly Dictionary<string, HashSet<string>> _ownEnvelope = new(StringComparer.Ordinal);

    private int _eventSeq;
    private Dictionary<string, object?>? _current;   // the payload being handled (an emit fills omitted fields from it)
    private Queued? _currentEvent;                   // …and its envelope: the id and trail its effects are caused by
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
            ConsoleLauncher.UseColors();
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
            if (Stopped) return;   // a one-shot command that quit at boot does not wait for input
            // Simple synchronous loop: stdin line → @Input → drain, until EOF or `@Quit`.
            string? line;
            while (!Stopped && (line = input.ReadLine()) is not null) { FireInput(line); Drain(); }
            if (!Stopped) { FireInputEnded(); Drain(); }
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

        // `tasks list` prints and quits at boot; waiting for stdin after that would hang the shell.
        if (Stopped) { _net?.Stop(); _inbox = null; return; }

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

            // The last chance to print a total or save, before a root run ends.
            try { inbox.Add(() => { if (!Stopped) { FireInputEnded(); Drain(); } }); } catch { /* inbox closed */ }

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
                // Lines the stdin thread read AHEAD are already in the inbox when `@Quit` lands — completing
                // the inbox does not drop them. Stopping means not running them: `quit` then `list` used
                // to list. (Found by the tasks pilot.)
                if (Stopped) break;
                try { action(); }
                catch (Exception ex) { _faulted = true; Err?.WriteLine($"(runtime error, continuing: {ex.Message})"); }
                if (Stopped) { try { inbox.CompleteAdding(); } catch { } }
            }
        }
        catch { /* the inbox completed — the run is over */ }
        bus.Stop();
        _net?.Stop();   // release the port; a listener left bound outlives the run that opened it
        _inbox = null;
    }

    // ---- a HOST's loop (Vein.Hosting's VeinGame) ------------------------------------------------
    //
    // A game engine owns the frame loop, so there is no `Run` here to own an inbox. Without one, a line
    // that arrived from the network ran on NetBus's reader thread — in the middle of whatever frame the
    // engine was stepping — and a send to a peer with no socket yet dialled inline, freezing the frame
    // for up to five seconds. With one, the network only POSTS, as it does under `veinc run`, and the
    // host runs what was posted between frames.

    /// Give this run an inbox the HOST pumps. Call before booting, for the reason `Run` gives: `@Listen`
    /// usually sits in `run once`.
    public void UseInbox() => _inbox ??= new System.Collections.Concurrent.BlockingCollection<Action>();

    /// Run everything the network posted since the last call, on the caller's thread — the host calls it
    /// once a frame, before stepping. Each item is guarded alone, as `Run`'s loop guards it.
    public int Pump()
    {
        if (_inbox is not { } box) return 0;
        int ran = 0;
        while (!Stopped && box.TryTake(out var action))
        {
            try { action(); }
            catch (Exception ex) { _faulted = true; Err?.WriteLine($"(runtime error, continuing: {ex.Message})"); }
            ran++;
        }
        return ran;
    }

    /// Close this run's port, if `@Listen` opened one — a host replacing the program (a reload) or ending
    /// it, so the next one can listen on the same port.
    public void Shutdown()
    {
        _net?.Stop();
        _net = null;
    }

    /// Whether `@Listen` opened a port that is still open.
    public bool Listening => _net is not null;

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
        foreach (var (key, type) in _events)
        {
            var own = type.Fields.Where(f => !IsTail(f) && f.Name is "id" or "cause" or "trail" or "bundle" or "from" or "origin")
                                 .Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            if (own.Count > 0) _ownEnvelope[key] = own;
        }
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
            string? key = shard.Attrs.FirstOrDefault(a => a.Name == "key") is { Args: [string k] } ? k : null;
            var fc = _registry.Register(shard.Name, kind, carriedShapes, carriedMarks, key);
            var inst = new Instance { Name = shard.Name, Fc = fc };
            inst.Marks.UnionWith(carriedMarks);
            if (key is not null) _byKey[key] = inst;
            foreach (var f in shard.State)
                inst.State[f.Name] = f.Default is { } init ? Eval(init, inst, new Dictionary<string, object?>()) : Default(f.Type.Name);
            foreach (var m in shard.Methods)
            {
                // A schedule block (`run once` / `each tick` / `settled` / …) is a body the CLOCK calls,
                // not the event loop; it takes no parameters and hears nothing.
                if (m.Attrs.FirstOrDefault(a => a.Name == "schedule") is { } sch)
                {
                    string when = sch.Args.Count > 0 ? sch.Args[0]?.ToString() ?? "" : "";
                    double seconds = sch.Args.Count > 1 && sch.Args[1] is double d ? d : 0;
                    _schedules.Add(new Schedule(inst, when, seconds, m.Body) { Clock = m.Params.FirstOrDefault()?.Name, At = AtOf(m.Attrs) });
                    continue;
                }

                var hear = m.Attrs.FirstOrDefault(a => a.Name == "hear");
                if (hear is null) continue;
                string ev = hear.Args.Count > 0 ? hear.Args[0]?.ToString() ?? "" : "";
                string bind = m.Params.Count > 0 ? m.Params[0].Name : "_";
                _handlers.TryAdd(ev, new List<Handler>());
                _handlers[ev].Add(new Handler(inst, bind, m.Body, AudienceOf(m.Attrs)) { At = AtOf(m.Attrs) });
            }
        }
    }

    /// A field Lower's AddPayloadTail ADDED to an event rather than one its declaration wrote: the tail's
    /// `from` is the provenance, its `origin` and `source` are Entity. A field of the same name with another
    /// type was declared, and is the event's own.
    private static bool IsTail(IrField f) => f.Name switch
    {
        "from" => f.Type.Name == Lower.ProvenanceType,
        "origin" or "source" => f.Type.Name == "Entity",
        _ => false,
    };

    /// Does event `ev` declare a field `name` of the envelope (`id`, `cause`, `trail`, `bundle`, `from`,
    /// `origin`) itself? Then the runtime leaves it as the emit wrote it.
    private bool Owns(string ev, string name) => _ownEnvelope.TryGetValue(ev, out var own) && own.Contains(name);

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
                    if ((fld.Name is "origin" or "source" && IsTail(fld)) || boot.ContainsKey(fld.Name)) continue;
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

        var sender = FromOf(reqFc);
        if (!Owns(bootEvent, "from")) boot["from"] = sender;
        if (!Owns(bootEvent, "origin")) boot["origin"] = null;
        if (!Owns(bootEvent, "bundle")) boot["bundle"] = _bundle;
        Emit(bootEvent, boot, sender);
    }

    /// Fire an @Input event carrying a line the user typed at the console.
    private void FireInput(string text)
    {
        var stdin = _registry.Register("stdin", VeinKind.Runtime);
        _linesRead++;
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
    /// `*Vein.Input.Mouse.@MouseWorld { x, y, z }` — where the pointer meets the ground, from a 3D host.
    public void FireMouseWorld(double x, double y, double z) => FireDevice("MouseWorld", ("x", x), ("y", y), ("z", z));

    /// `*Vein.Time.Clock.@Ticked { frame, delta }` — the clock, from a host that owns one.
    ///
    /// stdlib/Time.vein settles whose job this is: "a runtime emits @Ticked when appropriate (a game
    /// every frame, an app on a timer)". `@Ticked` is pure data — `HostEvents` above names it as "an
    /// occurrence and nothing more" — so a program hearing it compiled and ran and heard nothing, with
    /// no error anywhere, because the six entry points above were keyboard and mouse only and the
    /// helper they share is private. The host was doing the `$Clock` half through `World.Write` and had
    /// no way to do this half at all.
    public void FireTicked(int frame, double delta)
    {
        // Kept for `each tick as t` (D22): the block reads the frame it runs in without hearing @Ticked.
        _clockFrame = frame;
        _clockDelta = delta;
        FireDevice("Ticked", ("frame", (long)frame), ("delta", delta));
    }

    private int _clockFrame;
    private double _clockDelta;

    /// A track reached its end. Only a NON-LOOPING one ever does, and only the host can know: it owns
    /// the speaker and the clock, and a program cannot ask how long a file is.
    ///
    /// A named entry point beside `FireTicked` rather than a general host-raises-any-event API, because
    /// that is the bigger question and this is the smaller need. `@SoundEnded` is pure DATA — an
    /// occurrence and nothing more, like `@Ticked` and `@Collided` — so it is deliberately absent from
    /// `HostEvents` and the C# backend compiles it like any other event.
    public void FireSoundEnded(long track) => FireDevice("SoundEnded", ("track", track));

    /// `*Vein.Graphics.Animations.@AnimationEnded { entity, name }` — the clip `name` that `entity`'s
    /// `$Animation` asked for reached its end. The same reasoning as `FireSoundEnded`: only the host owns
    /// the clock the clip ran on, and the event is pure data.
    public void FireAnimationEnded(long entity, string name) =>
        FireDevice("AnimationEnded", ("entity", entity), ("name", name));

    /// `*Vein.Game.Collision.@Collided { a, b }` — two identities' `$Collider` boxes started to overlap,
    /// as the HOST's physics saw it. `a` is the lower id, so a pair is reported once.
    public void FireCollided(long a, long b) =>
        FireDevice("Collided", ("a", Math.Min(a, b)), ("b", Math.Max(a, b)));

    /// `*Vein.Game.Collision.@Separated { a, b }` — the pair no longer overlaps.
    public void FireSeparated(long a, long b) =>
        FireDevice("Separated", ("a", Math.Min(a, b)), ("b", Math.Max(a, b)));

    /// `*Vein.UI.Interaction.@Clicked { target }` — the player clicked this identity: the host decided
    /// what was under the pointer (the frontmost model or sprite), which only it can.
    public void FireClicked(long target) => FireDevice("Clicked", ("target", target));

    /// `*Vein.Input.Actions.@ActionPressed { action, strength }` — a named action began. Which inputs
    /// trigger it is the host's setting (an engine's input map), so the rules never name a key.
    /// `player` is whose action — 0 for this machine's player (see stdlib/Input.vein).
    public void FireActionPressed(string action, double strength, int player = 0) =>
        FireDevice("ActionPressed", ("action", action), ("strength", strength), ("player", (long)player));

    /// `*Vein.Input.Actions.@ActionReleased { action, player }`.
    public void FireActionReleased(string action, int player = 0) =>
        FireDevice("ActionReleased", ("action", action), ("player", (long)player));

    /// `*Vein.Input.Gamepad.@ButtonDown { device, button }` — the button by its common-layout name ("A").
    public void FireButtonDown(int device, string button) =>
        FireDevice("ButtonDown", ("device", (long)device), ("button", button));

    /// `*Vein.Input.Gamepad.@ButtonUp { device, button }`.
    public void FireButtonUp(int device, string button) =>
        FireDevice("ButtonUp", ("device", (long)device), ("button", button));

    /// `*Vein.Input.Gamepad.@AxisMoved { device, axis, value }` — a stick (-1..1) or trigger (0..1).
    public void FireAxisMoved(int device, string axis, double value) =>
        FireDevice("AxisMoved", ("device", (long)device), ("axis", axis), ("value", value));

    /// One device occurrence, with the provenance envelope every event carries.
    ///
    /// The sender is a registered runtime identity named `device`, the way stdin is registered as
    /// `stdin` — so `d.from.name` tells a program the key came from the host rather than from another
    /// bundle emitting it, and the audience barrier has something real to test.
    /// A host raises one of the PROGRAM'S OWN events — a Godot pause button sending `@Paused`, a menu
    /// sending `@StartGame` — and the queue drains, as for device input.
    ///
    /// The "bigger question" `FireSoundEnded` deferred, answered narrowly: only an event the program
    /// DECLARES (or imports) can be raised, matched by its bare name (`Paused`) or its key
    /// (`me.Game.Paused`). An unknown name is refused — false — rather than invented, because a host that
    /// misspells an event would otherwise deliver to nobody and never find out. Omitted fields take the
    /// declaration's defaults. The sender is the runtime's `host`, which carries no shard key and no marks.
    public bool FireHost(string eventName, IReadOnlyDictionary<string, object?>? fields = null)
    {
        string? key = _events.ContainsKey(eventName) ? eventName
            : _events.Keys.FirstOrDefault(k => k.EndsWith("." + eventName, StringComparison.Ordinal));
        if (key is null) return false;

        var host = _registry.Register("host", VeinKind.Runtime);
        var sender = FromOf(host);
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (!Owns(key, "from")) payload["from"] = sender;
        if (!Owns(key, "origin")) payload["origin"] = null;
        if (!Owns(key, "bundle")) payload["bundle"] = _bundle;
        foreach (var fld in _events[key].Fields)
            if (!IsTail(fld) && fld.Default is not null)
                payload[fld.Name] = Eval(fld.Default, new Instance { Name = "host", Fc = host }, new Dictionary<string, object?>());
        if (fields is not null)
            foreach (var (f, v) in fields) payload[f] = v;

        Emit(key, payload, sender);
        Drain();
        return true;
    }

    /// Every event as it is handled — the readable name (`Paused`, not `me.Game.Paused`) and its payload.
    /// For a host that acts on events the program emits for it (`@ShowMenu`, `@ShakeCamera`) beyond the
    /// built-in transport ones (`@PlaySound` goes to `AudioOut`). Read-only: the payload is the program's.
    public Action<string, IReadOnlyDictionary<string, object?>>? Observed;

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
    // NO SANDBOX FOR A PROGRAM YOU RUN. A path is a path, and the program runs with the permissions you
    // started it with — the same trust `veinc run` already implies. Under `veinc serve` that trust is not
    // yours to give: the program runs once per REQUEST, and a request names whatever path it likes. So a
    // served program's files are confined to its project folder (`ConfineFiles`, below).

    /// Where a RELATIVE path in `@ReadFile` / `@WriteFile` is anchored. Null keeps .NET's behaviour —
    /// the process's current directory — which is what every embedded caller got before this existed.
    ///
    /// A GAME IS NOT LAUNCHED FROM A SHELL, and that is the whole reason this is settable. `veinc run`
    /// makes the shell's folder look like the right answer by accident: run the same program from two
    /// directories and its saves land in two places, neither of them beside the `.vein` that asked. Once
    /// a program is opened from an editor or a desktop shortcut the folder is one the author never chose
    /// and cannot predict — and the failure is silent, because the write SUCCEEDS and `@FileWritten`
    /// fires. The save is not corrupt; it is somewhere else, and the next run starts a new game.
    ///
    /// It also settles an inconsistency with the language's own other kind of path: `$Image { source:
    /// "art/hero.png" }` means relative to the program in every tool that reads one, so an author who
    /// writes `"art/hero.png"` and `"saves/auto.save"` in one file has written two strings that look
    /// identical and mean different folders.
    public string? BaseDirectory { get; set; }

    /// Every file this program reads, writes or lists must be INSIDE `BaseDirectory`; anything else is refused
    /// with @FileMissing and a @DiagnosticRaised, and never touched.
    ///
    /// For `veinc serve` (ROADMAP Tier 1), where the program runs once per HTTP request and a request is not
    /// the author: `emit @ReadFile { path: r.body }` answered `../../secret.txt`, `C:\Users\…` or `\\host\share`
    /// with the file, and `@WriteFile` created folders anywhere on the way. A path is refused when it is
    /// absolute in any spelling (a drive, `\\?\`, a share, a leading slash), climbs out (`..`, `a/../../x`),
    /// passes through a link that leads out, names a device (`CON`, `COM1` — reading the console would hang
    /// the server) or a stream (`a.txt:x`). Off by default: `veinc run` keeps the trust it documents.
    public bool ConfineFiles { get; set; }

    /// The interpreter `veinc serve` answers ONE request with: a fresh world (RULES 22), its files confined to
    /// the project folder, and what it has to say that nothing hears — a refused path, a fault — written to
    /// `errors`, the server's console. Here rather than in the CLI so the server's configuration is the one
    /// the tests exercise.
    public static Interp Serving(string projectDir, TextWriter? errors = null) =>
        new() { BaseDirectory = projectDir, ConfineFiles = true, ErrorOutput = errors };

    /// The program's command-line arguments, as `args()` answers them. Empty unless a host passes some:
    /// `veinc run f.vein -- a b c`, or whatever a built exe was started with.
    public IReadOnlyList<string> Args { get; set; } = Array.Empty<string>();

    /// Where the runtime's own complaints go — an unheard error, a fault while reporting one. Null (the
    /// default, and what a test gets) means the program's output; a command-line host passes stderr, so a
    /// tool whose output is piped somewhere does not mix its errors into it.
    public TextWriter? ErrorOutput { get; set; }
    private TextWriter? Err => ErrorOutput ?? _out;

    private int? _quitCode;
    private long _linesRead;   // for `@InputEnded { lines }`
    private bool _faulted;

    /// The program asked to stop (`@Quit`).
    public bool Stopped => _quitCode is not null;

    /// What the process should exit with: the code `@Quit` gave, else 1 if an error went unheard, else 0.
    public int ExitCode => _quitCode ?? (_faulted ? 1 : 0);

    private void FireInputEnded()
    {
        var stdin = _registry.Register("stdin", VeinKind.Runtime);
        Emit("InputEnded", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["lines"] = _linesRead,
            ["from"] = FromOf(stdin), ["origin"] = null, ["bundle"] = _bundle
        });
    }

    /// What `now()` answers, when a host pins it — `veinc test` does, so a program that stamps dates gives
    /// the same output every run. Null: `VEIN_NOW` if set, else the real clock.
    public string? FixedNow { get; set; }

    /// `now()` — local time, ISO 8601 to the second. `VEIN_NOW` replaces it outright, which is how a test
    /// of a program that stamps dates gets the same output every run.
    private string Now() =>
        FixedNow is { Length: > 0 } pinned ? pinned
        : Environment.GetEnvironmentVariable("VEIN_NOW") is { Length: > 0 } fixedNow ? fixedNow
        : DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    /// A relative path against `BaseDirectory`; an absolute one is left exactly as written, because a
    /// program that computed a full path has already said where it means.
    private string ResolvePath(string path) =>
        BaseDirectory is { Length: > 0 } b && !Path.IsPathRooted(path)
            ? Path.GetFullPath(Path.Combine(b, path))
            : path;

    /// Where a relative READ looks when nothing is there under `BaseDirectory`: the program's own folder, set
    /// by a host whose files land elsewhere (a game's saves in the player's folder, Godot's user://) — so a
    /// file shipped beside the rules, a level, is still read, while every write still goes to `BaseDirectory`.
    public string? ReadFallback { get; set; }

    private string ResolveRead(string path)
    {
        string resolved = ResolvePath(path);
        // Confined, a read is answered from the project folder alone: a fallback is a second root a request
        // could reach, and `Refusal` vouched for the first one only.
        if (ConfineFiles || File.Exists(resolved) || ReadFallback is not { Length: > 0 } fallback || Path.IsPathRooted(path)) return resolved;
        string beside = Path.GetFullPath(Path.Combine(fallback, path));
        return File.Exists(beside) ? beside : resolved;
    }

    // ---- confinement (`ConfineFiles`, under `veinc serve`) ----------------------------------------------

    /// Why a confined program may not touch `path`; null when it may, or when nothing is confined. Decided on
    /// the path as WRITTEN and on where it LEADS, before anything is opened, created or listed.
    private string? Refusal(string path)
    {
        if (!ConfineFiles) return null;
        if (BaseDirectory is not { Length: > 0 } baseDir) return "no project folder is set, so nothing is inside one";
        if (path.Contains('\0')) return "it contains a NUL character";

        // Absolute in every spelling: a drive (`C:\…`, and drive-relative `C:x`), a device or long path
        // (`\\?\`, `\\.\`), a share (`\\host\share`), and a leading slash — the root of the current drive on
        // Windows, where .NET does not call `/etc` rooted on every platform it runs on.
        if (Path.IsPathRooted(path) || path.StartsWith('/') || path.StartsWith('\\'))
            return "it is absolute, and a served program names its files relative to its project folder";

        bool windows = OperatingSystem.IsWindows();
        // A colon anywhere else names a drive or a hidden stream of a file (`a.txt:x`), never a plain file.
        if (windows && path.Contains(':')) return "a `:` in it names a drive or a file stream";
        if (windows && path.Split('/', '\\').FirstOrDefault(IsDeviceName) is { } device)
            return $"`{device}` is a device in every folder, not a file";

        var cmp = windows || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDir));
        string full = Path.GetFullPath(Path.Combine(root, path));
        if (!Inside(full, root, cmp)) return "it leads outside the project folder";

        try { return LinkOut(root, full, cmp) is { } link ? $"`{link}` is a link that leads outside the project folder" : null; }
        catch (Exception ex) { return $"where it leads could not be checked ({ex.Message})"; }
    }

    /// `full` is `root` or below it — by whole folder names, so `site-private` is not inside `site`.
    private static bool Inside(string full, string root, StringComparison cmp)
    {
        string path = Path.TrimEndingDirectorySeparator(full);
        if (string.Equals(path, root, cmp)) return true;
        return path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, cmp);
    }

    /// The first link (a symlink or a junction) on the way from `root` down to `full` whose target is outside
    /// the project, as the path spells it — or null. A path that is inside by its letters is outside by where it
    /// leads when a link below the root points away, and the filesystem goes where it leads.
    private static string? LinkOut(string root, string full, StringComparison cmp)
    {
        string real = RealPath(root);
        string at = root;
        foreach (string part in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            at = Path.Combine(at, part);
            FileSystemInfo info = Directory.Exists(at) ? new DirectoryInfo(at) : new FileInfo(at);
            if (!info.Exists || info.LinkTarget is null) continue;
            string? target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (target is null || !(Inside(target, root, cmp) || Inside(target, real, cmp))) return Path.GetRelativePath(root, at);
        }
        return null;
    }

    /// `path` with every link along it followed — where the project folder itself really is, so a link inside it
    /// that points to another of its own folders is still inside when the project is reached through a link.
    private static string RealPath(string path)
    {
        if (Path.GetPathRoot(path) is not { Length: > 0 } drive) return path;
        string at = drive;
        foreach (string part in path[drive.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            at = Path.Combine(at, part);
            var info = new DirectoryInfo(at);
            if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                at = Path.TrimEndingDirectorySeparator(target.FullName);
        }
        return at;
    }

    /// A name Windows answers with a DEVICE in any folder — `CON`, `NUL`, `COM1` and the rest — with or without
    /// an extension, and whatever trailing dots or spaces it would drop. Reading `CON` reads the console.
    private static bool IsDeviceName(string segment)
    {
        string name = segment.TrimEnd(' ', '.');
        int dot = name.IndexOf('.');
        string stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem[..3] is "COM" or "LPT") && (char.IsAsciiDigit(stem[3]) || stem[3] is '¹' or '²' or '³'));
    }

    /// A confined program asked for a path it may not have. Answered the way every file failure is —
    /// @FileMissing, carrying the request's tag, so the program's own error path runs and nothing waits for an
    /// answer that will not come — and raised as a @DiagnosticRaised warning: a request probing for `../` is
    /// worth knowing about, so a guard shard hears it, and unheard it is printed where the operator sees it.
    /// The reason names the path as written and never the project's own location.
    private void Refuse(string verb, string path, string tag, string why)
    {
        string reason = $"refused: {verb} '{path}' — {why}. A served program reads and writes only inside its project folder.";
        Emit("FileMissing", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["path"] = path, ["reason"] = reason, ["tag"] = tag,
        });
        Emit("DiagnosticRaised", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["severity"] = 1L, ["message"] = reason,
            ["shard"] = "", ["phase"] = "", ["file"] = "", ["line"] = 0L,
            ["origin"] = null, ["bundle"] = _bundle,
        });
    }

    private void DoReadFile(Dictionary<string, object?> payload)
    {
        string path = Str(payload.GetValueOrDefault("path"));

        // The tag rides the REQUEST through to the ANSWER untouched, so a program with many files routes
        // on what the read was FOR rather than on where it lived. The runtime never reads it.
        string tag = Str(payload.GetValueOrDefault("tag"));

        if (Refusal(path) is { } refused) { Refuse("reading", path, tag, refused); return; }

        try
        {
            string full = Path.GetFullPath(ResolveRead(path));
            string text = File.ReadAllText(full);
            Emit("FileLoaded", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = path,
                ["text"] = text,
                ["bytes"] = (long)text.Length,
                ["full"] = full,
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

    /// `@ListFolder` → `@FolderListed { entries }`: folders first, each with a trailing `/`, then files — each group
    /// by name, ordinally, so the order is the same on every machine. A folder that cannot be read answers
    /// @FileMissing, routed by the same tag, as a file read is.
    private void DoListFolder(Dictionary<string, object?> payload)
    {
        string path = Str(payload.GetValueOrDefault("path"));
        string tag = Str(payload.GetValueOrDefault("tag"));
        if (Refusal(path.Length == 0 ? "." : path) is { } refused) { Refuse("listing", path, tag, refused); return; }
        try
        {
            string full = Path.GetFullPath(ResolvePath(path.Length == 0 ? "." : path));
            var folders = Directory.GetDirectories(full).Select(d => Path.GetFileName(d) + "/").OrderBy(n => n, StringComparer.Ordinal);
            var files = Directory.GetFiles(full).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal);
            Emit("FolderListed", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = path,
                ["full"] = full,
                ["entries"] = folders.Concat(files).Cast<object?>().ToList(),
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

    /// Asks the host for something only a host can do — today, a file dialog. True when the host took it and will
    /// answer later (with @FilePicked or @PickCancelled, through <see cref="FireHost"/>).
    public Func<string, IReadOnlyDictionary<string, object?>, bool>? HostRequest { get; set; }

    /// `@PickOpenFile` / `@PickSaveFile`. With no host to show a dialog (`veinc run`, a test), the answer is
    /// @PickCancelled at once — a program waiting on a picker must never wait forever.
    private void DoPick(string name, Dictionary<string, object?> payload)
    {
        if (HostRequest is { } host && host(name, payload)) return;
        Emit("PickCancelled", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["tag"] = Str(payload.GetValueOrDefault("tag")),
            ["reason"] = "no window to show a file dialog in",
        });
    }

    private void DoWriteFile(Dictionary<string, object?> payload)
    {
        string path = Str(payload.GetValueOrDefault("path"));
        string text = Str(payload.GetValueOrDefault("text"));
        bool append = payload.GetValueOrDefault("append") is bool b && b;

        // The tag rides along untouched, so a write can be routed by the same `match` as a read.
        string tag = Str(payload.GetValueOrDefault("tag"));

        // Before the folder is created on the way: a refused write leaves nothing behind, not even a folder.
        if (Refusal(path) is { } refused) { Refuse("writing", path, tag, refused); return; }

        try
        {
            // The folder is created on the way. Writing to `out/report.txt` when `out/` does not exist
            // is a mistake nobody makes on purpose, and the alternative is an error the program has to
            // handle before it can do the thing it asked for.
            string full = ResolvePath(path);
            if (Path.GetDirectoryName(Path.GetFullPath(full)) is { Length: > 0 } dir)
                Directory.CreateDirectory(dir);

            if (append) File.AppendAllText(full, text); else File.WriteAllText(full, text);

            Emit("FileWritten", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = path,
                ["bytes"] = (long)text.Length,
                ["full"] = Path.GetFullPath(full),
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
        ComposeWorld();   // so the first `settled` reads a composed world, not a half-built one
    }

    /// `--ticks N`: advance exactly N frames, firing the clock for each.
    ///
    /// `@Ticked` USED TO BE THE WALL CLOCK'S ALONE. `StartFrameTimer` fired it per frame and this path
    /// did not, so `veinc run --ticks 3` ran three `each tick` blocks and no `hear @Ticked` at all —
    /// and a game that moves on `@Ticked` booted under the compiler's own runner and did nothing. Every
    /// game in `samples/` is written that way, which is exactly the set that most wanted testing here.
    ///
    /// Fired SYNCHRONOUSLY, before the frame, in the same order the timer uses — so N ticks means N
    /// `@Ticked`s and the count a test asserts stays exact. The delta is the frame rate's when one is
    /// set and 1/60 otherwise: a deterministic run has no wall clock to ask, and a fixed step is what
    /// makes it reproducible.
    private void RunFrames()
    {
        double delta = 1.0 / (FrameRate > 0 ? FrameRate : 60.0);
        for (int i = 0; i < Ticks && !Stopped; i++)
        {
            FireTicked(i + 1, delta);
            Frame();
        }
    }

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

        long period = TimeSpan.FromSeconds(1.0 / FrameRate).Ticks;
        double delta = 1.0 / FrameRate;

        // PACED TO DEADLINES, NOT TO SLEEPS. Windows rounds a sleep up to its ~15.6 ms timer, so
        // sleeping 16.7 ms took ~31 and a game on `t.delta` ran at half speed (VeinCards, VEIN_REPORT
        // #21). Each frame now waits for its own slot on a stopwatch, so the overshoot of one sleep is
        // taken out of the next and sixty frames last a second. `delta` stays fixed — the same frames
        // `--ticks` gives. Far behind (a debugger pause), the clock starts over rather than bursting.
        var timer = new Thread(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long next = 0;
            int frame = 0;
            while (true)
            {
                next += period;
                long wait = next - clock.Elapsed.Ticks;
                if (wait > 0) Thread.Sleep(TimeSpan.FromTicks(wait));
                else if (-wait > 5 * period) next = clock.Elapsed.Ticks;
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
            long period = TimeSpan.FromSeconds(sched.Seconds).Ticks;
            var timer = new Thread(() =>
            {
                // Paced to deadlines like the frame clock, so `every 1` does not drift a sleep's
                // overshoot further behind each second.
                var clock = System.Diagnostics.Stopwatch.StartNew();
                long next = 0;
                while (true)
                {
                    next += period;
                    long wait = next - clock.Elapsed.Ticks;
                    if (wait > 0) Thread.Sleep(TimeSpan.FromTicks(wait));
                    else if (-wait > 5 * period) next = clock.Elapsed.Ticks;
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

    /// Seconds each `every N` block has been waiting, for a host that owns the clock (AdvanceEvery).
    private readonly Dictionary<Schedule, double> _everyWaited = new();

    /// Let `seconds` of a HOST'S clock pass: each `every N` block fires once per whole N elapsed, in
    /// declaration order, keeping the remainder. The timer threads `Run` starts are the wall-clock way;
    /// an engine stepping frames has no such thread, and without this its `every` blocks never ran at all.
    /// `FireEvery` stays as it is — "fire them all once" is what its tests mean.
    public void AdvanceEvery(double seconds)
    {
        if (!(seconds > 0)) return;
        foreach (var sched in _schedules)
        {
            if (sched.Kind != "every" || !(sched.Seconds > 0)) continue;
            double waited = _everyWaited.GetValueOrDefault(sched) + seconds;
            // Bounded, so a host that stalls for a minute does not fire a 0.1 s block six hundred times.
            for (int fired = 0; waited >= sched.Seconds && fired < 8 && !Stopped; fired++)
            {
                waited -= sched.Seconds;
                Fire(sched);
            }
            _everyWaited[sched] = Math.Min(waited, sched.Seconds);
        }
    }

    /// One firing of an `every N` block: it contributes like a tick, the phase commits, and the events
    /// it emitted drain — the same shape as a frame, just triggered by the wall instead of a count.
    private void Fire(Schedule sched)
    {
        if (IsMuted(sched.Owner)) { _log.Add($"{sched.Owner.Fc} MUTED every {sched.Seconds}s"); return; }
        RunGuarded(sched.Body, sched.Owner,
                   new Dictionary<string, object?>(StringComparer.Ordinal), sched.Kind, at: sched.At);
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

    /// What one unit COST: the same identification as a TraceEvent, plus how long it took and how many
    /// activations it made — one per identity a `target` inside it visited.
    ///
    /// Both numbers, because either alone misleads. Milliseconds say what the frame is spending; an
    /// activation count says whether a shard is slow or merely busy, and the two want different fixes —
    /// a slow one is a body to look at, a busy one is a query to narrow. The measured cost of a visit is
    /// a few microseconds, so a shard walking ten thousand tiles is expensive without a single
    /// questionable line in it.
    public readonly record struct UnitCost(int Tick, string Owner, string Kind, double Milliseconds, long Activations);

    /// Called after each unit, when a host is measuring. Null in an ordinary run, and the timing work
    /// is skipped entirely when it is — a Stopwatch per unit is cheap but not free, and a profiler that
    /// changes the number it reports is not a profiler.
    ///
    /// A hook rather than an accumulator on `Interp`, matching `Trace`: what a profiler wants to keep,
    /// how it wants to bucket it, and how long it wants to remember are all the host's questions.
    public Action<UnitCost>? Timed;


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

    /// Activations since the run began — one per identity a `target` body visited. A running total
    /// rather than a per-unit counter because units nest: a `hear` handler inside a schedule block is
    /// its own unit, and a difference across the call is the only number that stays right when they do.
    private long _activations;
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

    // ---- carrying a running world across an edit (Vein.Hosting's state-keeping reload) -----------------

    /// Set a program up WITHOUT running it — no `run once`, no boot, nothing drained. A host restoring a
    /// world it saved from the previous version of the program (identities, shard state, mutes) calls
    /// this instead of `Boot`, so nothing the program creates at start is created twice.
    public void Resume(IrModule module) => Setup(module);

    /// Every shard's `var`s, by the shard's key (`Author.Bundle.Name`) — a copy.
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> ShardStates() =>
        _byKey.ToDictionary(kv => kv.Key, kv => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(kv.Value.State, StringComparer.Ordinal),
                            StringComparer.Ordinal);

    /// Set one shard `var`; false when this program has no such shard or var.
    public bool SetShardState(string key, string name, object? value)
    {
        if (!_byKey.TryGetValue(key, out var inst) || !inst.State.ContainsKey(name)) return false;
        inst.State[name] = value;
        return true;
    }

    /// Shards silenced by their own `mute Name` (not by carrying a muted mark) — what a restore re-mutes.
    public IReadOnlyList<string> ShardsMutedByName => _byKey.Where(kv => kv.Value.Muted).Select(kv => kv.Key).ToList();

    /// Put back what was muted — immediately, not at a commit: the world is being restored, not run.
    public void RestoreMutes(IEnumerable<string> marks, IEnumerable<string> shardKeys)
    {
        _mutedMarks.UnionWith(marks);
        foreach (string key in shardKeys) if (_byKey.TryGetValue(key, out var inst)) inst.Muted = true;
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

            // BEFORE `settled`, and that ordering is the whole of why this is not a host job. Collision
            // runs in `settled` precisely because positions are written during the tick and reconciled
            // at its end, so composing after it would hand every collision pass a stale `$World` — a
            // one-frame lag, invisible at 60fps and wrong at every speed.
            ComposeWorld();

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
            if (sched.Kind == kind && IsMuted(sched.Owner)) _log.Add($"{sched.Owner.Fc} MUTED {kind}");
            else if (sched.Kind == kind)
            {
                var locals = new Dictionary<string, object?>(StringComparer.Ordinal);
                // `each tick as t`: the clock of this frame, as @Ticked carries it. A host that never fires
                // @Ticked still gets a frame number (the tick count), and the fixed step a run uses.
                if (sched.Clock is { } clock)
                    locals[clock] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["frame"] = (long)(_clockFrame > 0 ? _clockFrame : _tick),
                        ["delta"] = _clockFrame > 0 ? _clockDelta : 1.0 / (FrameRate > 0 ? FrameRate : 60.0),
                    };
                RunGuarded(sched.Body, sched.Owner, locals, kind, at: sched.At);
            }
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
                            string what, bool canRaise = true, SourceAt? at = null)
    {
        // The unit running, so a diagnostic raised INSIDE it — `do warn(…)` — says where, as a fault does
        // (Stamp). Saved and put back: units nest when a handler's drain runs another — and so is the
        // statement running, which belongs to the unit outside.
        var outer = _unit;
        var outerAt = _at;
        _unit = (owner.Name, what, at);
        _at = null;
        try { RunUnit(body, owner, locals, what, canRaise, at); }
        finally { _unit = outer; _at = outerAt; }
    }

    /// The unit of work running now: its shard, its phase (`once`, `tick`, `hear @E`) and where it is written.
    private (string Shard, string Phase, SourceAt? At)? _unit;

    /// The statement running now. A call's statements set it and the call's return puts the caller's back,
    /// so it is always the INNERMOST — the line a fault reports (RunUnit), where it used to report only the
    /// unit the fault was reached from.
    private IrLine? _at;

    /// The `fn`/`SF` calls open now, outermost first — a fault's call chain ("in fn a ← SF b ← hear @E").
    private readonly List<IrFunction> _calls = new();

    /// What RunUnit's exception filter saw: the statement that failed and the calls it was reached through.
    private IrLine? _pinnedAt;
    private List<string>? _pinnedCalls;

    /// Read where a fault happened, in RunUnit's exception FILTER. A filter runs BEFORE the stack unwinds, so
    /// every call between is still open and `_at` is still the innermost statement; by the time the catch
    /// body runs, each of those calls has returned and put its caller's place back. `outerCalls` are the
    /// calls a unit outside this one had open — not part of this fault's chain. Always true: it only reads.
    private bool Pin(int outerCalls)
    {
        _pinnedAt = _at;
        _pinnedCalls = _calls.Count > outerCalls ? _calls.Skip(outerCalls).Select(CallLabel).ToList() : null;
        return true;
    }

    /// `fn twice` / `SF deal` — a call as its author wrote it: a shard's own function without the shard prefix
    /// Lower gives it (`Gun__twice`), an imported one by its own name rather than its mangled key.
    private static string CallLabel(IrFunction f)
    {
        string name = f.Attrs.FirstOrDefault(a => a.Name == "imported")?.Args.FirstOrDefault() is string key
            ? key[(key.LastIndexOf('.') + 1)..]
            : f.Name.LastIndexOf("__", StringComparison.Ordinal) is var cut and > 0 ? f.Name[(cut + 2)..] : f.Name;
        return (f.IsPure || f.Attrs.Any(a => a.Name == "sf") ? "SF " : "fn ") + name;
    }

    /// " in fn a ← SF b ← hear @Loaded": the calls a fault came through, innermost first, ending at the unit.
    /// A run of one call is written once with its count — recursion that hit the cap is 256 of them — and a
    /// chain still longer keeps its two ends, which are where to look.
    private static string Through(List<string>? calls, string what)
    {
        if (calls is null || calls.Count == 0) return "";
        var runs = new List<string>();
        for (int i = calls.Count - 1; i >= 0;)
        {
            int start = i;
            while (start > 0 && calls[start - 1] == calls[i]) start--;
            runs.Add(i - start > 0 ? $"{calls[i]} ×{i - start + 1}" : calls[i]);
            i = start - 1;
        }
        if (runs.Count > 8) runs = runs.Take(4).Append("…").Concat(runs.Skip(runs.Count - 3)).ToList();
        return " in " + string.Join(" ← ", runs) + " ← " + what;
    }

    /// "Compute (once) at diagnostics.vein:88" — a place a person can open.
    private static string Where(string shard, string phase, SourceAt? at) =>
        at is null ? $"{shard} ({phase})" : $"{shard} ({phase}) at {at.File}:{at.Line}";

    /// A @DiagnosticRaised from inside a unit, given the unit's place where the raiser left it empty —
    /// what makes `if d.shard == "LoadSave"` work for a `do warn(…)` as it does for a fault.
    private void Stamp(Dictionary<string, object?> payload)
    {
        if (_unit is not { } u) return;
        if (Str(payload.GetValueOrDefault("shard")) == "") payload["shard"] = u.Shard;
        if (Str(payload.GetValueOrDefault("phase")) == "") payload["phase"] = u.Phase;
        if (u.At is { } at && Str(payload.GetValueOrDefault("file")) == "")
        {
            payload["file"] = at.File;
            payload["line"] = at.Line;
        }
    }

    private void RunUnit(IrBlock body, Instance owner, Dictionary<string, object?> locals,
                         string what, bool canRaise, SourceAt? at)
    {
        // The trace point. RunGuarded is the ONE place a unit of user code runs — a schedule block or a
        // single `hear` handler — so a hook here sees the whole execution without a second traversal,
        // and sees it at the same granularity an author thinks in. Null in an ordinary run; the Workbench
        // sets it to build the timeline.
        Trace?.Invoke(new TraceEvent(_tick, owner.Name, what, _current is null ? null : Str(_current.GetValueOrDefault("__event"))));

        // MEASURED ONLY WHEN SOMEONE IS MEASURING. The same choke point answers "what did this cost",
        // which is the question a profiler asks and the one nothing could answer before — the frame
        // budget was known and where it went was not. A Stopwatch per unit is cheap and not free, so an
        // unmeasured run allocates none and reads no clock.
        bool measuring = Timed is not null;
        long startedAt = measuring ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        long startedActivations = _activations;
        int outerCalls = _calls.Count;

        // A `return` that escaped its fn, or a `break`/`continue` written outside any loop, simply ends the
        // block — reporting a runtime error for it would turn a harmless mistake into a failed frame.
        try { Exec(body, owner, locals); }
        // Out of gas is the HOST's to hear, not the program's: the work did not finish, and no handler may
        // turn that into a diagnostic and carry on as if it had.
        catch (OutOfGas) { throw; }
        catch (Exception ex) when (Pin(outerCalls))
        {
            // WHERE IT FAILED, not only which unit: the statement that threw, in whatever file it is written,
            // and the calls it was reached through. A division by zero five SF calls below a `hear` was
            // reported at the `hear` — Engine.vein:1771, the file-loaded handler — and the real line took a
            // print at every step to find (Vein Cards #17). `shard` and `phase` still name the unit, so a
            // collector's `if d.shard == "LoadSave"` means what it did; `file` and `line` are the statement's.
            var place = _pinnedAt is { } stmt ? new SourceAt(stmt.File, stmt.Line) : at;
            string where = Where(owner.Name, what, place) + Through(_pinnedCalls, what);

            // The HOST hears about it first, and unconditionally — including the `!canRaise` case
            // below, where the program cannot be told at all because a @DiagnosticRaised handler is
            // what faulted. An editor watching a run should not go blind exactly when the program's own
            // error reporting is what broke.
            //
            // `_currentEntity` is 0 outside any `target`, so a `run once` fault reports no identity
            // rather than entity zero.
            Fault?.Invoke(new InterpFault(_tick, owner.Name, what,
                                          _currentEntity == 0 ? null : _currentEntity, ex));

            if (!canRaise) { _faulted = true; Err?.WriteLine($"(error in {where} while reporting: {ex.Message})"); return; }

            Emit("DiagnosticRaised", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["severity"] = 2L, ["message"] = $"{where}: {ex.Message}",
                ["shard"] = owner.Name, ["phase"] = what, ["file"] = place?.File ?? "", ["line"] = place?.Line ?? 0L,
                ["origin"] = null, ["bundle"] = _bundle,
            });
        }
        finally
        {
            // A unit that FAULTED still cost time, and a profiler that quietly omitted it would report a
            // frame whose parts do not add up to its whole — which is the one thing a profiler must not do.
            if (measuring)
                Timed!.Invoke(new UnitCost(_tick, owner.Name, what,
                    (System.Diagnostics.Stopwatch.GetTimestamp() - startedAt) * 1000.0 / System.Diagnostics.Stopwatch.Frequency,
                    _activations - startedActivations));
        }
    }

    /// A diagnostic as the console shows it: the message, and where — unless the message already says
    /// (a fault's does: "Compute (once) at f.vein:88: …").
    private static string DiagnosticText(Dictionary<string, object?> payload)
    {
        string message = Str(payload.GetValueOrDefault("message"));
        string shard = Str(payload.GetValueOrDefault("shard"));
        if (shard == "" || message.StartsWith(shard + " (", StringComparison.Ordinal)) return message;
        string file = Str(payload.GetValueOrDefault("file"));
        var at = file == "" ? null : new SourceAt(file, AsLong(payload.GetValueOrDefault("line")));
        return $"{message} — {Where(shard, Str(payload.GetValueOrDefault("phase")), at)}";
    }

    /// info / warning / error, matching the `severity` codes stdlib/Diagnostics.vein documents.
    private static string SeverityWord(long s) => s switch { 0 => "info", 1 => "warning", _ => "error" };

    /// The one point in a phase where the world changes: the fold reducers reconcile every activation's
    /// contributions first, then the structural commands (mark/attach/destroy) queued during the phase
    /// apply on top of the reconciled state.
    // ---- the transform hierarchy (RULES 13c) -------------------------------------------------------
    //
    // `$Parent { of: Entity }` has existed in `Vein.Core.Relations` since the beginning and meant nothing
    // to anything: every `$Position` was absolute, and a kit that wanted a child to follow its parent
    // rewrote the child's absolute position every tick at O(children x parents).
    //
    // Now: an identity carrying `$Parent` has its `$Position` read as LOCAL to that parent, and the
    // runtime publishes the composed result as `$World`.
    //
    // WHY A SECOND SHAPE RATHER THAN COMPOSING IN PLACE. A composed position the HOST can see is not
    // enough, and the counter-example is three lines of any collision kit:
    //
    //     target $Position $Collider as a { target $Position $Collider as b { let dx = b.Position.x - … } }
    //
    // If a parented crate's `$Position` were local, that subtraction compares a LOCAL against an
    // ABSOLUTE. It does not fault and it does not look wrong — the renderer composes correctly, so the
    // crate is DRAWN exactly where it belongs and simply fails to collide with what it is visibly
    // touching. A game that looks right and collides wrong is the worst failure this project has a name
    // for. So the program keeps writing `$Position` and every shard that wants a world value reads
    // `$World`, which is a name it can see.
    //
    // AND `$World` EXISTS ON EVERY POSITIONED IDENTITY, parented or not. Publishing it only for children
    // would mean every kit reading it needed a fallback, and a conditional migration is where the silent
    // bugs live. An unparented identity's `$World` is simply its `$Position`.
    private void ComposeWorld()
    {
        if (!_store.Declared("Position") || !_store.Declared("World")) return;

        var positioned = _store.Query(PositionOnly, NoTags, null, null);
        if (positioned.Length == 0) return;

        // Depth-first with memo, so a chain is composed once however many children hang off it. The
        // explicit stack is what lets a CYCLE be handled properly rather than merely detected: on
        // finding an identity that is already being resolved, everything from it to the top of the stack
        // IS the loop, and all of them fall back to their local position together. Breaking only at the
        // point of detection leaves the rest of the loop composed against a half-answer — `a` parented
        // to `b` parented to `a` came out at 4 and 3 rather than at 1 and 2.
        var world = new Dictionary<long, (double X, double Y, double Z)>();
        var state = new Dictionary<long, int>();
        var stack = new List<long>();

        (double X, double Y, double Z) Local(long e) =>
            (Num(_store.Read(e, "Position", "x")),
             Num(_store.Read(e, "Position", "y")),
             Num(_store.Read(e, "Position", "z")));

        (double X, double Y, double Z) Resolve(long e)
        {
            if (world.TryGetValue(e, out var done)) return done;

            if (state.TryGetValue(e, out int s) && s == 1)
            {
                // A CYCLE. Reported once per identity for the life of the run, and every identity in the
                // loop keeps its own position — a cycle is an authoring mistake, and moving things to
                // some arbitrary partial sum hides it where leaving them still shows it.
                int at = stack.LastIndexOf(e);
                for (int i = at; i >= 0 && i < stack.Count; i++)
                {
                    long member = stack[i];
                    world[member] = Local(member);
                    state[member] = 2;
                    if (_cycleReported.Add(member))
                        Fault?.Invoke(new InterpFault(_tick, _bundle, "compose", member,
                            new InvalidOperationException(
                                $"identity {member} is its own ancestor through $Parent — the chain cannot be " +
                                "composed, so it keeps its local position. Break the loop.")));
                }
                return world[e];
            }

            state[e] = 1;
            stack.Add(e);
            var result = Local(e);

            if (_store.Has(e, "Parent") && _store.Read(e, "Parent", "of") is { } raw)
            {
                long parent = raw switch { long l => l, int i => i, double d => (long)d, _ => 0 };

                // A parent that no longer exists, or one with no position of its own: the child keeps its
                // own position and stops being moved. That is what a detach means, and it is what the
                // kit this replaces already did deliberately.
                if (parent != 0 && parent != e && _store.IsAlive(parent) && _store.Has(parent, "Position"))
                {
                    var p = Resolve(parent);
                    // The recursion may have resolved THIS identity as part of a cycle, in which case its
                    // answer is already decided and adding a parent's on top would undo it.
                    if (world.TryGetValue(e, out var settled)) { stack.RemoveAt(stack.Count - 1); return settled; }
                    var local = result;
                    result = (local.X + p.X, local.Y + p.Y, local.Z + p.Z);
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[e] = 2;
            world[e] = result;
            return result;
        }

        foreach (long e in positioned)
        {
            var w = Resolve(e);
            if (!_store.Has(e, "World")) _store.AddComponent(e, "World");
            _store.Publish(e, "World", "x", w.X);
            _store.Publish(e, "World", "y", w.Y);
            _store.Publish(e, "World", "z", w.Z);
        }
    }

    private static readonly string[] PositionOnly = { "Position" };
    private static readonly string[] NoTags = Array.Empty<string>();

    /// Cycles already reported, so a loop is named once rather than once per frame for the life of the run.
    private readonly HashSet<long> _cycleReported = new();

    private static double Num(object? v) => v switch
    {
        double d => d, long l => l, int i => i, float f => f, _ => 0.0
    };

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
        while (_queue.Count > 0)
        {
            if (guard++ >= 10_000) { DrainCapped(); break; }
            var queued = _queue.Dequeue();
            var (name, payload) = (queued.Name, queued.Payload);
            if (Pure && Purity.ReachesOutside(name))
            {
                Err?.WriteLine($"(refused in a pure program: @{name})");
                continue;
            }
            _current = payload;      // an emit inside a handler fills omitted fields from this event…
            _currentEvent = queued;  // …and is caused by it: its id goes on the new event's trail
            Observed?.Invoke(EventName(name), payload);
            if (name == "Response") { _responseBody = Str(payload.GetValueOrDefault("body")); _responseStatus = AsLong(payload.GetValueOrDefault("status")); continue; }
            if (name == "Print") { _out?.WriteLine(Str(payload.GetValueOrDefault("text"))); continue; }
            // `@PrintError` — stderr when the host gave one (`veinc run`, a built exe, `veinc test`), else
            // the same output as @Print (the Workbench shows one stream).
            if (name == "PrintError") { Err?.WriteLine(Str(payload.GetValueOrDefault("text"))); continue; }
            // `@PrintInline` — no line ending, and flushed, or a prompt sits in the buffer until the user
            // has already typed the answer to it.
            if (name == "PrintInline") { _out?.Write(Str(payload.GetValueOrDefault("text"))); _out?.Flush(); continue; }

            // `@Quit { code }` — the program is done. What is already queued still runs (a save emitted
            // just before it lands), then nothing more is read: no input, no frames, no timers.
            if (name == "Quit") { _quitCode ??= (int)AsLong(payload.GetValueOrDefault("code")); continue; }

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
                var undelivered = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["to"] = to, ["text"] = body,
                    ["from"] = payload.GetValueOrDefault("from"), ["origin"] = null, ["bundle"] = _bundle
                };

                // The ONE place a transport is chosen, and the program never sees it happen. A mark that
                // has been `@Link`ed names a peer on another machine, so it goes over the wire; anything
                // else is a console on this one, so it goes over the pipe. That is the whole reason
                // console_roles.vein runs cross-machine by editing only its Boot shard: `@Send { to: #X }`
                // addresses an IDENTITY, and where that identity lives is the routing table's business.
                //
                // A live session never waits on the wire: a send that needs a connection is dialled by
                // NetBus's own thread, and a failure comes back through the inbox, as @Fetch's answer does.
                // Waiting here stalled every event behind a dead peer's connect timeout (2–5 s per send).
                // FAST: one datagram, fire and forget. When it cannot go fast (no fast channel yet, too
                // long) it goes the reliable way below — late rather than never.
                if (payload.GetValueOrDefault("fast") is true && NetBus.IsRemote(to) && NetBus.SendFast(to, _self, body))
                    continue;
                if (NetBus.IsRemote(to) && _inbox is { } box)
                {
                    NetBus.SendQueued(to, _self, body, () =>
                    { try { box.Add(() => { Emit("Undelivered", undelivered); Drain(); }); } catch { /* the run ended */ } });
                    continue;
                }
                bool sent = NetBus.IsRemote(to) ? NetBus.Send(to, _self, body) : ConsoleBus.Send(to, _self, body);

                // A console that was closed cannot be reached. A peer going away is NORMAL, so this is an
                // event the program can hear rather than an error — and never a silent drop, which would
                // leave a program unable to tell "delivered" from "shouting into a void".
                if (!sent) Emit("Undelivered", undelivered);
                continue;
            }
            if (name == "Listen") { DoListen(payload); continue; }
            if (name == "Link") { DoLink(payload); continue; }
            if (name == "Fetch") { DoFetch(payload); continue; }
            if (name == "ReadFile") { DoReadFile(payload); continue; }
            if (name == "WriteFile") { DoWriteFile(payload); continue; }
            if (name == "ListFolder") { DoListFolder(payload); continue; }
            if (name is "PickOpenFile" or "PickSaveFile") { DoPick(name, payload); continue; }

            // A DIAGNOSTIC IS NEVER DROPPED. Every other unheard event vanishing is the design — an
            // emit is not addressed to anyone. This one is different: a diagnostics library that loses
            // its own diagnostic is the single outcome it must not have, and that is exactly what
            // `*Vein.Diagnostics.Report.warn(…)` used to be in any program with no collector shard —
            // a call that emitted into a queue nobody read, and printed nothing.
            //
            // So: a shard that hears it wins, and the console is the fallback rather than the default.
            // A MUTED collector hears nothing, so it does not count — muting the only one must not turn
            // every diagnostic into silence.
            if (name == "DiagnosticRaised"
                && !(_handlers.TryGetValue("DiagnosticRaised", out var dh) && dh.Any(h => !IsMuted(h.Owner))))
            {
                long severity = AsLong(payload.GetValueOrDefault("severity"));
                if (severity >= 2) _faulted = true;   // an unheard ERROR fails the run: exit code 1
                Err?.WriteLine($"({SeverityWord(severity)}: {DiagnosticText(payload)})");
                continue;
            }

            if (!_handlers.TryGetValue(name, out var hs)) continue;
            foreach (var h in hs)
            {
                // Muted: the event is DROPPED for it, not held — `unmute` does not replay what it missed.
                if (IsMuted(h.Owner)) { _log.Add($"{h.Owner.Fc} MUTED @{name}"); continue; }
                // The SENDER the runtime recorded, not the payload's `from`: an event may declare a `from` of
                // its own, and a barrier that read that one would test whatever the emit wrote.
                if (!AudiencePermits(h, queued.Sender))
                {
                    var f = queued.Sender as Dictionary<string, object?>;
                    _log.Add($"{h.Owner.Fc} BLOCKS @{name} (audience) from {f?.GetValueOrDefault("kind")}.{f?.GetValueOrDefault("name")}");
                    continue;
                }
                var locals = new Dictionary<string, object?> { [h.BindName] = payload };
                RunGuarded(h.Body, h.Owner, locals, "hear @" + EventName(name), at: h.At,
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

    /// Set while DrainCapped's own diagnostic is being handled, so a runaway there cannot report itself again.
    private bool _reportingDrainCap;

    /// The queue handled 10,000 events in one drain: events that cause each other in a cycle, or one burst
    /// that large. With a budget that is OutOfGas, as before. Without one the drain still stops — a frame
    /// must end — and now it SAYS so: @DiagnosticRaised names the event next in line, and its shard, phase,
    /// file and line are where that event was emitted. It used to stop without a word, the rest of the queue
    /// waiting for a drain that, in a one-shot run, never came.
    ///
    /// The diagnostic is handled AT ONCE, with the events still queued set aside: in a live-lock it would
    /// otherwise wait behind them for ever, and a collector that prints it would print nothing. They go back
    /// afterwards, in order, and run at the next drain as they always did.
    private void DrainCapped()
    {
        const string what = "10,000 events in one drain";
        if (Budget > 0) throw new OutOfGas(what);
        if (_reportingDrainCap)
        {
            _faulted = true;
            Err?.WriteLine($"(error: {what}, while reporting one — the events left wait for the next drain)");
            return;
        }

        var next = _queue.Peek();
        var held = _queue.ToArray();
        _queue.Clear();
        var (shard, phase, unitAt) = next.Unit ?? ("", "", null);
        var place = next.Stmt is { } stmt ? new SourceAt(stmt.File, stmt.Line) : unitAt;
        Emit("DiagnosticRaised", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["severity"] = 2L,
            ["message"] = $"{what} — @{EventName(next.Name)} is still being emitted, and the events still queued wait for " +
                          "the next drain. Events that cause each other never settle: give the cycle a condition that ends " +
                          "it. A batch that large is better walked with `target` than sent as an event per item.",
            ["shard"] = shard, ["phase"] = phase, ["file"] = place?.File ?? "", ["line"] = place?.Line ?? 0L,
            ["origin"] = null, ["bundle"] = _bundle,
        });
        _reportingDrainCap = true;
        try { Drain(); }
        finally
        {
            _reportingDrainCap = false;
            foreach (var e in held) _queue.Enqueue(e);
        }
    }

    /// A `from` value exposes the emitter's name/kind, its opaque identity (display only), and the
    /// shapes/marks it carries (so an `audience` barrier can filter on them).
    private static Dictionary<string, object?> FromOf(VeinFirstClass fc, IEnumerable<string>? marks = null) => new(StringComparer.Ordinal)
    {
        ["name"] = fc.Name, ["kind"] = fc.Kind.ToString(), ["identity"] = fc.Identity.ToString(),
        ["key"] = fc.Key,   // what an `audience !Shard` matches: the linker renames `name`, never this
        ["shapes"] = fc.CarriedShapes.Cast<object?>().ToList(),
        // A shard's CURRENT marks (D20), snapshotted at the emit: a `mark !X #M` later does not rewrite
        // the provenance of what X already sent.
        ["marks"] = (marks ?? fc.CarriedMarks).Cast<object?>().ToList()
    };

    /// Read a two-list attribute (e.g. @audience(shapes, marks) / @carries(shapes, marks)).
    private static (IReadOnlyList<string> Shapes, IReadOnlyList<string> Marks) AttrLists(IReadOnlyList<IrAttr> attrs, string name)
    {
        var a = attrs.FirstOrDefault(x => x.Name == name);
        if (a is null || a.Args.Count < 2) return (Array.Empty<string>(), Array.Empty<string>());
        return (a.Args[0] as IReadOnlyList<string> ?? Array.Empty<string>(),
                a.Args[1] as IReadOnlyList<string> ?? Array.Empty<string>());
    }

    /// `@audience(shapes, marks, notShapes, notMarks, shards, notShards)`. IR from before D19 has only
    /// the first two lists, and reads as nothing excluded and no shard named.
    private static Audience AudienceOf(IReadOnlyList<IrAttr> attrs)
    {
        var a = attrs.FirstOrDefault(x => x.Name == "audience");
        IReadOnlyList<string> Arg(int i) =>
            a is not null && a.Args.Count > i ? a.Args[i] as IReadOnlyList<string> ?? Array.Empty<string>() : Array.Empty<string>();
        return new Audience(Arg(0), Arg(1), Arg(2), Arg(3), Arg(4), Arg(5));
    }

    /// The audience barrier: the emitter must carry ALL required shapes/marks, NONE of the excluded ones,
    /// be one of the named shards when any are named, and be none of the excluded shards (D19).
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
    private static bool AudiencePermits(Handler h, object? sender)
    {
        var aud = h.Aud;
        if (aud.IsEmpty) return true;

        HashSet<string> haveShapes, haveMarks;
        string? key;
        var raw = sender;
        if (raw is string address)
        {
            // The same rule, over a mark set of one: the console's own address. A console is no shard,
            // so it has no key — a named shard refuses it, an excluded one never matches it.
            haveShapes = new HashSet<string>(StringComparer.Ordinal);
            haveMarks = new HashSet<string>(StringComparer.Ordinal) { address };
            key = null;
        }
        else
        {
            var from = raw as Dictionary<string, object?>;
            haveShapes = (from?.GetValueOrDefault("shapes") as IEnumerable<object?>)?.Select(Str).ToHashSet() ?? new();
            haveMarks = (from?.GetValueOrDefault("marks") as IEnumerable<object?>)?.Select(Str).ToHashSet() ?? new();
            key = from?.GetValueOrDefault("key") as string;
        }

        return aud.Shapes.All(haveShapes.Contains) && aud.Marks.All(haveMarks.Contains)
            && !aud.NotShapes.Any(haveShapes.Contains) && !aud.NotMarks.Any(haveMarks.Contains)
            && (aud.Shards.Count == 0 || (key is not null && aud.Shards.Contains(key)))
            && !(key is not null && aud.NotShards.Contains(key));
    }

    /// Queue an event. `sender` is who the `audience` barrier tests — the payload's `from` unless the event
    /// declares a `from` of its own, when the emit site passes the real one.
    private void Emit(string ev, Dictionary<string, object?> payload, object? sender = null)
    {
        if (ev == "DiagnosticRaised") Stamp(payload);
        // Every event gets its own id (not an entity id) and a causation chain, kept in the queue entry so the
        // chain is the runtime's whatever the payload says. The payload gets them too — `e.id`, `e.cause`,
        // `e.trail` — except a name the event declares itself, which reads what the emit wrote.
        string id = "ev" + (++_eventSeq);
        var trail = new List<object?>();
        if (_currentEvent is { } cause) { trail.AddRange(cause.Trail); trail.Add(cause.Id); }
        if (!Owns(ev, "id")) payload["id"] = id;
        if (!Owns(ev, "cause")) payload["cause"] = _currentEvent?.Id ?? "";
        if (!Owns(ev, "trail")) payload["trail"] = new List<object?>(trail);   // id[] — the whole chain that led here
        _queue.Enqueue(new Queued(ev, payload, id, trail, sender ?? payload.GetValueOrDefault("from"), _unit, _at));
    }

    // ---- statements -----------------------------------------------------

    private Flow Exec(IrBlock block, Instance self, Dictionary<string, object?> locals)
    {
        var statements = block.Statements;
        for (int i = 0; i < statements.Count; i++)
        {
            var flow = ExecStmt(statements[i], self, locals);
            if (flow != Flow.Next) return flow;
        }
        return Flow.Next;
    }

    private Flow ExecStmt(IrStmt s, Instance self, Dictionary<string, object?> locals)
    {
        Burn();
        if (s.At is { } place) _at = place;   // a statement Lower generated runs as the one around it
        switch (s)
        {
            case IrBlock b: return Exec(b, self, locals);

            // `ordered by k` — the block runs FIRST with every bring deferred, then the deferred bodies
            // run sorted. Executing as we went would let an earlier bring change what a later key reads,
            // and the order would depend on itself. Same comparer as an ordered query, so both spell
            // "sorted" identically.
            case IrOrdered ord:
            {
                var frame = new List<Deferred>();
                _orderFrames.Push(frame);
                Flow collected;
                try { collected = Exec(ord.Collect, self, locals); }
                finally { _orderFrames.Pop(); }
                if (collected != Flow.Next) return collected;   // left before anything was brought

                // OrderBy is a STABLE sort, so brings that tie keep the order they were reached in —
                // which for a `target` loop is the order the rows arrived. Ties are common (a rank
                // column with duplicates), and arbitrary tie-breaking would make the output depend on
                // the sort's internals.
                long prevIndex = _currentIndex, prevEntity = _currentEntity;
                var prevBinds = new List<object?>(_targetBinds);
                var flow = Flow.Next;
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
                    _at = d.At ?? _at;   // its statements are the bring's, generated: a fault is the bring's line
                    if ((flow = Exec(d.Body, self, d.Locals)) != Flow.Next) break;
                }
                _currentIndex = prevIndex;
                _currentEntity = prevEntity;
                _targetBinds.Clear();
                _targetBinds.AddRange(prevBinds);
                return flow;
            }

            // A `bring` inside an `ordered by`. The key is read HERE, with this iteration's bindings; the
            // body is put aside with a copy of them, because by the time it runs the loop has moved on.
            case IrOrderedBring ob:
            {
                if (_orderFrames.Count == 0) return Exec(ob.Body, self, locals);
                // The copy below holds every local's list too, so none of them may grow in place any more.
                foreach (var held in locals.Values) if (held is GrowingList grown) grown.Shared = true;
                _orderFrames.Peek().Add(new Deferred(
                    Eval(ob.Key, self, locals),
                    ob.Body,
                    new Dictionary<string, object?>(locals, locals.Comparer),
                    new List<object?>(_targetBinds),
                    _currentIndex,
                    _currentEntity,
                    ob.At));
                return Flow.Next;
            }
            case IrLet l: locals[l.Name] = l.Init is null ? null : Eval(l.Init, self, locals); return Flow.Next;
            case IrAssign a: DoAssign(a, self, locals); return Flow.Next;
            case IrIf i:
                if (Truthy(Eval(i.Cond, self, locals))) return Exec(i.Then, self, locals);
                return i.Else is not null ? Exec(i.Else, self, locals) : Flow.Next;
            case IrLoop lp: return ExecLoop(lp, self, locals);
            case IrExprStmt e: Eval(e.Expr, self, locals); return Flow.Next;
            // Unwinds to the enclosing CallUser. The parser only admits `return` inside an `fn`, so a
            // stray one ends no more than the handler it is in.
            case IrReturn r:
                _returned = r.Value is null ? null : Eval(r.Value, self, locals);
                return Flow.Return;
            case IrBreak: return Flow.Break;
            case IrContinue: return Flow.Continue;
            case IrMatch m: return ExecMatch(m, self, locals);
            case IrWeave w: ExecWeave(w, self, locals); return Flow.Next;
            default: return Flow.Next;
        }
    }

    /// Every woven name in this run, and the two passes that use them — shared with the C# backend's emitted
    /// code (<see cref="Weaver"/>), so a woven record means the same thing on both runtimes.
    private readonly Weaver _weaver = new();

    /// <summary>`weave Loom(text) [as x]` (D23): every record brought as `bring Loom(…)` brings it.</summary>
    /// <remarks>
    /// The reading, naming and linking are <see cref="Weaver"/>'s. Here: each record's values become the locals
    /// the loom's generated bring reads (`__wv_x`, and `__wm_x` when the text left it out), and the bring runs
    /// onto the record's identity. RESULTS ARE EVENTS: each problem is one @Unwoven { loom, record, why }, in the
    /// order found; then @Woven { loom, count }. Both queue (RULES 21), so @Woven is handled after this event
    /// commits — when a `target` can see what was woven.
    /// </remarks>
    private void ExecWeave(IrWeave w, Instance self, Dictionary<string, object?> locals)
    {
        string text = Str(Eval(w.Source, self, locals));
        var result = _weaver.Weave(text, w.Format, w.Params,
            spawn: () => _store.Spawn(),
            isAlive: id => _store.IsAlive(id),
            destroyLater: id => _commands.Add(() => _store.Destroy(id)),
            bring: (identity, values) =>
            {
                foreach (var (name, v) in values)
                {
                    locals["__wm_" + name] = v.Missing;
                    locals["__wv_" + name] = v.Read;
                }
                locals["__wpre"] = identity;
                Exec(w.Record, self, locals);   // a loom's generated bring: it holds no return, break or continue
            });

        var problems = result.Problems.ToList();
        // `as x` names THE identity — so it means something only when the text held exactly one record.
        if (w.Bind is not null)
        {
            locals[w.Bind] = result.Count == 1 ? result.Brought : null;
            if (result.Count != 1) problems.Add(("", $"`as {w.Bind}`: the text holds {result.Count} records, so `{w.Bind}` names none of them"));
        }

        foreach (var (record, why) in problems)
            Emit("Unwoven", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["loom"] = w.Loom, ["record"] = record, ["why"] = why,
            });
        Emit("Woven", new Dictionary<string, object?>(StringComparer.Ordinal)
            { ["loom"] = w.Loom, ["count"] = (long)result.Count, ["ignored"] = result.Ignored });
    }

    /// <summary>`toText<Format>(Loom)` (D23, the way out): every identity the loom would make, as text.</summary>
    /// <remarks>
    /// The identities are the ones carrying all of the loom's shapes and marks, in query order — spawn order, so
    /// the order `weave` brought them in. Each parameter is read back from the shape it came from. A LINK is
    /// written as the NAME of the identity it points at (its first parameter, the inverse of weave's forward
    /// names); one that has no such name cannot be written, and is said rather than guessed. Problems arrive as
    /// @Unwoven, like a weave's — the record is left out, the rest are written.
    /// </remarks>
    private string ToText(IrToText t)
    {
        var problems = new List<(string Record, string Why)>();
        var records = new List<Dictionary<string, object?>>();
        string? nameShape = t.From.Count > 0 ? t.From[0] : null;
        bool refers = t.Params.Any(p => p.RefersByName);
        var names = new HashSet<string>(StringComparer.Ordinal);

        // A link is written as its target's NAME: the first field of the shape its elements carry (`~<list> cards:
        // owns $Card` → the card's `id`), or, for a plain `Entity`, this loom's own first parameter.
        string? NameOf(object? v, string? shape = null)
        {
            // The name its own loom gives it: the first field of a loomed shape it carries — the element's shape first.
            if (v is long linked && linked != 0 && _store.IsAlive(linked) && t.Names is { } names)
                foreach (var (s0, field0) in names.OrderBy(n => n.Shape == shape ? 0 : 1))
                    if (_store.Has(linked, s0)) return Str(_store.Read(linked, s0, field0));
            string? s = shape ?? nameShape;
            string? field = shape is null ? t.Params[0].Name : _store.FieldsOf(shape)?.FirstOrDefault()?.Name;
            return v is long id && id != 0 && s is not null && field is not null && _store.IsAlive(id) && _store.Has(id, s)
                ? Str(_store.Read(id, s, field)) : null;
        }

        foreach (long id in _store.Query(t.Shapes, t.Marks))
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            string label = NameOf(id) ?? "#" + id;
            string? why = null;
            for (int i = 0; i < t.Params.Count && why is null; i++)
            {
                var p = t.Params[i];
                object? raw = _store.Read(id, t.From[i]!, p.Name);
                if (p.Type == "Entity")
                {
                    if (raw is null || raw is 0L) values[p.Name] = "";
                    else if (NameOf(raw) is { } n) values[p.Name] = n;
                    else why = $"`{p.Name}` points at an identity with no `{t.Params[0].Name}` to write it by";
                }
                else if (p.IsList && p.Element == "Entity")
                {
                    var linked = new List<object?>();
                    foreach (var element in raw is System.Collections.IEnumerable e and not string ? e.Cast<object?>() : Enumerable.Empty<object?>())
                        if (NameOf(element, p.Carries) is { } n) linked.Add(n);
                        else problems.Add((label, $"`{p.Name}`: a linked identity has no `{t.Params[0].Name}` to write it by, so it is left out"));
                    values[p.Name] = linked;
                }
                else values[p.Name] = raw is System.Collections.IEnumerable e and not string ? e.Cast<object?>().ToList() : raw;
            }
            if (why is null && refers && !names.Add(label))
                why = $"another record is named \"{label}\" already, so a link to it could not be told apart";
            if (why is not null) { problems.Add((label, why)); continue; }
            records.Add(values);
        }

        string text = Weave.WriteAll(records, t.Format, t.Params, t.Defaults, problems);
        foreach (var (record, why) in problems)
            Emit("Unwoven", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["loom"] = t.Loom, ["record"] = record, ["why"] = $"toText<{t.Format.Name}>: \"{record}\": {why}",
            });
        return text;
    }

    /// `match subject { when Name { … } else { … } }`. An arm matches by NAME, because that is what both
    /// kinds of pattern evaluate to: an enum case and a mark each yield their own bare name. So
    /// `match here() { when #Alpha { … } }` is a switch on which console this process is running as.
    private Flow ExecMatch(IrMatch m, Instance self, Dictionary<string, object?> locals)
    {
        string subject = Str(Eval(m.Subject, self, locals));
        foreach (var arm in m.Arms)
            if (arm.CaseName == subject) return Exec(arm.Body, self, locals);
        return m.Else is not null ? Exec(m.Else, self, locals) : Flow.Next;
    }

    /// One pass of a loop body: Next to go on, Break to stop, Return to stop and pass the `return` up.
    ///
    /// Every loop kind goes through here so `break` and `continue` mean the same thing in all four —
    /// `while`, `repeat`, an identity `target` and a collection `target`. A `continue` inside a
    /// `target` still ends that entity's activation cleanly, because the caller's `finally` runs
    /// between this returning and the next iteration starting.
    private Flow Iterate(IrBlock body, Instance self, Dictionary<string, object?> locals)
    {
        var flow = Exec(body, self, locals);
        return flow == Flow.Continue ? Flow.Next : flow;
    }

    /// What a loop hands back once it has stopped: a `return` goes on up, a `break` ended here.
    private static Flow After(Flow last) => last == Flow.Return ? Flow.Return : Flow.Next;

    private Flow ExecLoop(IrLoop lp, Instance self, Dictionary<string, object?> locals)
    {
        var flow = Flow.Next;
        switch (lp.Kind)
        {
            case IrLoopKind.While:
            {
                // The condition is asked again after every turn, and a fault in it belongs to the `while`'s
                // line — not to the body's last statement, where the turn before left `_at`.
                var header = _at;
                for (int turns = 0; ; turns++)
                {
                    _at = header;
                    if (!Truthy(Eval(lp.Cond!, self, locals))) break;
                    if (turns >= 100_000)
                    {
                        Capped("a `while` loop ran 100,000 times",
                               "its condition never became false. If the work really is that long, do a part of it each tick");
                        break;
                    }
                    if ((flow = Iterate(lp.Body, self, locals)) != Flow.Next) break;
                }
                break;
            }
            case IrLoopKind.Repeat:
                { long n = AsLong(Eval(lp.Count!, self, locals));
                  long prevIdx = _currentIndex;
                  for (long i = 0; i < n; i++)
                  {
                      _currentIndex = i;                       // `Index` works here too; `as i` names the same number
                      if (lp.Var is not null) locals[lp.Var] = i;
                      if ((flow = Iterate(lp.Body, self, locals)) != Flow.Next) break;
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
                    // `on e`: that one identity, when it carries everything the query names (D22). Not a scan
                    // that keeps one: the store answers `Has` per shape and mark directly.
                    IEnumerable<long> matches = q.On is { } on
                        ? OneOf(AsLong(Eval(on, self, locals)), q)
                        : _store.Query(q.Components, q.Tags, q.OrderShape, q.OrderField, q.Excluded);
                    foreach (long entity in matches)
                    {
                        long previous = _currentEntity;
                        _currentEntity = entity;
                        _currentIndex = ++idx;
                        _targetBinds.Add(entity);
                        if (lp.Var is not null) locals[lp.Var] = entity;

                        _activations++;
                        _store.BeginActivation(entity);
                        bool more;
                        try { more = (flow = Iterate(lp.Body, self, locals)) == Flow.Next; }
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
                // Peeked: the loop walks a copy, so `xs += …` inside it cannot change what it walks.
                else if (lp.Source is not null && Peek(lp.Source, self, locals) is System.Collections.IEnumerable src and not string)
                {
                    long idx = -1, prevIdx = _currentIndex;
                    foreach (var item in src is IDictionary<string, object?> record ? Entries(record) : src.Cast<object?>().ToList())
                    {
                        _currentIndex = ++idx;
                        _targetBinds.Add(item);
                        if (lp.Var is not null) locals[lp.Var] = item;
                        bool more;
                        try { more = (flow = Iterate(lp.Body, self, locals)) == Flow.Next; }
                        finally { _targetBinds.RemoveAt(_targetBinds.Count - 1); }
                        if (!more) break;
                    }
                    _currentIndex = prevIdx;
                }
                break;
            }
        }
        return After(flow);
    }

    /// A record walked by `target d as kv` — a parsed object, a payload — as its ENTRIES, each a record with
    /// `key` and `value`, in the order the keys were first put: the same thing a `~<map>` walk gives (VeinMap),
    /// so `kv.key` reads alike on both. A key set again keeps its place, and nothing takes a key out of a record.
    ///
    /// It walked as .NET pairs, which nothing in the language can read: `kv.key` and `kv.value` were empty, and
    /// only `string(kv)` showed "[a, 1]" — so Vein Cards scanned the text `toJson` wrote to find a record's keys.
    internal static List<object?> Entries(IDictionary<string, object?> record) =>
        record.Select(e => (object?)new Dictionary<string, object?>(StringComparer.Ordinal) { ["key"] = e.Key, ["value"] = e.Value })
              .ToList();

    private IEnumerable<long> OneOf(long id, IrQuery q)
    {
        if (id > 0 && _store.IsAlive(id) && q.Components.All(c => _store.Has(id, c)) && q.Tags.All(t => _store.HasTag(id, t))
            && !q.Excluded.Any(t => _store.HasTag(id, t)))
            yield return id;
    }

    private void DoAssign(IrAssign a, Instance self, Dictionary<string, object?> locals)
    {
        // `xs += item` (`xs = xs + item`) on a local list: in place when only this variable holds it (GrowingList).
        // The two Burns are the `+` and the read of `xs` that Eval would have counted, so gas stays exact.
        if (a.Target is IrLocalRef target && a.Value is IrBinary { Op: IrBinOp.Add, Left: IrLocalRef left } add
            && left.Name == target.Name && locals.TryGetValue(target.Name, out var current) && current is IReadOnlyList<object?> held)
        {
            Burn(); Burn();
            var item = Eval(add.Right, self, locals);
            if (current is GrowingList own && !own.Shared) { ListOps.Append(own, item); return; }
            var grown = new GrowingList(held);
            ListOps.Append(grown, item);
            locals[target.Name] = grown;
            return;
        }

        var value = Eval(a.Value, self, locals);
        // Local first, as `Eval` reads it. Shard-first made a function's own `var cats` lose every write to
        // the CALLING shard's `var cats` — the fn returned its first value and the shard's changed, and a
        // Vein Cards tool wrote its page to a file named after the overwritten `let out` (2026-10-30).
        if (a.Target is IrLocalRef r)
        {
            if (!locals.ContainsKey(r.Name) && self.State.ContainsKey(r.Name)) self.State[r.Name] = value;
            else locals[r.Name] = value;
            return;
        }

        // `self.Health.hp = …` — a component field. The write lands in the activation's overlay, so it is
        // a fold CONTRIBUTION rather than an immediate mutation; see EntityStore.
        if (a.Target is IrFieldAccess { Receiver: var recvExpr } fa)
        {
            var recv = Eval(recvExpr, self, locals);
            if (recv is ComponentRef cr) { _store.Write(cr.Entity, cr.Shape, fa.Field, value); return; }
            // `row.title = …` on a record — a parsed JSON object is a dictionary, and setting a key on it
            // is how a program edits data it loaded before writing it back.
            if (recv is IDictionary<string, object?> record) { record[fa.Field] = value; return; }
        }

        // `xs[i] = v` / `m["k"] = v`. This silently did NOTHING — LANGUAGE.md's own `grid[i] = 0` with it —
        // so a program that built a list wrote into the void and read back what it started with. In place,
        // like the read: a list is the same list everywhere it is held.
        if (a.Target is IrIndex ix)
        {
            var recv = Peek(ix.Receiver, self, locals);
            var key = Eval(ix.Index, self, locals);
            ListOps.Set(recv, key, value);
        }
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

    /// A read that keeps no hold on what it reads — the receiver of `xs[i]`, the argument of `len(xs)` — so a
    /// GrowingList it finds stays this variable's alone. Counted like the Eval it stands in for.
    private object? Peek(IrExpr e, Instance self, Dictionary<string, object?> locals)
    {
        if (e is not IrLocalRef r || !locals.TryGetValue(r.Name, out var v)) return Eval(e, self, locals);
        Burn();
        return v;
    }

    /// Built-ins that read their arguments and answer something new, never the argument itself or a view of it.
    private static readonly HashSet<string> KeepsNothing = new(StringComparer.Ordinal)
    { "len", "join", "toJson", "string", "sort", "slice", "contains", "indexOf" };

    private object? Eval(IrExpr e, Instance self, Dictionary<string, object?> locals)
    {
        Burn();
        switch (e)
        {
            case IrLiteral l: return l.Value;
            case IrLocalRef r:
                if (locals.TryGetValue(r.Name, out var lv))
                {
                    if (lv is GrowingList grown) grown.Shared = true;   // whoever reads it may keep it
                    return lv;
                }
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
                if (locals.TryGetValue(sr.Bind, out var bound) && bound is GrowingList boundList) boundList.Shared = true;
                return locals.TryGetValue(sr.Bind, out bound) ? bound
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
                // `-x` of an int is an int (it was always a double: an int field negated turned float).
                return u.Op == IrUnOp.Not ? !Truthy(v) : v is long or int ? (object)unchecked(-AsLong(v)) : -AsDouble(v);
            }
            case IrRuntimeCall rc: return EvalRuntime(rc, self, locals);
            case IrToText tt: return ToText(tt);
            case IrList li: return li.Items.Select(i => Eval(i, self, locals)).ToList();
            case IrIndex ix:
            {
                var recv = Peek(ix.Receiver, self, locals);
                return Index(recv, Eval(ix.Index, self, locals));
            }
            case IrCall call:
                if (call.Callee is IrLocalRef fn)
                {
                    // A user `fn`/`SF` shadows nothing built-in: look it up first, then the prebuilts.
                    bool user = _functions.TryGetValue(fn.Name, out var decl);
                    bool keepsNothing = !user && KeepsNothing.Contains(fn.Name);
                    var args = new List<object?>(call.Args.Count);
                    foreach (var arg in call.Args) args.Add(keepsNothing ? Peek(arg, self, locals) : Eval(arg, self, locals));
                    return user ? CallUser(decl!, args, self) : Prebuilt(fn.Name, args);
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
        // Runaway recursion: stopped rather than blowing the stack — and SAID (Capped), where it used to
        // answer null and let the caller carry on computing with it.
        if (_callDepth >= 256)
        {
            Capped("calls nested 256 deep",
                   $"`{CallLabel(f)}` is called again before any call returns. A function that calls itself needs a case " +
                   "that stops; or write the repetition as a `while` loop");
            return null;
        }
        _callDepth++;
        _calls.Add(f);
        var caller = _at;   // the calling statement: the rest of it, after this call returns, is the caller's again
        try
        {
            var scope = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int i = 0; i < f.Params.Count; i++)
                scope[f.Params[i].Name] = i < args.Count ? args[i] : null;

            if (Exec(f.Body, self, scope) != Flow.Return) return null;   // fell off the end (an SF, or an fn with no return)
            var value = _returned;
            _returned = null;
            return value;
        }
        finally
        {
            _callDepth--;
            _calls.RemoveAt(_calls.Count - 1);
            _at = caller;
        }
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
        { "Response", "Print", "Console", "Send", "Listen", "Link", "Fetch", "ReadFile", "WriteFile", "ListFolder",
          "PickOpenFile", "PickSaveFile", "PlaySound", "Quit", "PrintError", "PrintInline" };

    /// <summary>The readable half of an event key: `me.T.Go` → `Go`.</summary>
    /// <remarks>
    /// KEYS ARE INTERNAL. An event is identified by `Author.Bundle.Name` so that two vendors' own
    /// `@Dispatch` are not silently one event — and none of that belongs in a sentence a person
    /// reads. A fault in a handler reported "Handler (hear @me.T.Go)" where the author had written
    /// `hear @Go`, which is the machine's bookkeeping leaking into their error message.
    /// </remarks>
    public static string EventName(string key)
    {
        int dot = key.LastIndexOf('.');
        return dot < 0 ? key : key[(dot + 1)..];
    }

    /// Every event the RUNTIME itself puts on the queue, under a bare name.
    ///
    /// WHY `Lower` NEEDS IT. An event is otherwise identified by `Author.Bundle.Name`, so that two
    /// vendors both calling something `@Dispatch` are not silently one event. These are the exception,
    /// and not a grudging one: the runtime raises them, so there is exactly one of each no matter who
    /// declares it. `@Request` is the sharpest case — a program declares its own, and `FireBoot`
    /// fires it by name, so qualifying it means the boot event is delivered to nobody.
    ///
    /// KEEP THIS IN STEP with the `Emit("…")` calls below and with `HostEvents` above, for the same
    /// reason `PrebuiltNames` says so: a name raised here and missing from this set becomes an event
    /// the program declares and the runtime can never deliver.
    public static readonly IReadOnlySet<string> RuntimeEvents =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Request", "Response", "DiagnosticRaised", "Failed", "Fetched",
            "FileLoaded", "FileMissing", "FileWritten", "FolderListed", "FilePicked", "PickCancelled",
            "Input", "Message", "Undelivered",
            "KeyDown", "KeyUp", "TextInput", "MouseDown", "MouseUp", "MouseMove", "MouseWorld",
            "ActionPressed", "ActionReleased", "ButtonDown", "ButtonUp", "AxisMoved",
            "Ticked", "SoundEnded", "AnimationEnded", "Collided", "Separated", "Clicked", "InputEnded",
            "Woven", "Unwoven",     // D23: a weave's results (ExecWeave), declared in stdlib/Weaving
        };

    /// The names `Prebuilt` below answers to. Declared as a set because `Lower` needs the same list: a
    /// built-in already resolves, so a `use`d bundle exporting the same name must not capture it (VS0217).
    /// Keep the two in step — a name added below and not here is silently rebindable by `use`.
    public static readonly IReadOnlySet<string> PrebuiltNames =
        new HashSet<string>(StringComparer.Ordinal)
        { "spawn", "here", "pick", "len", "random", "join", "toJson", "fromJson", "toText",
          "split", "lines", "words", "chars", "trim", "code", "chr", "upper", "lower",
          "contains", "startsWith", "endsWith", "indexOf", "substring", "replace",
          "int", "float", "string", "bool", "isNumber", "args", "now", "remove", "sort",
          "slice", "padLeft", "padRight", "env",
          // Vein.Crypto (stdlib/Crypto.vein): VeinCrypto, the one implementation compiled code calls too.
          "cryptoKeys", "cryptoPublic", "cryptoSeal", "cryptoOpen", "cryptoFingerprint",
          "cryptoLock", "cryptoUnlock", "cryptoHash", "cryptoToken", "cryptoSign", "cryptoVerify" };

    /// Prebuilt (built-in) functions that DO return a value — the only functions that return.
    ///
    /// `spawn()` is here rather than a keyword: `let e = spawn()` already parses and lowers, so creating
    /// an entity costs no lexer, parser or AstTree change — and nothing in the language creates one
    /// otherwise, which would leave every `target` query matching an empty world forever.
    /// A built-in that answers differently on each machine (the clock, the environment, fresh randomness)
    /// answers "" in a pure program — total, like every built-in, and the same everywhere.
    private object? Prebuilt(string name, List<object?> args) =>
        Pure && Purity.MachineDependent.Contains(name) ? "" : PrebuiltCore(name, args);

    private object? PrebuiltCore(string name, List<object?> args) => name switch
    {
        "spawn" => _store.Spawn(),
        // This console's own address. Every spawned console runs the SAME program, so `here() == #Main`
        // is how a program says "only the window the user launched does this".
        "here" => _self,
        // The program's command-line arguments — `veinc run f.vein -- a b`, or a built exe's own. A list, so
        // `len`, `args()[0]` and `target args() as a` all work on it.
        "args" => Args.Cast<object?>().ToList(),
        // Local wall-clock time, ISO 8601 to the second. Pulled, not delivered — a deliberate exception
        // (RULES 28f) — and fixed by `VEIN_NOW` so a test can expect an exact date.
        "now" => Now(),
        "pick" => args.Count > 0 && args[0] is IReadOnlyList<object?> { Count: > 0 } items
            ? items[_rng.Next(items.Count)]
            : null,
        "random" => _rng.NextDouble(),
        // A whole COMPONENT serialises, not just a scalar: `toJson(r.Row)` reads every field the shape
        // declares. An entity cannot — nothing can ask an identity which shapes it carries (RULES.md 14)
        // — so the thing you name is always a shape on one.
        "toJson" => Json.Write(args.Count > 0 ? Expand(args[0]) : null),
        _ => PureBuiltin(name, args)
    };

    /// The built-ins that need nothing but their arguments — no world, no clock, no randomness. Compiled code
    /// calls these same arms (VeinValues.Call), so a built-in cannot mean one thing interpreted and another
    /// compiled.
    internal static object? PureBuiltin(string name, List<object?> args) => name switch
    {
        // A list without element `i`, and a list sorted the way `by Shape.field` sorts — both NEW lists,
        // so `xs = remove(xs, 2)` reads as what it does.
        "remove" => args.Count > 1 ? ListOps.Remove(args[0], args[1]) : new List<object?>(),
        "sort" => args.Count > 0 ? ListOps.Sort(args[0]) : new List<object?>(),
        // A piece of a list, `substring`'s rule: start, then how many (all the rest when left out).
        "slice" => args.Count > 1 ? ListOps.Slice(args[0], args[1], args.Count > 2 ? args[2] : null) : new List<object?>(),
        // An environment variable, "" when it is not set — the same answer as a set-but-empty one, which is
        // what a program means by "not configured". Pulled, like `now()`: the environment is the host's.
        "env" => args.Count > 0 ? Environment.GetEnvironmentVariable(Str(args[0])) ?? "" : "",
        // Vein.Crypto: one implementation (VeinCrypto), the same one compiled code calls.
        "cryptoKeys" => VeinCrypto.NewKeys(),
        "cryptoPublic" => VeinCrypto.PublicKey(Str(args.ElementAtOrDefault(0))),
        "cryptoSeal" => VeinCrypto.Seal(Str(args.ElementAtOrDefault(0)), Str(args.ElementAtOrDefault(1)), Str(args.ElementAtOrDefault(2))),
        "cryptoOpen" => VeinCrypto.Open(Str(args.ElementAtOrDefault(0)), Str(args.ElementAtOrDefault(1)), Str(args.ElementAtOrDefault(2))),
        "cryptoFingerprint" => VeinCrypto.Fingerprint(Str(args.ElementAtOrDefault(0))),
        "cryptoLock" => VeinCrypto.Lock(Str(args.ElementAtOrDefault(0)), Str(args.ElementAtOrDefault(1))),
        "cryptoUnlock" => VeinCrypto.Unlock(Str(args.ElementAtOrDefault(0)), Str(args.ElementAtOrDefault(1))),
        "cryptoHash" => VeinCrypto.Hash(Str(args.ElementAtOrDefault(0))),
        "cryptoToken" => VeinCrypto.Token(AsLong(args.ElementAtOrDefault(0))),
        "cryptoSign" => VeinCrypto.Sign(Str(args.ElementAtOrDefault(0)), Str(args.ElementAtOrDefault(1))),
        "cryptoVerify" => VeinCrypto.Verify(Str(args.ElementAtOrDefault(0)), Str(args.ElementAtOrDefault(1)), Str(args.ElementAtOrDefault(2))),
        "len" => (long)(args.Count > 0 ? args[0] switch { System.Collections.ICollection c => c.Count, string s => s.Length, _ => 0 } : 0),
        "join" => args.Count > 1 && args[0] is System.Collections.IEnumerable e ? string.Join(Str(args[1]), e.Cast<object?>().Select(Str)) : "",
        "toJson" => Json.Write(args.Count > 0 ? args[0] : null),

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

        // `padLeft(id, 3)` is "  7": text widened to `width` with spaces (or the first character of `fill`),
        // for columns that line up. Never cut — text already wider comes back whole, because a table with
        // one long row is still right and a truncated title is not.
        "padLeft" => Pad(args, left: true),
        "padRight" => Pad(args, left: false),

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
    internal static long ToLong(object? v) => v switch
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
    internal static double ToDouble(object? v) => v switch
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
    internal static bool ToBool(object? v)
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
    private static string Pad(List<object?> args, bool left)
    {
        string s = args.Count > 0 ? Str(args[0]) : "";
        if (args.Count < 2) return s;
        int width = (int)Math.Clamp(AsLong(args[1]), 0, 10_000);
        string fill = args.Count > 2 ? Str(args[2]) : "";
        char c = fill.Length > 0 ? fill[0] : ' ';
        return left ? s.PadLeft(width, c) : s.PadRight(width, c);
    }

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
                            // The tail's `origin`/`source` are the envelope's; an event's OWN field of that
                            // name (`origin: string`) is filled like any other.
                            if ((fld.Name is "origin" or "source" && IsTail(fld)) || payload.ContainsKey(fld.Name)) continue;
                            if (fld.Default is not null) payload[fld.Name] = Eval(fld.Default, self, locals);
                            else if (_current is not null && _current.TryGetValue(fld.Name, out var cv)) payload[fld.Name] = cv;
                            else if (locals.TryGetValue(fld.Name, out var lv))
                            {
                                if (lv is GrowingList grown) grown.Shared = true;   // the payload holds it now
                                payload[fld.Name] = lv;
                            }
                            else if (si.FillRest) payload[fld.Name] = ZeroVal(fld.Type.Name);   // `?` placeholder
                        }
                    // Auto provenance. Two layers kept separate:
                    //   from   = the First-Class object that emitted (runtime identity)
                    //   origin = the ECS entity, when emitted inside a `target` (null outside one)
                    // A name the event declares itself is left as written (Owns): `@MeshDelivered { origin:
                    // string }` names a mesh node, and replacing it with the entity id read back a number.
                    // An `origin` this emit writes is kept too, as the compiled payload keeps it.
                    var sender = FromOf(self.Fc, self.Marks);
                    if (!Owns(si.TypeName, "from")) payload["from"] = sender;
                    if (!Owns(si.TypeName, "origin") && !si.Fields.Any(f => f.Field == "origin"))
                        payload["origin"] = _currentEntity == 0 ? null : _currentEntity;
                    if (!Owns(si.TypeName, "bundle")) payload["bundle"] = _bundle;
                    _edges.Add(new GraphEdge(self.Fc.Name, self.Fc.Kind.ToString(), si.TypeName));
                    _log.Add($"{self.Fc} [{self.Fc.Identity}] emits @{si.TypeName}");
                    Emit(si.TypeName, payload, sender);
                }
                return null;
            // Structural change. The arguments are evaluated NOW (they read the current world) but the
            // mutation is queued: it lands at CommitPhase, after the folds, so no unit in this phase can
            // observe a half-changed world and the phase's result stays order-independent.
            // `add v to x.Shape.~field` / `remove v from …` (D24). The element is checked NOW — inside the
            // unit, so a misfit is a fault with this unit's file and line — and the change is queued like a
            // mark: two shards adding in one tick both land, at the commit.
            case "CollAdd":
            case "CollRemove":
            case "CollClear":
            case "CollPut":
            case "CollMove":
            {
                long e = AsLong(Eval(rc.Args[0], self, locals));
                string shape = Str(Eval(rc.Args[1], self, locals));
                string field = Str(Eval(rc.Args[2], self, locals));
                object? value = rc.Args.Count > 3 ? Eval(rc.Args[3], self, locals) : null;
                object? put = rc.Args.Count > 4 ? Eval(rc.Args[4], self, locals) : null;
                string op = rc.Name switch { "CollAdd" => "add", "CollRemove" => "remove", "CollClear" => "clear", "CollMove" => "move", _ => "put" };
                var spec = _store.FieldsOf(shape)?.FirstOrDefault(f => f.Name == field)?.Collection;
                if (spec is not null && op != "clear")
                {
                    string where = $"{shape}.~{field}";
                    if (spec.Key is not null)
                    {
                        value = CollectionRules.Element(value, spec.Key, where, op == "put" ? "key of" : "remove from");
                        if (op == "put") put = CollectionRules.Element(put, spec.Element, where, "value of");
                    }
                    else value = CollectionRules.Element(value, spec.Element, where, op == "add" ? "add to" : op == "move" ? "move in" : "remove from");
                }
                // A `$Shape` element is checked AT the commit, after the commands queued before it — so an
                // identity brought earlier in this event counts — and refused with this unit's place, at the
                // statement that asked for the change.
                var unit = _unit;
                var asked = _at;
                _commands.Add(() =>
                {
                    if (_store.ChangeCollection(e, shape, field, op, value, put)) return;
                    var (bad, need) = op == "put" && !CarriesNow(value, spec?.KeyCarries) ? (value, spec?.KeyCarries) : (op == "put" ? put : value, spec?.Carries);
                    CommitFault(unit, asked, $"{op} {shape}.~{field}: identity {Str(bad)} does not carry ${need}");
                });
                return null;
            }
            case "CollHas":
            {
                var coll = Peek(rc.Args[0], self, locals);
                var v = Eval(rc.Args[1], self, locals);
                return CollectionRules.Holds(coll, v);   // a map is asked about a KEY
            }
            case "AddTag":
            case "RemoveTag":
            {
                long e = AsLong(Eval(rc.Args[0], self, locals));
                string mark = Str(Eval(rc.Args[1], self, locals));
                bool add = rc.Name == "AddTag";
                _commands.Add(() => { if (add) _store.AddTag(e, mark); else _store.RemoveTag(e, mark); });
                return null;
            }
            // `mute X` / `unmute X` (D18). Queued like a mark, so it lands at the commit: the shard still
            // finishes the event or phase in progress, whichever of the two runs first. A key with no
            // shard — `mute` in an imported SF whose bundle is not linked — mutes nothing.
            case "Mute":
            case "Unmute":
            {
                bool mute = rc.Name == "Mute";
                if (_byKey.TryGetValue(Str(Eval(rc.Args[0], self, locals)), out var target))
                    _commands.Add(() => target.Muted = mute);
                return null;
            }
            // `!Boards.hideDone` (D28): a shard's setting — read as it stands, written at the commit like `mute`.
            case "ShardSetting":
                return _byKey.TryGetValue(Str(Eval(rc.Args[0], self, locals)), out var owner)
                    ? owner.State.GetValueOrDefault(Str(Eval(rc.Args[1], self, locals)))
                    : null;
            case "SetShardSetting":
            {
                string name = Str(Eval(rc.Args[1], self, locals));
                var value = Eval(rc.Args[2], self, locals);
                if (rc.Args[3] is IrLiteral { Value: "float" } && value is long whole) value = (double)whole;
                if (_byKey.TryGetValue(Str(Eval(rc.Args[0], self, locals)), out var target))
                    _commands.Add(() => target.State[name] = value);
                return null;
            }
            // `x carries #M` / `x carries $S` (D20). The subject says where to look: a sender's `from`
            // carries what its shard carried when it emitted; a console's `from` is its address, which
            // it carries as a mark (the audience rule); an entity id asks the store, which answers for
            // the COMMITTED world like every other read. Anything else carries nothing.
            case "Carries":
            {
                var subject = Eval(rc.Args[0], self, locals);
                string name = Str(Eval(rc.Args[1], self, locals));
                bool shape = rc.Args[2] is IrLiteral { Value: true };
                return subject switch
                {
                    Dictionary<string, object?> from =>
                        (from.GetValueOrDefault(shape ? "shapes" : "marks") as IEnumerable<object?>)?.Any(x => Str(x) == name) == true,
                    string address => !shape && address == name,
                    long or int or double => shape ? _store.Has(AsLong(subject), name) : _store.HasTag(AsLong(subject), name),
                    _ => false,
                };
            }
            // `mute #M` / `unmute #M` (D20): the standing rule, queued like the rest.
            case "MuteMark":
            case "UnmuteMark":
            {
                bool mute = rc.Name == "MuteMark";
                string mark = Str(Eval(rc.Args[0], self, locals));
                _commands.Add(() => { if (mute) _mutedMarks.Add(mark); else _mutedMarks.Remove(mark); });
                return null;
            }
            // `mark !S #M` / `unmark !S #M` (D20): a shard's own marks, read by `audience`, `carries` and
            // `mute #M` from the commit on.
            case "MarkShard":
            case "UnmarkShard":
            {
                bool add = rc.Name == "MarkShard";
                string mark = Str(Eval(rc.Args[1], self, locals));
                if (_byKey.TryGetValue(Str(Eval(rc.Args[0], self, locals)), out var target))
                    _commands.Add(() => { if (add) target.Marks.Add(mark); else target.Marks.Remove(mark); });
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
        return Binary(b.Op, l, r);
    }

    /// `recv[key]`. An index truncates toward zero, so `list[random() * len(list)]` picks an element and needs
    /// no cast. Out of range reads null rather than throwing — a runtime is not a place to crash a user's
    /// console app.
    internal static object? Index(object? recv, object? key)
    {
        if (recv is IReadOnlyList<object?> list)
        { long i = AsLong(key); return i >= 0 && i < list.Count ? list[(int)i] : null; }
        if (recv is IDictionary<string, object?> map) return map.TryGetValue(Str(key), out var mv) ? mv : null;
        if (recv is VeinMap vm) return vm[key];   // `b.Board.~scores["ann"]` — the value type's zero when missing
        if (recv is string str)
        { long i = AsLong(key); return i >= 0 && i < str.Length ? str[(int)i].ToString() : null; }
        return null;
    }

    /// Every operator but `and`/`or` on two values already computed — the interpreter's and, through
    /// VeinValues, compiled code's: one implementation, so the two runtimes cannot answer differently.
    internal static object? Binary(IrBinOp op, object? l, object? r)
    {
        switch (op)
        {
            case IrBinOp.Add:
                // A LIST on the left builds a list: `xs += item` appends, `xs + ys` joins. It fell into
                // number addition and answered 0, so a list could never grow (D4: mutation is `+=`).
                if (l is IReadOnlyList<object?>) return ListOps.Plus(l, r);
                if (l is string || r is string) return Str(l) + Str(r);   // string concat (prebuilt +)
                // TWO INTS STAY EXACT INTS, in 64 bits as compiled code computes them. Through a double they
                // lost precision past 2^53, and `Num` below returned a double for every `-`, `*` and `/` —
                // `cond ? (long)v : v` has ONE type, double — so `balance -= amount` turned an int field into
                // a float on the interpreter only (found by a state hash that differed between the runtimes).
                if (IsInt(l) && IsInt(r)) return unchecked(AsLong(l) + AsLong(r));
                return AsDouble(l) + AsDouble(r);
            case IrBinOp.Sub: return IsInt(l) && IsInt(r) ? (object)unchecked(AsLong(l) - AsLong(r)) : Num(AsDouble(l) - AsDouble(r), l, r);
            case IrBinOp.Mul: return IsInt(l) && IsInt(r) ? (object)unchecked(AsLong(l) * AsLong(r)) : Num(AsDouble(l) * AsDouble(r), l, r);
            // Integer division truncates toward zero, as C#'s does; by zero it stays the old answer (no throw).
            case IrBinOp.Div: return IsInt(l) && IsInt(r) && AsLong(r) != 0 && !(AsLong(l) == long.MinValue && AsLong(r) == -1)
                                     ? (object)(AsLong(l) / AsLong(r)) : Num(AsDouble(l) / AsDouble(r), l, r);
            // `%` alone truncated BOTH operands to long, so `7.5 % 2.0` was 1 rather than 1.5 while
            // `7 / 2.0` was correctly 3.5 — and the C# backend emits `%` on doubles, so the two
            // runtimes disagreed silently about every float remainder.
            //
            // TWO INTS STAY INTEGER MODULO, which is not merely "the part that was right": it is the
            // one operation reachable from VeinScript that genuinely throws, and `DiagnosticsTests`
            // uses `5 % 0` as its only way to produce a fault. Routing ints through doubles made that
            // NaN, and `Num` then cast NaN to long — so a division by zero became -9.2e18, a number
            // that looks like an answer. Float remainder is IEEE, so `7.5 % 0.0` is NaN and says so.
            case IrBinOp.Mod:
                return IsInt(l) && IsInt(r) ? AsLong(l) % AsLong(r)
                                            : (object)(AsDouble(l) % AsDouble(r));
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
    internal static int Ordering(object? l, object? r)
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

    private static object Num(double v, object? l, object? r) => IsInt(l) && IsInt(r) ? (object)(long)v : v;
    internal static bool IsInt(object? o) => o is long or int;
    internal static bool Truthy(object? o) => o switch { null => false, bool b => b, long n => n != 0, double d => d != 0, string s => s.Length > 0, _ => true };
    internal static long AsLong(object? o) => o switch { long l => l, int i => i, double d => (long)d, _ => 0 };
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
    /// A value going into a collection of `element` (D24): converted where the conversion is exact (an
    /// integral float into `int`, an int into `float`), refused otherwise — a fault, so the program hears
    /// it with a place, instead of a collection quietly holding the wrong kind of thing.
    private bool CarriesNow(object? value, string? shape) => shape is null || (value is long id && _store.Has(id, shape));

    /// A collection change refused at the commit (D24) — reported as a fault of the unit that wrote it.
    private void CommitFault((string Shard, string Phase, SourceAt? At)? unit, IrLine? asked, string message)
    {
        var (shard, phase, unitAt) = unit ?? ("", "", null);
        var at = asked is { } stmt ? new SourceAt(stmt.File, stmt.Line) : unitAt;
        string where = shard == "" ? "" : Where(shard, phase, at) + ": ";
        Fault?.Invoke(new InterpFault(_tick, shard, phase, null, new InvalidOperationException(message)));
        Emit("DiagnosticRaised", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["severity"] = 2L, ["message"] = where + message,
            ["shard"] = shard, ["phase"] = phase, ["file"] = at?.File ?? "", ["line"] = at?.Line ?? 0L,
            ["origin"] = null, ["bundle"] = _bundle,
        });
    }

    internal static bool LooseEq(object? l, object? r) =>
        IsNumeric(l) && IsNumeric(r)
            ? AsDouble(l) == AsDouble(r)
            : string.Equals(Str(l), Str(r), StringComparison.Ordinal);

    private static bool IsNumeric(object? o) => o is double or long or int or bool;

    internal static double AsDouble(object? o) => o switch { double d => d, long l => l, int i => i, bool b => b ? 1 : 0, _ => 0 };
    /// Any runtime value as text. The list arm is not a nicety: without it `"lines = " + lines` printed
    /// `System.Collections.Generic.List` plus a mangled type argument — a .NET implementation detail
    /// leaking into the output of a program written in a language that has no .NET in it. `join(list,
    /// sep)` is still how you render a list deliberately; this is what it looks like when you did not ask.
    internal static string Str(object? o) => o switch
    {
        null => "",
        string s => s,
        double d => d.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        List<object?> or VeinMap or IDictionary<string, object?> => Nested(o, new HashSet<object>(ReferenceEqualityComparer.Instance)),
        _ => o.ToString() ?? ""
    };

    /// A list, a `~<map>` or a RECORD — a parsed object, a payload, an entry `target d as kv` binds — as text.
    /// A record is written as a map is; it printed `System.Collections.Generic.Dictionary` and its type
    /// arguments, so `"" + d` showed .NET, and `==` (which compares anything else as text) found EVERY two
    /// records equal. Shared state is often a record holding itself (`db.match.db`), so one already being
    /// written is `{…}` rather than a walk that never ends.
    private static string Nested(object? o, HashSet<object> open)
    {
        string Part(object? v) => v is List<object?> or VeinMap or IDictionary<string, object?> ? Nested(v, open) : Str(v);
        if (o is null) return "";
        if (!open.Add(o)) return o is List<object?> ? "[…]" : "{…}";
        try
        {
            return o switch
            {
                List<object?> xs => "[" + string.Join(", ", xs.Select(Part)) + "]",
                VeinMap m => "{" + string.Join(", ", m.Entries.Select(e => Part(e.Key) + ": " + Part(e.Value))) + "}",
                IDictionary<string, object?> r => "{" + string.Join(", ", r.Select(e => e.Key + ": " + Part(e.Value))) + "}",
                _ => Str(o),
            };
        }
        finally { open.Remove(o); }
    }
    private static object? Default(string typeName) => typeName switch { "string" or "Mark" => "", "int" => 0L, "float" => 0.0, "bool" => false, _ => null };

    // Typed zero used to satisfy required fields under `?` (fill-the-rest). A `Mark` (an identity
    // reference — e.g. a console address) has no meaningful zero, so it names the reserved root.
    private static object? ZeroVal(string typeName) => typeName switch
    {
        "int" or "Entity" => 0L, "float" => 0.0, "bool" => false, "percent" => 0.0,
        "Mark" => RootConsole, "string" => "", _ => ""
    };
}
