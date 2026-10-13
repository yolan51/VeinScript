namespace Vein.Hosting;

/// One identity as a host sees it: an id that stays the same for its whole life (ids are never reused),
/// its marks, and every shape it carries with that shape's field values.
///
/// Shapes are keyed by their bare name — `Position`, `Image` — because components unify by name across
/// bundles (RULES 14e), so `*Vein.Transform.Spatial.$Position` and a game's own `$Position` are the same
/// table. The helpers below cover the two a 2D host draws from; anything else is in `Shapes`.
public sealed record VeinEntity(
    long Id,
    IReadOnlyList<string> Marks,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Shapes)
{
    public bool Has(string shape) => Shapes.ContainsKey(shape);

    public bool Carries(string mark) => Marks.Contains(mark, StringComparer.Ordinal);

    /// A numeric field, or `fallback` when the shape or field is absent. Ints and floats both read here:
    /// the interpreter keeps `int` fields as `long` and `float` as `double`.
    public double Number(string shape, string field, double fallback = 0) =>
        Shapes.TryGetValue(shape, out var fields) && fields.TryGetValue(field, out var v)
            ? v switch { long l => l, int i => i, double d => d, float f => f, _ => fallback }
            : fallback;

    /// A text field, or null when the shape or field is absent.
    public string? Text(string shape, string field) =>
        Shapes.TryGetValue(shape, out var fields) && fields.TryGetValue(field, out var v) ? v?.ToString() : null;

    /// `$Position` (x, y), when it has one — where a 2D host puts it.
    public (double X, double Y)? Position =>
        Has("Position") ? (Number("Position", "x"), Number("Position", "y")) : null;

    /// `*Vein.UI.Widgets.$Image { source }` — what a 2D host draws there.
    public string? Image => Text("Image", "source");

    /// Where to DRAW it: `$World` (where it actually is, after its parents — written by the runtime) when
    /// the program has one, else `$Position`. A child drawn at its `$Position` would sit at its offset from
    /// the origin instead of from its parent.
    public (double X, double Y)? Placement =>
        Has("World") ? (Number("World", "x"), Number("World", "y")) : Position;

    /// `$Rotation.z` in radians — the 2D angle. Null when it carries no `$Rotation`.
    public double? Rotation => Has("Rotation") ? Number("Rotation", "z") : null;

    /// `$Scale` (x, y). Null when it carries no `$Scale`, which a host reads as (1, 1).
    public (double X, double Y)? Scale => Has("Scale") ? (Number("Scale", "x", 1), Number("Scale", "y", 1)) : null;

    /// `*Vein.UI.Widgets.$Text { content }` — words to draw at its placement.
    public string? Caption => Text("Text", "content");
}
