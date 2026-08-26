using ShardECS.SECS;
using Vein.Compiler.Ir;

namespace Vein.Runtime.SECS;

/// Skeleton of the SECS runtime backend. It will materialize a lowered <see cref="IrModule"/> into a
/// live ShardECS world — shapes → components, events → publish/subscribe, `target` queries → entities,
/// shard schedules → the tick loop. For now it only establishes the build linkage between the VeinScript
/// toolchain (net8 <c>Vein.Compiler</c>) and the ShardECS SECS core (net9). See docs/BACKEND-CONTRACT.md.
public sealed class SecsRuntime
{
    private readonly Secs _secs = new();

    /// Placeholder that proves the toolchain links against ShardECS.SECS: it touches the SECS API and
    /// the VeinScript HIR in one method. Returns the id of a throwaway entity.
    public int Probe(IrModule module)
    {
        _ = module.Name;                 // consumes the lowered HIR
        return _secs.CreateEntity();     // touches the live SECS world
    }

    // TODO(next): map module.Types (Component) → secs.Add<…>, module events → Publish/Subscribe,
    //             shard schedules → dressers/drawers, then secs.Run(ct). See BACKEND-CONTRACT.md §2.
}
