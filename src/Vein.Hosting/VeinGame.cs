using System.Text;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Project;
using Vein.Compiler.Service;

namespace Vein.Hosting;

/// A VeinScript program run by an engine: the engine owns the window, the frame clock, input, drawing and
/// sound; the program owns the rules. See docs/HOSTING.md for the per-frame contract.
///
/// Built from seams the interpreter already had — `Boot`/`Frame` for stepping, `FireTicked` for the clock,
/// the `Fire*` device inputs, `World.Snapshot()` for what to draw, `AudioOut` for sound — plus
/// `Interp.FireHost`/`Observed` for the program's own events. Single-threaded, like the interpreter:
/// call it from the engine's main thread.
public sealed class VeinGame
{
    private Interp? _interp;
    private IReadOnlyList<VeinEntity>? _entities;
    private IReadOnlyList<VeinDrawable>? _drawables;

    private VeinGame(string path) => Path = System.IO.Path.GetFullPath(path);

    /// The `.vein` file or `app` manifest this game runs.
    public string Path { get; }

    /// What the last load or reload reported, warnings included. A failed `Reload` leaves the previous
    /// program running and puts its errors here.
    public IReadOnlyList<Diagnostic> Diagnostics { get; private set; } = Array.Empty<Diagnostic>();

    /// True while a program is loaded and running.
    public bool Running => _interp is not null && !_interp.Stopped;

    /// Frames stepped since the program booted.
    public int Tick => _interp?.Tick ?? 0;

    /// A line the program printed (`@Print`), and whether it was an error (`@PrintError`, or a runtime
    /// fault nobody heard).
    public event Action<string, bool>? Printed;

    /// `*Vein.Audio.Sound.@PlaySound` — the file the program named, and a volume in 0..1.
    public event Action<string, double>? Sound;

    /// Every event the program handles, by its readable name — for events a host acts on beyond sound and
    /// printing (`@ShowMenu`, `@ShakeCamera`). The payload belongs to the program; read, don't keep.
    public event Action<string, IReadOnlyDictionary<string, object?>>? Emitted;

    private IrModule? _pending;

    /// Compile and boot. Never throws for a bad program: check `Running` and `Diagnostics`. A host that
    /// wants what the program prints or emits WHILE BOOTING (a score's first value, a welcome line) uses
    /// `Open`, subscribes, then `Start` — here the boot has already happened by the time `Load` returns.
    public static VeinGame Load(string path)
    {
        var game = Open(path);
        game.Start();
        return game;
    }

    /// Compile without booting, so the host can subscribe to `Printed`, `Sound` and `Emitted` first.
    /// `Diagnostics` is filled; nothing runs until `Start`.
    public static VeinGame Open(string path)
    {
        var game = new VeinGame(path);
        (game._pending, game.Diagnostics) = Compile(game.Path);
        return game;
    }

    /// Boot what `Open` compiled. False when it did not compile (see `Diagnostics`) or already started.
    public bool Start()
    {
        if (_pending is not { } module) return false;
        _pending = null;
        Boot(module);
        return true;
    }

    /// Recompile from disk and boot afresh — what a host calls when a `.vein` file changes. The new
    /// program starts from its own boot (state is not carried across an edit). If it does not compile,
    /// the running program is KEPT and the errors are in `Diagnostics`: a typo must not stop the game.
    public bool Reload()
    {
        var (module, diagnostics) = Compile(Path);
        Diagnostics = diagnostics;
        if (module is null) return false;
        _pending = null;
        Boot(module);
        return true;
    }

    private void Boot(IrModule module)
    {
        var interp = new Interp
        {
            BaseDirectory = System.IO.Path.GetDirectoryName(Path),
            Output = new LineWriter(line => Printed?.Invoke(line, false)),
            ErrorOutput = new LineWriter(line => Printed?.Invoke(line, true)),
        };
        interp.Observed = (name, payload) => Emitted?.Invoke(name, payload);
        _interp = interp;
        _entities = null; _drawables = null;
        using (Enter()) interp.Boot(module);
    }

    /// One frame: the clock event first (`*Vein.Time.Clock.@Ticked`, with `delta` in seconds), then every
    /// `each tick`/`frame`/`settled` block — the order `veinc run --ticks` uses.
    public void Step(double delta)
    {
        if (!Running) return;
        using (Enter())
        {
            _interp!.FireTicked(_interp.Tick + 1, delta);
            _interp.Frame();
            _interp.AdvanceEvery(delta);   // `every N` runs on the HOST's clock — there is no timer thread here
        }
        _entities = null; _drawables = null;
    }

    // ---- input: the stdlib's *Vein.Input events -------------------------------------------------

    public void KeyDown(string key) => Fire(i => i.FireKeyDown(key));
    public void KeyUp(string key) => Fire(i => i.FireKeyUp(key));
    public void TextInput(string text) => Fire(i => i.FireTextInput(text));
    public void MouseDown(double x, double y, int button) => Fire(i => i.FireMouseDown(x, y, button));
    public void MouseUp(double x, double y, int button) => Fire(i => i.FireMouseUp(x, y, button));
    public void MouseMove(double x, double y) => Fire(i => i.FireMouseMove(x, y));
    public void SoundEnded(long track) => Fire(i => i.FireSoundEnded(track));

    /// Raise one of the PROGRAM'S events (`Emit("Paused")`, `Emit("Buy", new() { ["item"] = "sword" })`).
    /// False when the program declares no such event — a misspelt name is reported, not delivered to
    /// nobody.
    public bool Emit(string eventName, IReadOnlyDictionary<string, object?>? fields = null)
    {
        if (!Running) return false;
        bool ok;
        using (Enter()) ok = _interp!.FireHost(eventName, fields);
        _entities = null; _drawables = null;
        return ok;
    }

    // ---- what to draw ---------------------------------------------------------------------------

    /// Every live identity, as committed at the end of the last step or input — the world a frame
    /// should draw. Snapshotted once per change, so reading it many times in a frame is free.
    public IReadOnlyList<VeinEntity> Entities =>
        _entities ??= _interp is null
            ? Array.Empty<VeinEntity>()
            : _interp.World.Snapshot().Select(s => new VeinEntity(s.Entity, s.Marks, s.Components)).ToList();

    /// Just what a 2D host draws, for every identity with a place and an `$Image` and/or `$Text` — the
    /// per-frame path. `Entities` copies every component of every identity; this reads the handful of
    /// fields a sprite needs straight from the committed world, which the addon stress test measured as
    /// the larger half of a frame at 1000 moving identities.
    ///
    /// Same answers as the VeinEntity helpers: placed at `$World` when the program has it, else
    /// `$Position`; rotation `$Rotation.z` (0 without one); scale `$Scale` ((1, 1) without one).
    public IReadOnlyList<VeinDrawable> Drawables => _drawables ??= BuildDrawables();

    private IReadOnlyList<VeinDrawable> BuildDrawables()
    {
        if (_interp is null) return Array.Empty<VeinDrawable>();
        var store = _interp.World;
        bool hasWorld = store.Declared("World");
        bool hasImage = store.Declared("Image"), hasText = store.Declared("Text");
        if (!hasImage && !hasText) return Array.Empty<VeinDrawable>();
        bool hasRotation = store.Declared("Rotation"), hasScale = store.Declared("Scale");

        double Num(long id, string shape, string field, double fallback) =>
            store.Read(id, shape, field) switch { long l => l, int i => i, double d => d, float f => f, _ => fallback };

        var list = new List<VeinDrawable>();
        foreach (long id in store.Query(new[] { "Position" }, Array.Empty<string>()))
        {
            string? image = hasImage && store.Has(id, "Image") ? store.Read(id, "Image", "source")?.ToString() : null;
            string? caption = hasText && store.Has(id, "Text") ? store.Read(id, "Text", "content")?.ToString() : null;
            if (image is null && caption is null) continue;

            string place = hasWorld && store.Has(id, "World") ? "World" : "Position";
            list.Add(new VeinDrawable(
                id,
                Num(id, place, "x", 0), Num(id, place, "y", 0),
                hasRotation && store.Has(id, "Rotation") ? Num(id, "Rotation", "z", 0) : 0,
                hasScale && store.Has(id, "Scale") ? Num(id, "Scale", "x", 1) : 1,
                hasScale && store.Has(id, "Scale") ? Num(id, "Scale", "y", 1) : 1,
                image, caption));
        }
        return list;
    }

    // ---- plumbing -------------------------------------------------------------------------------

    private void Fire(Action<Interp> input)
    {
        if (!Running) return;
        using (Enter()) input(_interp!);
        _entities = null; _drawables = null;
    }

    /// `AudioOut.Hook` is process-wide, so it points at THIS game only while this game runs code — two
    /// games in one host (an editor preview and a play window) each hear their own sounds.
    private IDisposable Enter()
    {
        var previous = AudioOut.Hook;
        AudioOut.Hook = (source, volume) => Sound?.Invoke(source, volume);
        return new Restore(() => AudioOut.Hook = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
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

    /// A TextWriter that hands each finished line to the host.
    private sealed class LineWriter(Action<string> line) : TextWriter
    {
        private readonly StringBuilder _buffer = new();
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n') { line(_buffer.ToString().TrimEnd('\r')); _buffer.Clear(); }
            else _buffer.Append(value);
        }

        public override void Flush()
        {
            if (_buffer.Length == 0) return;
            line(_buffer.ToString());
            _buffer.Clear();
        }
    }
}
