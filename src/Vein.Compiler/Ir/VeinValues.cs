namespace Vein.Compiler.Ir;

/// What a VeinScript value does — `+`, `<`, `==`, truth, text, `xs[i]`, `d.field`, the pure built-ins — for
/// code compiled by the C# backend, which calls these where a value has no C# type it can know: a record
/// from `fromJson`, a `dictionary` parameter, a list element.
///
/// EVERY MEMBER IS THE INTERPRETER'S OWN CODE, not a copy of it. The backend's first helpers (`__VeinText`)
/// were rewritten in the emitted file and had already drifted — `S` wrote a record as its .NET type name,
/// where `Interp.Str` writes `{a: 1}`. Two implementations of one rule is the divergence
/// docs/BACKEND-CONTRACT.md exists to prevent; one implementation called from both runtimes cannot diverge.
public static class VeinValues
{
    public static object? Add(object? l, object? r) => Interp.Binary(IrBinOp.Add, l, r);
    public static object? Sub(object? l, object? r) => Interp.Binary(IrBinOp.Sub, l, r);
    public static object? Mul(object? l, object? r) => Interp.Binary(IrBinOp.Mul, l, r);
    public static object? Div(object? l, object? r) => Interp.Binary(IrBinOp.Div, l, r);
    public static object? Mod(object? l, object? r) => Interp.Binary(IrBinOp.Mod, l, r);
    public static bool Eq(object? l, object? r) => Interp.LooseEq(l, r);
    public static bool Lt(object? l, object? r) => Interp.Ordering(l, r) < 0;
    public static bool Gt(object? l, object? r) => Interp.Ordering(l, r) > 0;
    public static bool Le(object? l, object? r) => Interp.Ordering(l, r) <= 0;
    public static bool Ge(object? l, object? r) => Interp.Ordering(l, r) >= 0;

    /// `-x`: an int stays an int.
    public static object Neg(object? v) => v is long or int ? unchecked(-Interp.AsLong(v)) : -Interp.AsDouble(v);

    public static bool Truthy(object? v) => Interp.Truthy(v);
    public static string Str(object? v) => Interp.Str(v);

    /// The value a typed slot holds when an untyped one is put in it — `int(x)`'s rule, so a parsed `"5"`
    /// becomes 5 and a float truncates, as everywhere else a value turns into a number.
    public static long Int(object? v) => Interp.ToLong(v);
    public static double Float(object? v) => Interp.ToDouble(v);

    /// `recv[key]` on a list, a record, a `~<map>` or a text.
    public static object? Index(object? recv, object? key) => Interp.Index(recv, key);

    /// `d.field` on a record; nothing else here has fields a compiled program reads by name.
    public static object? Field(object? recv, string name) =>
        recv is IDictionary<string, object?> d && d.TryGetValue(name, out var v) ? v : null;

    /// `xs[i] = v` / `m["k"] = v`: in place, like the read.
    public static void Set(object? recv, object? key, object? value) => ListOps.Set(recv, key, value);

    /// `d.field = v` on a record.
    public static void SetField(object? recv, string name, object? value)
    {
        if (recv is IDictionary<string, object?> d) d[name] = value;
    }

    /// What `target <expr> as x` walks: a record as its entries, anything else that is a collection as a
    /// copy taken first, and a text or a single value as nothing.
    public static IEnumerable<object?> Items(object? source) => source switch
    {
        IDictionary<string, object?> record => Interp.Entries(record),
        string => Array.Empty<object?>(),
        System.Collections.IEnumerable e => e.Cast<object?>().ToList(),
        _ => Array.Empty<object?>(),
    };

    /// A built-in that needs only its arguments: `fromJson`, `toJson`, `split`, `sort`, `slice`, `len`, `int`, …
    public static object? Call(string name, params object?[] args) => Interp.PureBuiltin(name, new List<object?>(args));
}
