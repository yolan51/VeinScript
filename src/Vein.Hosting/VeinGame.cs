using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Project;
using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Vein.Jit;

namespace Vein.Hosting;

/// A VeinScript program run by an engine: the engine owns the window, the frame clock, input, drawing and
/// sound; the program owns the rules. See docs/HOSTING.md for the per-frame contract.
///
/// Two runtimes behind one API (IGameRuntime): the interpreter, which runs the whole language, and the
/// rules compiled to C# — ~14–25× faster — when the whole program compiles with nothing missing
/// (VeinRuntime.Auto, the default). Every answer below (Drawables, Camera, Entities, …) is computed the
/// same way from either, which is what keeps them giving the same answers. Single-threaded, like the
/// interpreter: call it from the engine's main thread (ReloadInBackground compiles on a worker, and swaps
/// on the caller's next Step).
public sealed class VeinGame
{
    private IGameRuntime? _runtime;       // running
    private IGameRuntime? _pending;       // opened, not started
    private Prepared? _prepared;          // a background reload, waiting for the next Step to swap in
    private bool _preparedKeepsState;     // …and whether it carries the running world across
    private IrModule? _module;            // the running program, for comparing it with an edited one
    private IrModule? _pendingModule;
    private IReadOnlyList<VeinEntity>? _entities;
    private IReadOnlyList<VeinDrawable>? _drawables;
    private IReadOnlyList<VeinModel>? _models;
    private IReadOnlyList<VeinCollider>? _colliders;
    private readonly VeinRuntime _mode;
    private readonly RuntimeSinks _sinks;

    private VeinGame(string path, VeinRuntime mode)
    {
        Path = System.IO.Path.GetFullPath(path);
        _mode = mode;
        _sinks = new RuntimeSinks(
            (line, isError) => Printed?.Invoke(line, isError),
            (source, volume) => Sound?.Invoke(source, volume),
            (name, payload) => Emitted?.Invoke(name, payload),
            () => Emitted is not null);
    }

    /// The `.vein` file or `app` manifest this game runs.
    public string Path { get; }

    /// What the last load or reload reported, warnings included. A failed `Reload` leaves the previous
    /// program running and puts its errors here.
    public IReadOnlyList<Diagnostic> Diagnostics { get; private set; } = Array.Empty<Diagnostic>();

    /// "compiled" or "interpreter" — which runtime runs the rules (or will, once started); "none" before
    /// anything compiled.
    public string Runtime => (_runtime ?? _pending)?.Name ?? "none";

    /// Why the rules are NOT compiled, when compiling was asked for (Auto or Compiled) and they could not
    /// be without losing behaviour — e.g. "`every N` needs a wall clock". Empty when they are compiled, or
    /// when the interpreter was asked for.
    public string RuntimeNote { get; private set; } = "";

    /// True while a program is loaded and running.
    public bool Running => _runtime?.Running ?? false;

    /// Marks under a standing `mute #M` right now (D20), and the keys of every shard currently silent —
    /// by `mute Name` or by carrying a muted mark. For a debugger's "what is paused" line.
    public IReadOnlyList<string> MutedMarks => _runtime?.MutedMarks ?? Array.Empty<string>();
    public IReadOnlyList<string> MutedShards => _runtime?.MutedShards ?? Array.Empty<string>();

    /// Frames stepped since the program booted.
    public int Tick => _runtime?.Tick ?? 0;

    /// A line the program printed (`@Print`), and whether it was an error (`@PrintError`, or a runtime
    /// fault nobody heard).
    public event Action<string, bool>? Printed;

    /// `*Vein.Audio.Sound.@PlaySound` — the file the program named, and a volume in 0..1.
    public event Action<string, double>? Sound;

    /// Every event the program handles, by its readable name — for events a host acts on beyond sound and
    /// printing (`@ShowMenu`, `@ShakeCamera`). The payload belongs to the program; read, don't keep.
    public event Action<string, IReadOnlyDictionary<string, object?>>? Emitted;

    /// A background reload (ReloadInBackground) was swapped in at the start of a Step: the rules restarted
    /// from their boot, with fresh identity ids.
    public event Action? Reloaded;

    /// Compile and boot. Never throws for a bad program: check `Running` and `Diagnostics`. A host that
    /// wants what the program prints or emits WHILE BOOTING (a score's first value, a welcome line) uses
    /// `Open`, subscribes, then `Start` — here the boot has already happened by the time `Load` returns.
    public static VeinGame Load(string path, VeinRuntime runtime = VeinRuntime.Auto)
    {
        var game = Open(path, runtime);
        game.Start();
        return game;
    }

    /// Compile without booting, so the host can subscribe to `Printed`, `Sound` and `Emitted` first.
    /// `Diagnostics`, `Runtime` and `RuntimeNote` are filled; nothing runs until `Start`.
    public static VeinGame Open(string path, VeinRuntime runtime = VeinRuntime.Auto) => Open(path, runtime, null);

    /// `Open`, with the rules already compiled AHEAD OF TIME (`Precompile`, run by a game's export): the
    /// bytes are loaded instead of compiling at start — no compiler runs, nothing waits a second. The source
    /// is still compiled by the front end (diagnostics, and the interpreter if the bytes do not load or the
    /// interpreter was asked for). Used once: a reload compiles afresh from the source.
    public static VeinGame Open(string path, VeinRuntime runtime, byte[]? precompiled)
    {
        var game = new VeinGame(path, runtime) { _precompiled = precompiled };
        var prepared = game.Prepare();
        game._precompiled = null;
        game.Diagnostics = prepared.Diagnostics;
        game.RuntimeNote = prepared.Note;
        game._pending = prepared.Runtime;
        game._pendingModule = prepared.Module;
        return game;
    }

    /// The rules of `path` compiled to assembly bytes for `Open(path, runtime, bytes)` — what an engine's
    /// export step ships so the game never compiles at start. Null, with the reason, when they cannot be
    /// compiled without losing behaviour (the same notes that send `Auto` to the interpreter) or do not
    /// compile at all; the game then runs them on the interpreter, as it would have anyway.
    public static byte[]? Precompile(string path, out string reason)
    {
        var (module, diagnostics) = Compile(System.IO.Path.GetFullPath(path));
        if (module is null)
        {
            reason = diagnostics.FirstOrDefault(d => d.Severity == Severity.Error)?.ToString() ?? "does not compile";
            return null;
        }
        var image = VeinBuild.CompileImageForHost(module);
        reason = image.Reason;
        return image.Image;
    }


    /// Every diagnostic for `source` as if it were the file at `path` — the unsaved text in an editor, so
    /// a host's editor can underline problems while the user types. The same front end `Open` uses: `need`,
    /// a bundle's `publicators/`/`shards/` fragments and an app's `load` resolve from `path`'s folder, from
    /// disk. Nothing is compiled to C# and nothing runs; a few milliseconds for a game-sized file.
    public static IReadOnlyList<Diagnostic> Check(string path, string source)
    {
        string full = System.IO.Path.GetFullPath(path);
        var diagnostics = Compile(full, source).Diagnostics.ToList();
        // What is wrong in vein.settings (D21) that compiling did not already say: a line naming no setting.
        foreach (var d in SettingsFor(full).Problems)
            if (!diagnostics.Any(x => x.Code == d.Code && x.Span.Line == d.Span.Line && x.Span.File == d.Span.File))
                diagnostics.Add(d);
        return diagnostics;
    }
    /// True when the running rules were loaded from precompiled bytes rather than compiled at start.
    public bool Precompiled { get; private set; }

    /// The VeinScript libraries (packages such as VeinKit) found beside the app and searched by `need` — for
    /// a host's UI that lists them, and opens their bundles read-only.
    public static IReadOnlyList<VeinLibraries.Library> Libraries => VeinLibraries.All;

    // ---- settings (D21) -------------------------------------------------------------------------------

    /// One SETTING — a shard var under `tune("…")` — of the program or of a library it needs: its default
    /// (the source's literal), the value `vein.settings` saves for it (null: the default), and its value in
    /// the running game right now (null when not running).
    public sealed record Setting(
        string Key, string Section, string Name, string Type, string Default, string? Saved, object? Live,
        string Doc, string File, int Line, bool IsLibrary);

    /// The project's `vein.settings`, where SaveSetting writes. A host watches it as it watches `.vein`
    /// files and reloads on a change (keeping the game), which is how a saved value reaches the rules.
    public string SettingsPath => SettingsFile.Locate(System.IO.Path.GetDirectoryName(Path)!);

    /// Every setting this program has, for a host's Settings panel.
    public IReadOnlyList<Setting> Settings
    {
        get
        {
            var live = _runtime?.ShardVars();
            return Index.Settings.Select(s => new Setting(
                s.Key, s.Section, s.Name, s.Type, s.Default, s.Value,
                live is not null && live.TryGetValue(s.Section + "." + s.Shard, out var vars) && vars.TryGetValue(s.Var, out var v) ? v : null,
                s.Doc, s.File, s.Line, s.IsLibrary)).ToList();
        }
    }

    /// Change a setting in the RUNNING game only — while a slider is dragged. Nothing is saved: the next
    /// reload or start goes back to the saved value. False when there is no such setting or the value does
    /// not fit its type.
    public bool Tune(string key, object? value)
    {
        if (_runtime is null || Index.Settings.FirstOrDefault(s => s.Key == key) is not { } s) return false;
        object? typed = s.Type switch
        {
            "int" => value is IConvertible c ? Convert.ToInt64(c, System.Globalization.CultureInfo.InvariantCulture) : null,
            "float" or "percent" => value is IConvertible c ? Convert.ToDouble(c, System.Globalization.CultureInfo.InvariantCulture) : null,
            "bool" => value as bool?,
            _ => value?.ToString(),
        };
        if (typed is null) return false;
        bool ok = _runtime.SetShardVar(s.Section + "." + s.Shard, s.Var, typed);
        if (ok) Changed();
        return ok;
    }

    /// Save a setting's value — a VeinScript literal (`6.5`, `30%`, `"text"`, `true`) — to `vein.settings`,
    /// and apply it to the running game now. False when there is no such setting or the literal does not
    /// fit its type.
    public bool SaveSetting(string key, string literal)
    {
        if (Index.Settings.FirstOrDefault(s => s.Key == key) is not { } s) return false;
        if (SettingsFile.ParseValue(literal, SettingsIndex.KindOf(s.Type), default) is not { } parsed) return false;
        SettingsFile.Set(SettingsPath, s.Section, s.Name, literal.Trim());
        _settingsIndex = null;
        if (_runtime is not null) Tune(key, LiteralValue(parsed));
        return true;
    }

    /// Remove a setting from `vein.settings` — back to its source default, now too. False when it had none.
    public bool ResetSetting(string key)
    {
        if (Index.Settings.FirstOrDefault(s => s.Key == key) is not { } s) return false;
        bool removed = SettingsFile.Remove(SettingsPath, s.Section, s.Name);
        _settingsIndex = null;
        if (removed && _runtime is not null && SettingsFile.ParseValue(s.Default, SettingsIndex.KindOf(s.Type), default) is { } parsed)
            Tune(key, LiteralValue(parsed));
        return removed;
    }

    /// The settings of the program at `path` without running it — for an editor's Settings panel. `Live` is null.
    public static IReadOnlyList<Setting> SettingsOf(string path) =>
        SettingsFor(System.IO.Path.GetFullPath(path)).Settings.Select(s => new Setting(
            s.Key, s.Section, s.Name, s.Type, s.Default, s.Value, null, s.Doc, s.File, s.Line, s.IsLibrary)).ToList();

    /// Save a setting of the program at `path` to its project's `vein.settings`. A running game that
    /// watches the file (SettingsPath) reloads and takes it. False when there is no such setting or the
    /// literal does not fit its type.
    public static bool SaveSetting(string path, string key, string literal)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (SettingsFor(full).Settings.FirstOrDefault(s => s.Key == key) is not { } s) return false;
        if (SettingsFile.ParseValue(literal, SettingsIndex.KindOf(s.Type), default) is null) return false;
        SettingsFile.Set(SettingsFile.Locate(System.IO.Path.GetDirectoryName(full)!), s.Section, s.Name, literal.Trim());
        return true;
    }

    /// Remove a setting of the program at `path` from `vein.settings`: back to its default.
    public static bool ResetSetting(string path, string key)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (SettingsFor(full).Settings.FirstOrDefault(s => s.Key == key) is not { } s) return false;
        return SettingsFile.Remove(SettingsFile.Locate(System.IO.Path.GetDirectoryName(full)!), s.Section, s.Name);
    }

    private static object? LiteralValue(Compiler.Parsing.Expr e) => e switch
    {
        Compiler.Parsing.UnaryExpr { Operand: Compiler.Parsing.LiteralExpr l } => l.Value switch { long n => -n, double d => -d, var o => o },
        Compiler.Parsing.LiteralExpr { Kind: Compiler.Parsing.LiteralKind.Percent, Value: var p } => Convert.ToDouble(p) / 100.0,
        Compiler.Parsing.LiteralExpr l => l.Value,
        _ => null,
    };

    /// This program's settings index, read once and again after a reload, a save or a reset — a debugger asks
    /// for Settings several times a second, and the index parses files.
    private SettingsIndex.Result Index => _settingsIndex ??= SettingsFor(Path);
    private SettingsIndex.Result? _settingsIndex;

    /// The settings index, resolving libraries as a compile does (they are beside the game, not listed).
    private static SettingsIndex.Result SettingsFor(string path)
    {
        using var _ = BundleSearch.Scope(StdlibCache.Root);
        using var __ = BundleSearch.WithLibraries(VeinLibraries.Roots);
        return SettingsIndex.For(path);
    }

    private byte[]? _precompiled;

    /// Boot what `Open` compiled. False when it did not compile (see `Diagnostics`) or already started.
    public bool Start()
    {
        if (_pending is not { } runtime) return false;
        _pending = null;
        _module = _pendingModule;
        Boot(runtime);
        return true;
    }

    /// Recompile from disk — what a host calls when a `.vein` file changes. If it does not compile, the
    /// running program is KEPT and the errors are in `Diagnostics`: a typo must not stop the game.
    ///
    /// `keepState: false` boots the new program from its own boot, fresh ids and all. `keepState: true`
    /// carries the running world across the edit instead (see Carry): every identity keeps its id and the
    /// shapes and marks the new program still declares, every shard `var` keeps its value UNLESS its
    /// declared initial value was what the edit changed (so `speed = 5.0` → `9.0` takes effect, while
    /// `score` survives), mutes stay as they were, and neither `run once` nor the boot runs again.
    public bool Reload(bool keepState = false)
    {
        var prepared = Prepare();
        Diagnostics = prepared.Diagnostics;
        if (prepared.Runtime is null) return false;
        RuntimeNote = prepared.Note;
        _pending = null;
        Swap(prepared, keepState);
        return true;
    }

    /// `Reload`, with the compiling done on a worker thread — compiled rules can take a second to build,
    /// and a game should not freeze for it. The new rules are swapped in at the start of the next `Step`
    /// (on the caller's thread, like everything else), and `Reloaded` fires then. The task's result is
    /// whether it compiled; when it did not, the running rules are kept and `Diagnostics` says why.
    public Task<bool> ReloadInBackground(bool keepState = false) => Task.Run(() =>
    {
        var prepared = Prepare();
        if (prepared.Runtime is null)
        {
            Diagnostics = prepared.Diagnostics;
            return false;
        }
        _preparedKeepsState = keepState;
        Interlocked.Exchange(ref _prepared, prepared);
        return true;
    });

    private void Swap(Prepared prepared, bool keepState)
    {
        _settingsIndex = null;   // the edit, or vein.settings, may have changed what the settings are
        if (keepState && _runtime is { } old && _module is { } was && prepared.Module is { } now)
            Carry(old, was, prepared.Runtime!, now);
        else
            Boot(prepared.Runtime!);
        _module = prepared.Module;
    }

    /// The running world, moved into the edited program (Reload with keepState). Identities first, by id —
    /// a shard `var` may hold one — with the shapes the new program still declares (a field it dropped is
    /// dropped; one it added starts at its default) and the marks it knows; then the vars, each restored
    /// only if its declared initial value is the same text in both programs; then the mutes, so a paused
    /// game stays paused. The host's `$View` identity comes across with the rest, under the same id.
    private void Carry(IGameRuntime old, IrModule was, IGameRuntime next, IrModule now)
    {
        var entities = old.Snapshot();
        var vars = old.ShardVars();
        var mutedMarks = old.MutedMarks.ToList();
        var mutedShards = old.ShardsMutedByName.ToList();

        _runtime = next;
        Changed();
        next.Resume();
        foreach (var e in entities.OrderBy(e => e.Id))
        {
            long id = next.SpawnAt(e.Id);
            foreach (var (shape, fields) in e.Shapes)
                if (next.Declared(shape))
                    foreach (var (field, value) in fields) next.Publish(id, shape, field, value);
            foreach (string mark in e.Marks) next.SetMark(id, mark);
        }

        var before = Initialisers(was);
        var after = Initialisers(now);
        foreach (var (shard, values) in vars)
            foreach (var (name, value) in values)
                if (after.TryGetValue((shard, name), out string? init) && before.TryGetValue((shard, name), out string? old0) && init == old0)
                    next.SetShardVar(shard, name, value);

        next.RestoreMutes(mutedMarks, mutedShards);
        Changed();
        PublishView();
    }

    /// Each shard var's declared type and initial value, as text — what tells an edit that changed `speed`
    /// from one that did not touch it.
    private static Dictionary<(string Shard, string Var), string> Initialisers(IrModule module)
    {
        var result = new Dictionary<(string, string), string>();
        foreach (var shard in module.Shards)
        {
            if (shard.Attrs.FirstOrDefault(a => a.Name == "key") is not { Args: [string key] }) continue;
            foreach (var f in shard.State)
                result[(key, f.Name)] = f.Type.Name + " = " + (f.Default is { } d ? IrPrinter.Expr(d) : "");
        }
        return result;
    }

    private void Boot(IGameRuntime runtime)
    {
        _runtime = runtime;
        _viewId = -1;   // a new world: the old one's identity is gone with it
        Changed();
        runtime.Start();
        PublishView();
    }

    /// One frame: the clock event first (`*Vein.Time.Clock.@Ticked`, with `delta` in seconds), then every
    /// `each tick`/`frame`/`settled` block — the order `veinc run --ticks` uses — then any `every N` whose
    /// seconds have passed on this clock.
    public void Step(double delta)
    {
        if (Interlocked.Exchange(ref _prepared, null) is { Runtime: { } next } swap)
        {
            Diagnostics = swap.Diagnostics;
            RuntimeNote = swap.Note;
            Swap(swap, _preparedKeepsState);
            Reloaded?.Invoke();
        }
        if (!Running) return;
        _runtime!.Step(delta);
        Changed();
    }

    // ---- input: the stdlib's *Vein.Input events -------------------------------------------------

    public void KeyDown(string key) => Device("KeyDown", ("key", key));
    public void KeyUp(string key) => Device("KeyUp", ("key", key));
    public void TextInput(string text) => Device("TextInput", ("text", text));
    public void MouseDown(double x, double y, int button) => Device("MouseDown", ("x", x), ("y", y), ("button", (long)button));
    public void MouseUp(double x, double y, int button) => Device("MouseUp", ("x", x), ("y", y), ("button", (long)button));
    public void MouseMove(double x, double y) => Device("MouseMove", ("x", x), ("y", y));
    public void SoundEnded(long track) => Device("SoundEnded", ("track", track));

    /// The clip `name` reached its end on `entity` — the host owns the clock it ran on (*Vein.Graphics).
    public void AnimationEnded(long entity, string name) => Device("AnimationEnded", ("entity", entity), ("name", name));

    /// Two `$Collider` boxes started / stopped overlapping, as the host's physics saw it (*Vein.Game.Collision).
    public void Collided(long a, long b) => Device("Collided", ("a", Math.Min(a, b)), ("b", Math.Max(a, b)));
    public void Separated(long a, long b) => Device("Separated", ("a", Math.Min(a, b)), ("b", Math.Max(a, b)));

    /// The player clicked this identity — the host found what was under the pointer (*Vein.UI.Interaction).
    public void Clicked(long target) => Device("Clicked", ("target", target));

    /// A named action began — `strength` 1 for a key or button, partial for a stick or trigger
    /// (*Vein.Input.Actions). Which inputs make an action is the host's input map.
    public void ActionPressed(string action, double strength = 1) => Device("ActionPressed", ("action", action), ("strength", strength));
    public void ActionReleased(string action) => Device("ActionReleased", ("action", action));

    /// A gamepad button by its common-layout name ("A", "Start", "DpadUp"; *Vein.Input.Gamepad).
    public void ButtonDown(int device, string button) => Device("ButtonDown", ("device", (long)device), ("button", button));
    public void ButtonUp(int device, string button) => Device("ButtonUp", ("device", (long)device), ("button", button));

    /// A stick ("LeftX", -1..1) or trigger ("TriggerLeft", 0..1) moved. Drop the jitter around rest
    /// before calling, and report the return to rest as 0.
    public void AxisMoved(int device, string axis, double value) => Device("AxisMoved", ("device", (long)device), ("axis", axis), ("value", value));

    // ---- the window: *Vein.UI.Surface.$View ------------------------------------------------------

    private (double Width, double Height)? _view;
    private long _viewId = -1;

    /// The size of what the player sees, in the units the rules place things in. Written to
    /// `*Vein.UI.Surface.$View { width, height }` on an identity the host owns — immediately, and again
    /// after every reload — so rules that centre or clamp to the screen read it rather than guess. Call it
    /// at start and whenever the window is resized. Programs that declare no `$View` are left alone.
    public void SetView(double width, double height)
    {
        _view = (width, height);
        PublishView();
    }

    private void PublishView()
    {
        if (_view is not { } v || _runtime is not { } r || !r.Declared("View")) return;
        if (_viewId < 0) _viewId = r.Spawn();
        r.Publish(_viewId, "View", "width", v.Width);
        r.Publish(_viewId, "View", "height", v.Height);
        Changed();
    }

    /// Raise one of the PROGRAM'S events (`Emit("Paused")`, `Emit("Buy", new() { ["item"] = "sword" })`).
    /// False when the program declares no such event — a misspelt name is reported, not delivered to
    /// nobody.
    public bool Emit(string eventName, IReadOnlyDictionary<string, object?>? fields = null)
    {
        if (!Running) return false;
        bool ok = _runtime!.Raise(eventName, fields);
        Changed();
        return ok;
    }

    private void Device(string name, params (string Field, object? Value)[] fields)
    {
        if (!Running) return;
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (field, value) in fields) payload[field] = value;
        _runtime!.Device(name, payload);
        Changed();
    }

    // ---- what to draw ---------------------------------------------------------------------------

    /// Every live identity, as committed at the end of the last step or input — the world a frame
    /// should draw. Snapshotted once per change, so reading it many times in a frame is free.
    public IReadOnlyList<VeinEntity> Entities => _entities ??= _runtime?.Snapshot() ?? Array.Empty<VeinEntity>();

    /// Just what a 2D host draws, for every identity with a place and an `$Image` and/or `$Text` — the
    /// per-frame path. `Entities` copies every component of every identity; this reads the handful of
    /// fields a sprite needs straight from the committed world, which the addon stress test measured as
    /// the larger half of a frame at 1000 moving identities.
    ///
    /// Same answers as the VeinEntity helpers: placed at `$World` when the program has it, else
    /// `$Position`; rotation `$Rotation.z` (0 without one); scale `$Scale` ((1, 1) without one).
    public IReadOnlyList<VeinDrawable> Drawables => _drawables ??= BuildDrawables();

    /// Where the view is centred and how far it is zoomed: the identity carrying `*Vein.UI.Surface.$Camera`
    /// (the lowest id, if several do) at its place — `$World` when it has one, else `$Position`. Null when
    /// nothing carries `$Camera` or the camera has no place: the host shows the world's top-left corner.
    /// Read fresh each call; it is one identity, not a list.
    public VeinCamera? Camera
    {
        get
        {
            if (_runtime is not { } r || !r.Declared("Camera")) return null;
            foreach (long id in r.Ids("Camera"))
            {
                if (!r.Has(id, "Position")) continue;
                string place = r.Declared("World") && r.Has(id, "World") ? "World" : "Position";
                double zoom = Num(r, id, "Camera", "zoom", 1);
                bool turned = r.Declared("Rotation") && r.Has(id, "Rotation");
                return new VeinCamera(id, Num(r, id, place, "x", 0), Num(r, id, place, "y", 0), zoom > 0 ? zoom : 1,
                    Num(r, id, place, "z", 0),
                    turned ? Num(r, id, "Rotation", "x", 0) : 0,
                    turned ? Num(r, id, "Rotation", "y", 0) : 0,
                    turned ? Num(r, id, "Rotation", "z", 0) : 0);
            }
            return null;
        }
    }

    /// What a 3D host shows, for every identity with a place and a `*Vein.Graphics.Models.$Model` — the
    /// 3D counterpart of `Drawables`, read the same lean way. Placed at `$World` (else `$Position`), all
    /// three axes; turned by `$Rotation` (radians, all three; 0 without one); sized by `$Scale` ((1, 1, 1)
    /// without one); and the clip its `*Vein.Graphics.Animations.$Animation` asks for, if it carries one.
    public IReadOnlyList<VeinModel> Models => _models ??= BuildModels();

    private IReadOnlyList<VeinModel> BuildModels()
    {
        if (_runtime is not { } r || !r.Declared("Model")) return Array.Empty<VeinModel>();
        bool hasWorld = r.Declared("World"), hasRotation = r.Declared("Rotation"), hasScale = r.Declared("Scale");
        bool hasAnimation = r.Declared("Animation");

        var list = new List<VeinModel>();
        foreach (long id in r.Ids("Model"))
        {
            if (!r.Has(id, "Position")) continue;
            string source = r.Read(id, "Model", "source")?.ToString() ?? "";
            string place = hasWorld && r.Has(id, "World") ? "World" : "Position";
            bool turned = hasRotation && r.Has(id, "Rotation");
            bool scaled = hasScale && r.Has(id, "Scale");
            bool animated = hasAnimation && r.Has(id, "Animation");
            list.Add(new VeinModel(
                id, source,
                Num(r, id, place, "x", 0), Num(r, id, place, "y", 0), Num(r, id, place, "z", 0),
                turned ? Num(r, id, "Rotation", "x", 0) : 0,
                turned ? Num(r, id, "Rotation", "y", 0) : 0,
                turned ? Num(r, id, "Rotation", "z", 0) : 0,
                scaled ? Num(r, id, "Scale", "x", 1) : 1,
                scaled ? Num(r, id, "Scale", "y", 1) : 1,
                scaled ? Num(r, id, "Scale", "z", 1) : 1,
                animated ? r.Read(id, "Animation", "name")?.ToString() : null,
                animated ? Num(r, id, "Animation", "speed", 1) : 1,
                r.Carries(id, "Hidden")));
        }
        return list;
    }

    /// Every identity with a place and a `*Vein.Game.Collision.$Collider` — what a host with physics turns
    /// into collision boxes, and reports overlaps of with `Collided`/`Separated`. Placed and turned like
    /// `Models` ($World else $Position; $Rotation in radians); the extents are HALF-extents, and a `depth`
    /// of 0 means "as deep as it is wide".
    public IReadOnlyList<VeinCollider> Colliders => _colliders ??= BuildColliders();

    private IReadOnlyList<VeinCollider> BuildColliders()
    {
        if (_runtime is not { } r || !r.Declared("Collider")) return Array.Empty<VeinCollider>();
        bool hasWorld = r.Declared("World"), hasRotation = r.Declared("Rotation");

        var list = new List<VeinCollider>();
        foreach (long id in r.Ids("Collider"))
        {
            if (!r.Has(id, "Position")) continue;
            string place = hasWorld && r.Has(id, "World") ? "World" : "Position";
            bool turned = hasRotation && r.Has(id, "Rotation");
            double width = Num(r, id, "Collider", "width", 0), depth = Num(r, id, "Collider", "depth", 0);
            list.Add(new VeinCollider(
                id,
                Num(r, id, place, "x", 0), Num(r, id, place, "y", 0), Num(r, id, place, "z", 0),
                width, Num(r, id, "Collider", "height", 0), depth > 0 ? depth : width,
                turned ? Num(r, id, "Rotation", "x", 0) : 0,
                turned ? Num(r, id, "Rotation", "y", 0) : 0,
                turned ? Num(r, id, "Rotation", "z", 0) : 0));
        }
        return list;
    }

    /// Everything read from the world is stale once rules have run.
    private void Changed()
    {
        _entities = null; _drawables = null; _models = null; _colliders = null;
    }

    private IReadOnlyList<VeinDrawable> BuildDrawables()
    {
        if (_runtime is not { } r) return Array.Empty<VeinDrawable>();
        bool hasWorld = r.Declared("World");
        bool hasImage = r.Declared("Image"), hasText = r.Declared("Text");
        if (!hasImage && !hasText) return Array.Empty<VeinDrawable>();
        bool hasRotation = r.Declared("Rotation"), hasScale = r.Declared("Scale");
        bool hasTint = r.Declared("Tint"), hasFlip = r.Declared("Flip"), hasAnimation = r.Declared("Animation");

        var list = new List<VeinDrawable>();
        foreach (long id in r.Ids("Position"))
        {
            string? image = hasImage && r.Has(id, "Image") ? r.Read(id, "Image", "source")?.ToString() : null;
            string? caption = hasText && r.Has(id, "Text") ? r.Read(id, "Text", "content")?.ToString() : null;
            if (image is null && caption is null) continue;

            string place = hasWorld && r.Has(id, "World") ? "World" : "Position";
            bool scaled = hasScale && r.Has(id, "Scale");
            bool tinted = hasTint && r.Has(id, "Tint");
            bool flipped = hasFlip && r.Has(id, "Flip");
            bool animated = hasAnimation && r.Has(id, "Animation");
            list.Add(new VeinDrawable(
                id,
                Num(r, id, place, "x", 0), Num(r, id, place, "y", 0), Num(r, id, place, "z", 0),
                hasRotation && r.Has(id, "Rotation") ? Num(r, id, "Rotation", "z", 0) : 0,
                scaled ? Num(r, id, "Scale", "x", 1) : 1,
                scaled ? Num(r, id, "Scale", "y", 1) : 1,
                image, caption,
                tinted ? Num(r, id, "Tint", "r", 1) : 1,
                tinted ? Num(r, id, "Tint", "g", 1) : 1,
                tinted ? Num(r, id, "Tint", "b", 1) : 1,
                tinted ? Num(r, id, "Tint", "a", 1) : 1,
                flipped && r.Read(id, "Flip", "x") is true,
                flipped && r.Read(id, "Flip", "y") is true,
                r.Carries(id, "Hidden"),
                animated ? r.Read(id, "Animation", "name")?.ToString() : null,
                animated ? Num(r, id, "Animation", "speed", 1) : 1));
        }
        return list;
    }

    private static double Num(IGameRuntime r, long id, string shape, string field, double fallback) =>
        r.Read(id, shape, field) switch { long l => l, int i => i, double d => d, float f => f, _ => fallback };

    // ---- compiling --------------------------------------------------------------------------------

    /// A compiled program and the runtime chosen for it — or, when it did not compile, only diagnostics.
    private sealed record Prepared(IGameRuntime? Runtime, IReadOnlyList<Diagnostic> Diagnostics, string Note, IrModule? Module = null);

    /// Front end, then the runtime: compiled when asked (Auto or Compiled) and the whole program compiles
    /// with no backend note; otherwise the interpreter, and the note says why.
    private Prepared Prepare()
    {
        var (module, diagnostics) = Compile(Path);
        if (module is null) return new Prepared(null, diagnostics, "");

        string baseDirectory = System.IO.Path.GetDirectoryName(Path)!;
        if (_mode == VeinRuntime.Interpreter)
            return new Prepared(new InterpRuntime(module, baseDirectory, _sinks), diagnostics, "", module);

        // Compiled ahead of time (an exported game): load the bytes, no compiler. Bytes that do not load —
        // built by an older backend — fall through to compiling here, as if there were none.
        if (_precompiled is { } image && VeinBuild.LoadForHost(image) is { Program: { } ready })
        {
            Precompiled = true;
            return new Prepared(new CompiledRuntime(ready, _sinks), diagnostics, "", module);
        }
        Precompiled = false;

        HostBuild build;
        try { build = VeinBuild.CompileForHost(module); }
        catch (Exception ex) { build = new HostBuild(null, Array.Empty<string>(), new[] { $"{ex.GetType().Name}: {ex.Message}" }, ""); }

        return build.Program is { } program
            ? new Prepared(new CompiledRuntime(program, _sinks), diagnostics, "", module)
            : new Prepared(new InterpRuntime(module, baseDirectory, _sinks), diagnostics, "not compiled: " + build.Reason, module);
    }

    /// The same front end `veinc run` uses: an `app` manifest is linked into one module, anything else
    /// is a bundle (with its `publicators/` and `shards/` fragments) whose entry module runs. The
    /// stdlib comes from StdlibCache, so it resolves wherever the host process happens to be.
    private static (IrModule? Module, IReadOnlyList<Diagnostic> Diagnostics) Compile(string path, string? text = null)
    {
        string source;
        try { source = text ?? File.ReadAllText(path); }
        catch (Exception ex)
        {
            return (null, new[] { new Diagnostic(Severity.Error, "VS0001", $"cannot read {path}: {ex.Message}",
                                                 new Compiler.Lexing.SourceSpan(path, 1, 1, 0)) });
        }

        using var _ = BundleSearch.Scope(StdlibCache.Root);
        using var __ = BundleSearch.WithLibraries(VeinLibraries.Roots);   // VeinKit & co., beside the game
        var diag = new DiagnosticBag();
        if (AppLinker.Link(path, source, diag) is { } app)
            return (diag.HasErrors ? null : app.Module, diag.Items);

        // A `need` LINKS (RULES 18): a game that needs a library runs that library's shards too. From disk
        // only — `Check` passes the editor's unsaved text, and diagnostics need no linking.
        if (text is null && AppLinker.LinkNeeds(path, source, diag) is { } linked)
            return (diag.HasErrors ? null : linked.Module, diag.Items);

        var result = new VeinCompilerService().Compile(new CompileRequest(
            System.IO.Path.GetFileName(path), source,
            ProjectDir: System.IO.Path.GetDirectoryName(path), SourcePath: path));
        return (result.Success ? result.Entry : null, result.Diagnostics);
    }
}
