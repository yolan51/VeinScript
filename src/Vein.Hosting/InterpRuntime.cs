using System.Text;
using Vein.Compiler.Ir;

namespace Vein.Hosting;

/// The rules on the interpreter — the whole language. What VeinGame was before it had a second runtime,
/// moved here unchanged: Boot/Frame, FireTicked, the named Fire* inputs, FireHost, AdvanceEvery, and the
/// EntityStore for reading.
internal sealed class InterpRuntime : IGameRuntime
{
    private readonly Interp _interp;
    private readonly IrModule _module;
    private readonly RuntimeSinks _sinks;

    public InterpRuntime(IrModule module, string baseDirectory, RuntimeSinks sinks)
    {
        _module = module;
        _sinks = sinks;
        _interp = new Interp
        {
            BaseDirectory = baseDirectory,
            Output = new LineWriter(line => sinks.Printed(line, false)),
            ErrorOutput = new LineWriter(line => sinks.Printed(line, true)),
        };
        _interp.Observed = (name, payload) => { if (sinks.WantsEvents()) sinks.Emitted(name, payload); };
    }

    public string Name => "interpreter";
    public string? FilesRoot { set => _interp.BaseDirectory = value; }
    public bool Running => !_interp.Stopped;
    public int Tick => _interp.Tick;
    public IReadOnlyList<string> MutedMarks => _interp.MutedMarks.OrderBy(m => m, StringComparer.Ordinal).ToList();
    public IReadOnlyList<string> MutedShards => _interp.MutedShards;

    public void Start()
    {
        using (Enter()) _interp.Boot(_module);
    }

    public void Step(double delta)
    {
        using (Enter())
        {
            _interp.FireTicked(_interp.Tick + 1, delta);
            _interp.Frame();
            _interp.AdvanceEvery(delta);   // `every N` on the HOST's clock — there is no timer thread here
        }
    }

    public void Device(string name, IReadOnlyDictionary<string, object?> f)
    {
        // The interpreter's NAMED entry points, on purpose (Interp: "specific entry points, not a generic
        // Emit") — a host cannot fabricate a transport event through this.
        using (Enter())
            switch (name)
            {
                case "KeyDown": _interp.FireKeyDown(Text(f, "key")); break;
                case "KeyUp": _interp.FireKeyUp(Text(f, "key")); break;
                case "TextInput": _interp.FireTextInput(Text(f, "text")); break;
                case "MouseDown": _interp.FireMouseDown(Num(f, "x"), Num(f, "y"), (int)Num(f, "button")); break;
                case "MouseUp": _interp.FireMouseUp(Num(f, "x"), Num(f, "y"), (int)Num(f, "button")); break;
                case "MouseMove": _interp.FireMouseMove(Num(f, "x"), Num(f, "y")); break;
                case "SoundEnded": _interp.FireSoundEnded((long)Num(f, "track")); break;
                case "AnimationEnded": _interp.FireAnimationEnded((long)Num(f, "entity"), Text(f, "name")); break;
                case "Collided": _interp.FireCollided((long)Num(f, "a"), (long)Num(f, "b")); break;
                case "Separated": _interp.FireSeparated((long)Num(f, "a"), (long)Num(f, "b")); break;
                case "Clicked": _interp.FireClicked((long)Num(f, "target")); break;
                case "ActionPressed": _interp.FireActionPressed(Text(f, "action"), Num(f, "strength")); break;
                case "ActionReleased": _interp.FireActionReleased(Text(f, "action")); break;
                case "ButtonDown": _interp.FireButtonDown((int)Num(f, "device"), Text(f, "button")); break;
                case "ButtonUp": _interp.FireButtonUp((int)Num(f, "device"), Text(f, "button")); break;
                case "AxisMoved": _interp.FireAxisMoved((int)Num(f, "device"), Text(f, "axis"), Num(f, "value")); break;
                default: throw new ArgumentException($"not a device event: {name}", nameof(name));
            }
    }

    public bool Raise(string name, IReadOnlyDictionary<string, object?>? fields)
    {
        using (Enter()) return _interp.FireHost(name, fields);
    }

    public bool Declared(string shape) => _interp.World.Declared(shape);
    public IReadOnlyList<long> Ids(string shape) => _interp.World.Query(new[] { shape }, Array.Empty<string>());
    public bool Has(long id, string shape) => _interp.World.Has(id, shape);
    public bool Carries(long id, string mark) => _interp.World.HasTag(id, mark);
    public object? Read(long id, string shape, string field) => _interp.World.Read(id, shape, field);

    public IReadOnlyList<VeinEntity> Snapshot() =>
        _interp.World.Snapshot().Select(s => new VeinEntity(s.Entity, s.Marks, s.Components)).ToList();

    public bool Publish(long id, string shape, string field, object? value)
    {
        var store = _interp.World;
        if (!store.Declared(shape)) return false;
        if (!store.Has(id, shape)) store.AddComponent(id, shape);
        store.Publish(id, shape, field, value);
        return true;
    }

    public long Spawn() => _interp.World.Spawn();

    public void Resume()
    {
        using (Enter()) _interp.Resume(_module);
    }

    public long SpawnAt(long id) => _interp.World.SpawnAt(id);

    public bool SetMark(long id, string mark)
    {
        _interp.World.AddTag(id, mark);
        return true;
    }

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> ShardVars() => _interp.ShardStates();
    public bool SetShardVar(string shard, string name, object? value) => _interp.SetShardState(shard, name, value);
    public IReadOnlyList<string> ShardsMutedByName => _interp.ShardsMutedByName;
    public void RestoreMutes(IEnumerable<string> marks, IEnumerable<string> shardKeys) => _interp.RestoreMutes(marks, shardKeys);

    private static string Text(IReadOnlyDictionary<string, object?> f, string k) => f.GetValueOrDefault(k)?.ToString() ?? "";
    private static double Num(IReadOnlyDictionary<string, object?> f, string k) =>
        f.GetValueOrDefault(k) switch { long l => l, int i => i, double d => d, float x => x, _ => 0 };

    /// `AudioOut.Hook` is process-wide, so it points at THIS game only while this game runs code — two
    /// games in one host (an editor preview and a play window) each hear their own sounds.
    private IDisposable Enter()
    {
        var previous = AudioOut.Hook;
        AudioOut.Hook = (source, volume) => _sinks.Sound(source, volume);
        return new Restore(() => AudioOut.Hook = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}

/// A TextWriter that hands each finished line to the host.
internal sealed class LineWriter(Action<string> line) : TextWriter
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
