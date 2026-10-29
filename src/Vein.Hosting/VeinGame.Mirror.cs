namespace Vein.Hosting;

/// MIRRORS — a host writing the world, not just reading it. The rules own their identities; these are for
/// identities the HOST owns: a co-op client showing what another machine simulates (Vein.Coop), an editor
/// placing a prop, a replay. Every write is committed at once, between frames — the way `$View` is
/// (SetView) — so the next Step, Entities or Drawables sees it, and no fold or `attach` competes with it.
///
/// Values go in the way the rules hold them: `int` as long, `float` as double, an Entity as long, text as
/// string, `bool` as bool. A shape, field or mark the program does not declare is refused (false).
public sealed partial class VeinGame
{
    /// A new identity, with nothing on it. -1 when nothing is running.
    public long Spawn()
    {
        if (!Running) return -1;
        long id = _runtime!.Spawn();
        Changed();
        return id;
    }

    /// Write one field, attaching the shape (its other fields at their declared defaults) when the identity
    /// does not carry it yet.
    public bool Set(long id, string shape, string field, object? value)
    {
        if (!Running || !_runtime!.Alive(id)) return false;
        bool ok = _runtime.Publish(id, shape, field, value);
        Changed();
        return ok;
    }

    /// Colliders the host sizes: an identity's `*Vein.Game.Collision.$Collider` left at 0 × 0 takes the
    /// half-extents of what the host draws for it — a model's bounds, a picture's rectangle — so a program
    /// need not repeat an asset's size. Only a collider still at 0 × 0 is written: a size the rules gave is
    /// theirs, and asking again is free. True when it wrote.
    public bool SizeCollider(long id, double halfWidth, double halfHeight, double halfDepth = 0)
    {
        if (!Running || !_runtime!.Alive(id) || !_runtime.Declared("Collider") || !_runtime.Has(id, "Collider")) return false;
        if (Num(_runtime, id, "Collider", "width", 0) != 0 || Num(_runtime, id, "Collider", "height", 0) != 0) return false;
        if (halfWidth <= 0 && halfHeight <= 0) return false;
        _runtime.Publish(id, "Collider", "width", halfWidth);
        _runtime.Publish(id, "Collider", "height", halfHeight);
        _runtime.Publish(id, "Collider", "depth", halfDepth);
        Changed();
        return true;
    }

    /// Put a mark on an identity.
    public bool Mark(long id, string mark)
    {
        if (!Running || !_runtime!.Alive(id)) return false;
        bool ok = _runtime.SetMark(id, mark);
        Changed();
        return ok;
    }

    /// Take a mark off an identity.
    public bool Unmark(long id, string mark)
    {
        if (!Running || !_runtime!.Alive(id)) return false;
        bool ok = _runtime.Unmark(id, mark);
        Changed();
        return ok;
    }

    /// Destroy an identity. False when it was not alive.
    public bool Destroy(long id)
    {
        if (!Running || !_runtime!.Alive(id)) return false;
        _runtime.Destroy(id);
        Changed();
        return true;
    }

    /// `mute #M` from outside (D20): every shard carrying the mark goes silent, now. What a co-op client does
    /// to the shards only the host may run (`#Authority`).
    public void Mute(string mark)
    {
        if (!Running) return;
        _runtime!.RestoreMutes(new[] { mark }, Array.Empty<string>());
    }
}
