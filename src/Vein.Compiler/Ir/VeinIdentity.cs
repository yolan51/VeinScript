namespace Vein.Compiler.Ir;

// Unified runtime identity for VeinScript First-Class objects (Shard, ShardView, Bridge, Publicator,
// and future kinds). ONE mechanism — not a per-class scheme. The developer never writes an id; the
// runtime allocates one when a First-Class object is registered.
//
// This is the SECOND identity layer: it identifies the execution/declaration objects themselves,
// distinct from ECS *entity* identity (#Player, #Enemy). See the design spec.

public enum VeinKind { Shard, ShardView, Bridge, Publicator, Runtime }

/// An opaque runtime identity. Code should not depend on its format — treat it as a handle. The
/// Display string is for diagnostics/tracing only.
public readonly record struct VeinIdentity(int Handle, string Display)
{
    public override string ToString() => Display;
}

/// The runtime representation of a First-Class object: a stable identity + human-readable metadata.
/// (Definition-level today; the Identity abstraction leaves room for per-instance ids later.)
public sealed class VeinFirstClass
{
    public required VeinIdentity Identity { get; init; }
    public required string Name { get; init; }
    public required VeinKind Kind { get; init; }

    /// Shapes/marks this First-Class object carries; an `audience` barrier matches against these.
    public IReadOnlyList<string> CarriedShapes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> CarriedMarks { get; init; } = Array.Empty<string>();

    public override string ToString() => $"{Kind}.{Name}";
}

/// Central allocator/registry. All First-Class objects register here; nothing else generates ids.
/// Doubles as the node set of the "Vein First-Class graph" used for provenance/routing/diagnostics.
public sealed class VeinIdentityRegistry
{
    private int _seq;
    private readonly List<VeinFirstClass> _all = new();
    private readonly Dictionary<int, VeinFirstClass> _byHandle = new();

    public VeinFirstClass Register(string name, VeinKind kind,
        IReadOnlyList<string>? carriedShapes = null, IReadOnlyList<string>? carriedMarks = null)
    {
        int handle = ++_seq;
        var id = new VeinIdentity(handle, $"VF:{kind}:{name}#{handle}");
        var fc = new VeinFirstClass
        {
            Identity = id, Name = name, Kind = kind,
            CarriedShapes = carriedShapes ?? Array.Empty<string>(),
            CarriedMarks = carriedMarks ?? Array.Empty<string>()
        };
        _all.Add(fc);
        _byHandle[handle] = fc;
        return fc;
    }

    public VeinFirstClass? Resolve(VeinIdentity id) => _byHandle.GetValueOrDefault(id.Handle);
    public IReadOnlyList<VeinFirstClass> All => _all;
}

/// The ECS *entity* id allocator — the SECOND identity layer (see the note atop this file). Entities
/// are what `target` cycles and what `self`/`Entity` refer to; ids are plain ints, distinct from the
/// First-Class handles above. `0` is reserved for "no entity" (used where there is no entity context).
public sealed class VeinEntityRegistry
{
    private int _seq;
    /// Allocate the next entity id (always >= 1; 0 means "no entity").
    public int Allocate() => ++_seq;
    public int Count => _seq;
}
