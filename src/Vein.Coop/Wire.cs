using LiteNetLib.Utils;

namespace Vein.Coop;

/// What travels between a host and its partners. One byte says which message; the rest is LiteNetLib's
/// NetDataWriter in the order written here, so reading is writing backwards.
///
///   host → client, reliable and in order (channel 0):
///     Welcome  player                                     you are this player
///     Spawn    id, marks[], shapes[{ name, fields[{ name, value }] }]
///     Set      id, shape, field, value
///     Mark     id, mark, on
///     Destroy  id
///     Event    name, fields[{ name, value }]              a relayed @Hud, re-raised on the client
///     Sound    source, volume
///   host → client, unreliable (channel 1):
///     Motion   time, [id, px, py, pz, rx, ry, rz]…         as many as fit; a lost one is replaced by the next
///   client → host, reliable:
///     Input    name, fields[{ name, value }]              this machine's ActionPressed / ActionReleased
///
/// Ids are the HOST's identity ids; a client keeps its own map to the mirrors it made.
internal enum Kind : byte { Welcome = 1, Spawn, Set, Mark, Destroy, Event, Sound, Motion, Input = 20 }

internal static class Wire
{
    // A value as the rules hold it: int as long, float as double, text, bool. Anything else (a list) is
    // not sent — the reader sees null and the field keeps its default on the mirror.
    private enum Tag : byte { Null, Long, Double, Text, Bool }

    public static void Value(NetDataWriter w, object? v)
    {
        switch (v)
        {
            case long l: w.Put((byte)Tag.Long); w.Put(l); break;
            case int i: w.Put((byte)Tag.Long); w.Put((long)i); break;
            case double d: w.Put((byte)Tag.Double); w.Put(d); break;
            case float f: w.Put((byte)Tag.Double); w.Put((double)f); break;
            case string s: w.Put((byte)Tag.Text); w.Put(s); break;
            case bool b: w.Put((byte)Tag.Bool); w.Put(b); break;
            default: w.Put((byte)Tag.Null); break;
        }
    }

    public static object? Value(NetDataReader r) => (Tag)r.GetByte() switch
    {
        Tag.Long => r.GetLong(),
        Tag.Double => r.GetDouble(),
        Tag.Text => r.GetString(),
        Tag.Bool => r.GetBool(),
        _ => null,
    };

    public static void Fields(NetDataWriter w, IReadOnlyDictionary<string, object?> fields)
    {
        var sendable = fields.Where(f => Sendable(f.Value)).ToList();
        w.Put(sendable.Count);
        foreach (var (name, value) in sendable) { w.Put(name); Value(w, value); }
    }

    public static Dictionary<string, object?> Fields(NetDataReader r)
    {
        int n = r.GetInt();
        var fields = new Dictionary<string, object?>(n, StringComparer.Ordinal);
        for (int i = 0; i < n; i++) { string name = r.GetString(); fields[name] = Value(r); }
        return fields;
    }

    public static bool Sendable(object? v) => v is long or int or double or float or string or bool;
}
