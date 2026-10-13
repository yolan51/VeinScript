namespace Vein.Hosting;

/// Which engine runs the rules (VeinGame.Open).
public enum VeinRuntime
{
    /// Compiled when the whole program compiles with nothing missing; otherwise the interpreter, with
    /// `VeinGame.RuntimeNote` saying why. The default.
    Auto,
    /// The interpreter: the whole language, slower.
    Interpreter,
    /// Compiled C# (~14–25× the interpreter). A program that cannot be compiled without losing behaviour
    /// still runs — on the interpreter — and `RuntimeNote` says so: a game never refuses to start.
    Compiled,
}

/// One runtime behind VeinGame — the interpreter (InterpRuntime) or a compiled program (CompiledRuntime).
/// Everything VeinGame offers a host (Drawables, Camera, Entities, `$View`, …) is written ONCE against
/// this, which is what keeps the two runtimes giving the same answers.
///
/// Values come back the interpreter's way: `int` as long, `float` as double, an Entity as long.
internal interface IGameRuntime
{
    string Name { get; }
    bool Running { get; }
    int Tick { get; }
    IReadOnlyList<string> MutedMarks { get; }
    IReadOnlyList<string> MutedShards { get; }

    void Start();

    /// One frame: the clock event (`@Ticked` with `delta`), every tick/frame/settled block, then any
    /// `every N` whose seconds have passed on this clock.
    void Step(double delta);

    /// A device event the runtime raises — KeyDown, MouseMove, SoundEnded, … — with its fields.
    void Device(string name, IReadOnlyDictionary<string, object?> fields);

    /// One of the program's own events, from the host. False when the program has no such event.
    bool Raise(string name, IReadOnlyDictionary<string, object?>? fields);

    // ---- reading (and the host's one kind of write) ----------------------------------------------

    bool Declared(string shape);
    IReadOnlyList<long> Ids(string shape);
    bool Has(long id, string shape);
    bool Carries(long id, string mark);
    object? Read(long id, string shape, string field);
    IReadOnlyList<VeinEntity> Snapshot();

    /// A committed write of one field, attaching the shape if needed — what keeps `$View` current.
    bool Publish(long id, string shape, string field, object? value);
    long Spawn();
}

/// Where a runtime reports to — VeinGame's events.
internal sealed record RuntimeSinks(
    Action<string, bool> Printed,
    Action<string, double> Sound,
    Action<string, IReadOnlyDictionary<string, object?>> Emitted,
    Func<bool> WantsEvents);
