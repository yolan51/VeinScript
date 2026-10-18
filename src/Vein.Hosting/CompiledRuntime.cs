using System.Collections.Concurrent;
using System.Reflection;
using Vein.Compiler.Ir;
using Vein.Runtime.SECS;

namespace Vein.Hosting;

/// The rules compiled to C# (VeinBuild.CompileForHost) — ~14–25× the interpreter. Driven through the
/// program's generated `__Host` (IVeinCompiledProgram): no reflection on the per-frame path except to
/// turn an event payload into fields, and only when someone is listening.
///
/// Where the two runtimes differ, it is made to answer like the interpreter: entity ids as long, a
/// throwing frame reported as an error rather than ending the game (Interp catches faults per unit),
/// `@Quit` seen through Emitted.
internal sealed class CompiledRuntime : IGameRuntime
{
    private readonly IVeinCompiledProgram _program;
    private readonly VeinWorld _world;
    private readonly RuntimeSinks _sinks;
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> PayloadFields = new();
    private static readonly IReadOnlyDictionary<string, object?> None = new Dictionary<string, object?>();

    public CompiledRuntime(IVeinCompiledProgram program, RuntimeSinks sinks, string? filesRoot = null)
    {
        _program = program;
        _sinks = sinks;
        _world = program.Create();
        _world.BaseDirectory = filesRoot;
        // The transport events never pass through the compiled queue (VeinWorld.Observed says so); the
        // interpreter's Emitted sees each of them, so they are reported here as it would report them.
        _world.Out = new LineWriter(line =>
        {
            sinks.Printed(line, false);
            Transport("Print", ("text", line));
        });
        _world.Err = new LineWriter(line =>
        {
            sinks.Printed(line, true);
            Transport("PrintError", ("text", line));
        });
        _world.Sound = (source, volume) =>
        {
            sinks.Sound(source, volume);
            Transport("PlaySound", ("source", source), ("volume", volume));
        };
        _world.Quitting = code => Transport("Quit", ("code", (long)code));
        _world.Observed = (key, payload) =>
        {
            if (sinks.WantsEvents()) sinks.Emitted(Interp.EventName(key), Fields(payload));
        };
    }

    public string Name => "compiled";
    public bool Running => !_world.Stopped;
    public int Tick => _world.Tick;
    public IReadOnlyList<string> MutedMarks => _world.MutedMarks.OrderBy(m => m, StringComparer.Ordinal).ToList();
    public IReadOnlyList<string> MutedShards => _world.MutedShards;

    public void Start() => Guard(_world.Start);

    public void Step(double delta) => Guard(() =>
    {
        _world.Clock(_world.Tick + 1, delta);
        _world.Frame();
    });

    public void Device(string name, IReadOnlyDictionary<string, object?> fields) =>
        Guard(() => _program.Raise(_world, name, fields, "device"));

    public bool Raise(string name, IReadOnlyDictionary<string, object?>? fields)
    {
        bool raised = false;
        Guard(() => raised = _program.Raise(_world, name, fields ?? None, "host"));
        return raised;
    }

    public bool Declared(string shape) => _program.Shapes.Contains(shape);
    public IReadOnlyList<long> Ids(string shape) => _program.Ids(_world, shape).Select(i => (long)i).ToList();
    public bool Has(long id, string shape) => _program.Has(_world, (int)id, shape);
    public bool Carries(long id, string mark) => _program.Carries(_world, (int)id, mark);
    public object? Read(long id, string shape, string field) => _program.Read(_world, (int)id, shape, field);

    /// EntityStore.Snapshot's shape: every identity carrying any shape, ascending; its shapes (by name)
    /// with their fields; its marks, sorted.
    public IReadOnlyList<VeinEntity> Snapshot()
    {
        var ids = new SortedSet<int>();
        foreach (string shape in _program.Shapes) ids.UnionWith(_program.Ids(_world, shape));

        var result = new List<VeinEntity>(ids.Count);
        foreach (int id in ids)
        {
            var shapes = new SortedDictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
            foreach (string shape in _program.Shapes)
            {
                if (!_program.Has(_world, id, shape)) continue;
                var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (string field in _program.Fields(shape)) fields[field] = _program.Read(_world, id, shape, field);
                shapes[shape] = fields;
            }
            var marks = _program.Marks(_world, id).OrderBy(m => m, StringComparer.Ordinal).ToList();
            result.Add(new VeinEntity(id, marks, shapes));
        }
        return result;
    }

    public bool Publish(long id, string shape, string field, object? value) =>
        _program.Publish(_world, (int)id, shape, field, value);

    public long Spawn() => _world.Spawn();

    public string? FilesRoot { set => _world.BaseDirectory = value; }

    public void Resume() => Guard(_world.Resume);
    public long SpawnAt(long id) => _world.SpawnAt((int)id);
    public bool SetMark(long id, string mark) => _program.SetMark(_world, (int)id, mark);

    /// A generated shard class keeps its `var`s as public fields of its own; read them by reflection — once
    /// per reload, never per frame.
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> ShardVars() =>
        _world.Systems.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<string, object?>)VarFields(kv.Value).ToDictionary(f => f.Name, f => Value(f.GetValue(kv.Value)), StringComparer.Ordinal),
            StringComparer.Ordinal);

    public bool SetShardVar(string shard, string name, object? value)
    {
        if (!_world.Systems.TryGetValue(shard, out var system)) return false;
        var field = VarFields(system).FirstOrDefault(f => f.Name == name);
        if (field is null) return false;
        try
        {
            object? converted = value is null || field.FieldType.IsInstanceOfType(value) ? value
                : value is IConvertible ? Convert.ChangeType(value, field.FieldType, System.Globalization.CultureInfo.InvariantCulture)
                : null;
            if (converted is null && field.FieldType.IsValueType) return false;
            field.SetValue(system, converted);
            return true;
        }
        catch (Exception) { return false; }   // a var whose type changed in the edit keeps its new start value
    }

    public IReadOnlyList<string> ShardsMutedByName => _world.ShardsMutedByName;
    public void RestoreMutes(IEnumerable<string> marks, IEnumerable<string> shardKeys) => _world.RestoreMutes(marks, shardKeys);

    private static FieldInfo[] VarFields(VeinSystem system) =>
        system.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

    private void Transport(string name, params (string Field, object? Value)[] fields)
    {
        if (!_sinks.WantsEvents()) return;
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (field, value) in fields) payload[field] = value;
        _sinks.Emitted(name, payload);
    }

    /// The interpreter catches a fault per unit and the game goes on; compiled code throws out of the
    /// frame. Reported the same way an unheard fault is (an error line), and the next frame runs.
    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { _sinks.Printed($"(error: {ex.GetType().Name}: {ex.Message})", true); }
    }

    /// A generated `Events.X` payload as the interpreter's fields: public fields by name, an Entity widened
    /// to long, and the provenance class (`from`) as a dictionary of its own fields.
    private static IReadOnlyDictionary<string, object?> Fields(object payload)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in PayloadFields.GetOrAdd(payload.GetType(), t => t.GetFields(BindingFlags.Public | BindingFlags.Instance)))
            result[field.Name] = Value(field.GetValue(payload));
        // The envelope as the interpreter shows it: no `source`, and `origin` empty outside a `target`
        // rather than entity 0 (which is "no entity" in both runtimes).
        // Only the envelope's own empty value: a program's field may be called `source` too (@PlaySound's is).
        if (result.GetValueOrDefault("source") is 0L) result.Remove("source");
        if (result.GetValueOrDefault("origin") is 0L) result["origin"] = null;
        return result;
    }

    private static object? Value(object? v) => v switch
    {
        int i => (long)i,
        null or string or long or double or bool => v,
        System.Collections.IEnumerable list => list.Cast<object?>().Select(Value).ToList(),
        _ when v.GetType().IsClass => Fields(v),
        _ => v,
    };
}
