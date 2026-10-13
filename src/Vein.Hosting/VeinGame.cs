using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Project;
using Vein.Compiler.Service;
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
    private IReadOnlyList<VeinEntity>? _entities;
    private IReadOnlyList<VeinDrawable>? _drawables;
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
    public static VeinGame Open(string path, VeinRuntime runtime = VeinRuntime.Auto)
    {
        var game = new VeinGame(path, runtime);
        var prepared = game.Prepare();
        game.Diagnostics = prepared.Diagnostics;
        game.RuntimeNote = prepared.Note;
        game._pending = prepared.Runtime;
        return game;
    }

    /// Boot what `Open` compiled. False when it did not compile (see `Diagnostics`) or already started.
    public bool Start()
    {
        if (_pending is not { } runtime) return false;
        _pending = null;
        Boot(runtime);
        return true;
    }

    /// Recompile from disk and boot afresh — what a host calls when a `.vein` file changes. The new
    /// program starts from its own boot (state is not carried across an edit). If it does not compile,
    /// the running program is KEPT and the errors are in `Diagnostics`: a typo must not stop the game.
    public bool Reload()
    {
        var prepared = Prepare();
        Diagnostics = prepared.Diagnostics;
        if (prepared.Runtime is null) return false;
        RuntimeNote = prepared.Note;
        _pending = null;
        Boot(prepared.Runtime);
        return true;
    }

    /// `Reload`, with the compiling done on a worker thread — compiled rules can take a second to build,
    /// and a game should not freeze for it. The new rules are swapped in at the start of the next `Step`
    /// (on the caller's thread, like everything else), and `Reloaded` fires then. The task's result is
    /// whether it compiled; when it did not, the running rules are kept and `Diagnostics` says why.
    public Task<bool> ReloadInBackground() => Task.Run(() =>
    {
        var prepared = Prepare();
        if (prepared.Runtime is null)
        {
            Diagnostics = prepared.Diagnostics;
            return false;
        }
        Interlocked.Exchange(ref _prepared, prepared);
        return true;
    });

    private void Boot(IGameRuntime runtime)
    {
        _runtime = runtime;
        _entities = null; _drawables = null;
        runtime.Start();
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
            Boot(next);
            Reloaded?.Invoke();
        }
        if (!Running) return;
        _runtime!.Step(delta);
        _entities = null; _drawables = null;
    }

    // ---- input: the stdlib's *Vein.Input events -------------------------------------------------

    public void KeyDown(string key) => Device("KeyDown", ("key", key));
    public void KeyUp(string key) => Device("KeyUp", ("key", key));
    public void TextInput(string text) => Device("TextInput", ("text", text));
    public void MouseDown(double x, double y, int button) => Device("MouseDown", ("x", x), ("y", y), ("button", (long)button));
    public void MouseUp(double x, double y, int button) => Device("MouseUp", ("x", x), ("y", y), ("button", (long)button));
    public void MouseMove(double x, double y) => Device("MouseMove", ("x", x), ("y", y));
    public void SoundEnded(long track) => Device("SoundEnded", ("track", track));

    /// Raise one of the PROGRAM'S events (`Emit("Paused")`, `Emit("Buy", new() { ["item"] = "sword" })`).
    /// False when the program declares no such event — a misspelt name is reported, not delivered to
    /// nobody.
    public bool Emit(string eventName, IReadOnlyDictionary<string, object?>? fields = null)
    {
        if (!Running) return false;
        bool ok = _runtime!.Raise(eventName, fields);
        _entities = null; _drawables = null;
        return ok;
    }

    private void Device(string name, params (string Field, object? Value)[] fields)
    {
        if (!Running) return;
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (field, value) in fields) payload[field] = value;
        _runtime!.Device(name, payload);
        _entities = null; _drawables = null;
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
                return new VeinCamera(id, Num(r, id, place, "x", 0), Num(r, id, place, "y", 0), zoom > 0 ? zoom : 1);
            }
            return null;
        }
    }

    private IReadOnlyList<VeinDrawable> BuildDrawables()
    {
        if (_runtime is not { } r) return Array.Empty<VeinDrawable>();
        bool hasWorld = r.Declared("World");
        bool hasImage = r.Declared("Image"), hasText = r.Declared("Text");
        if (!hasImage && !hasText) return Array.Empty<VeinDrawable>();
        bool hasRotation = r.Declared("Rotation"), hasScale = r.Declared("Scale");

        var list = new List<VeinDrawable>();
        foreach (long id in r.Ids("Position"))
        {
            string? image = hasImage && r.Has(id, "Image") ? r.Read(id, "Image", "source")?.ToString() : null;
            string? caption = hasText && r.Has(id, "Text") ? r.Read(id, "Text", "content")?.ToString() : null;
            if (image is null && caption is null) continue;

            string place = hasWorld && r.Has(id, "World") ? "World" : "Position";
            bool scaled = hasScale && r.Has(id, "Scale");
            list.Add(new VeinDrawable(
                id,
                Num(r, id, place, "x", 0), Num(r, id, place, "y", 0), Num(r, id, place, "z", 0),
                hasRotation && r.Has(id, "Rotation") ? Num(r, id, "Rotation", "z", 0) : 0,
                scaled ? Num(r, id, "Scale", "x", 1) : 1,
                scaled ? Num(r, id, "Scale", "y", 1) : 1,
                image, caption));
        }
        return list;
    }

    private static double Num(IGameRuntime r, long id, string shape, string field, double fallback) =>
        r.Read(id, shape, field) switch { long l => l, int i => i, double d => d, float f => f, _ => fallback };

    // ---- compiling --------------------------------------------------------------------------------

    /// A compiled program and the runtime chosen for it — or, when it did not compile, only diagnostics.
    private sealed record Prepared(IGameRuntime? Runtime, IReadOnlyList<Diagnostic> Diagnostics, string Note);

    /// Front end, then the runtime: compiled when asked (Auto or Compiled) and the whole program compiles
    /// with no backend note; otherwise the interpreter, and the note says why.
    private Prepared Prepare()
    {
        var (module, diagnostics) = Compile(Path);
        if (module is null) return new Prepared(null, diagnostics, "");

        string baseDirectory = System.IO.Path.GetDirectoryName(Path)!;
        if (_mode == VeinRuntime.Interpreter)
            return new Prepared(new InterpRuntime(module, baseDirectory, _sinks), diagnostics, "");

        HostBuild build;
        try { build = VeinBuild.CompileForHost(module); }
        catch (Exception ex) { build = new HostBuild(null, Array.Empty<string>(), new[] { $"{ex.GetType().Name}: {ex.Message}" }, ""); }

        return build.Program is { } program
            ? new Prepared(new CompiledRuntime(program, _sinks), diagnostics, "")
            : new Prepared(new InterpRuntime(module, baseDirectory, _sinks), diagnostics, "not compiled: " + build.Reason);
    }

    /// The same front end `veinc run` uses: an `app` manifest is linked into one module, anything else
    /// is a bundle (with its `publicators/` and `shards/` fragments) whose entry module runs. The
    /// stdlib comes from StdlibCache, so it resolves wherever the host process happens to be.
    private static (IrModule? Module, IReadOnlyList<Diagnostic> Diagnostics) Compile(string path)
    {
        string source;
        try { source = File.ReadAllText(path); }
        catch (Exception ex)
        {
            return (null, new[] { new Diagnostic(Severity.Error, "VS0001", $"cannot read {path}: {ex.Message}",
                                                 new Compiler.Lexing.SourceSpan(path, 1, 1, 0)) });
        }

        using var _ = BundleSearch.Scope(StdlibCache.Root);
        var diag = new DiagnosticBag();
        if (AppLinker.Link(path, source, diag) is { } app)
            return (diag.HasErrors ? null : app.Module, diag.Items);

        var result = new VeinCompilerService().Compile(new CompileRequest(
            System.IO.Path.GetFileName(path), source,
            ProjectDir: System.IO.Path.GetDirectoryName(path), SourcePath: path));
        return (result.Success ? result.Entry : null, result.Diagnostics);
    }
}
