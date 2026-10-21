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
