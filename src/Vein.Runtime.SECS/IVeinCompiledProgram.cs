namespace Vein.Runtime.SECS;

/// What a HOST needs from a compiled VeinScript program — implemented by the `__Host` class the C# backend
/// generates into every program (CSharpBackend.EmitHost), so an engine can create, drive and read the
/// world with plain calls and no reflection per frame. The interpreter's equivalents are Interp.World
/// (EntityStore) and Interp.FireHost; Vein.Hosting puts both behind one interface.
///
/// Values cross this boundary the way the interpreter holds them: `int` fields as `long`, `float` as
/// `double`, an Entity as `long`, text and enum cases as `string`, `bool` as `bool`.
public interface IVeinCompiledProgram
{
    /// A world with every shard registered and its hooks set — NOT started. The host calls
    /// `Start()` once, then per frame `Clock(frame, delta)` and `Frame()`.
    VeinWorld Create();

    /// Shape names this program has as component types (`Position`, `Image`, …).
    IReadOnlyList<string> Shapes { get; }

    /// A shape's field names, in declaration order; empty for a shape the program does not have.
    IReadOnlyList<string> Fields(string shape);

    /// Live identities carrying `shape`, ascending; empty for a shape the program does not have.
    IReadOnlyList<int> Ids(VeinWorld world, string shape);

    bool Has(VeinWorld world, int id, string shape);

    /// A field's committed value, or null when the identity does not carry the shape or the field is
    /// not one of its fields.
    object? Read(VeinWorld world, int id, string shape, string field);

    /// The marks an identity carries, by name.
    IReadOnlyList<string> Marks(VeinWorld world, int id);

    /// Whether an identity carries one mark, by name — `Marks` without building a list, for a host that
    /// asks one question of every identity every frame (`#Hidden`). False for a mark the program lacks.
    bool Carries(VeinWorld world, int id, string mark);

    /// Put a mark on an identity now, by name — a host restoring a saved world. False for a mark the
    /// program lacks.
    bool SetMark(VeinWorld world, int id, string mark);

    /// Take a mark off an identity now, by name. False for a mark the program lacks — and for a program
    /// generated before this existed.
    bool Unmark(VeinWorld world, int id, string mark) => false;

    /// Raise one of the program's events — by bare name (`KeyDown`, `Paused`) or by key — from
    /// `sender` (a runtime identity: "device" for input, "host" for the engine's own), then drain.
    /// False when the program has no such event: a misspelt name is reported, never delivered to nobody.
    bool Raise(VeinWorld world, string eventName, IReadOnlyDictionary<string, object?> fields, string sender);

    /// Write one field of a shape on an identity, attaching the shape (its other fields zero) when the
    /// identity does not carry it yet — a committed write, as `EntityStore.Publish` is: what a host does
    /// to keep `$View` current. False for a shape or field the program does not have.
    bool Publish(VeinWorld world, int id, string shape, string field, object? value);
}
