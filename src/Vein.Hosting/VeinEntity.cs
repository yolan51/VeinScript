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

    /// The place's z — `$World.z`, else `$Position.z`. A 2D host draws a higher z on top.
    public double Depth => Has("World") ? Number("World", "z") : Number("Position", "z");

    /// `$Rotation.z` in radians — the 2D angle. Null when it carries no `$Rotation`.
    public double? Rotation => Has("Rotation") ? Number("Rotation", "z") : null;

    /// `$Scale` (x, y). Null when it carries no `$Scale`, which a host reads as (1, 1).
    public (double X, double Y)? Scale => Has("Scale") ? (Number("Scale", "x", 1), Number("Scale", "y", 1)) : null;

    /// `*Vein.UI.Widgets.$Text { content }` — words to draw at its placement.
    public string? Caption => Text("Text", "content");
}

/// One thing to draw this frame (VeinGame.Drawables): where, turned and scaled how, showing what. A value,
/// cheap to make every frame — the lean path a host's draw loop reads instead of `Entities`.
/// `Z` is the place's z — a 2D host's DRAW ORDER: higher is drawn on top, equal keeps creation order.
/// The looks (*Vein.Graphics.Looks): `Tint*` multiplies the picture (white, opaque without `$Tint`), `Flip*`
/// mirrors it, `Hidden` (`#Hidden`) means keep it but draw it nowhere. `Animation` is the clip `$Animation`
/// names, for an `$Image` that is a set of animated frames rather than one picture.
public readonly record struct VeinDrawable(
    long Id, double X, double Y, double Z, double Rotation, double ScaleX, double ScaleY, string? Image, string? Caption,
    double TintR = 1, double TintG = 1, double TintB = 1, double TintA = 1,
    bool FlipX = false, bool FlipY = false, bool Hidden = false,
    string? Animation = null, double AnimationSpeed = 1);

/// The view's centre and zoom (VeinGame.Camera), from the identity carrying `*Vein.UI.Surface.$Camera`.
/// A 2D host reads X, Y and Zoom. A 3D host also reads Z and the camera identity's `$Rotation` (radians,
/// 0 without one), and narrows the field of view as it zooms: `FieldOfView` is 70° at zoom 1.
public readonly record struct VeinCamera(
    long Id, double X, double Y, double Zoom,
    double Z = 0, double RotationX = 0, double RotationY = 0, double RotationZ = 0)
{
    public const double BaseFieldOfView = 70;

    /// Degrees, for a 3D host: 70 / zoom, kept between 1° and 179°.
    public double FieldOfView => Math.Clamp(BaseFieldOfView / Zoom, 1, 179);
}

/// One model to show this frame (VeinGame.Models): which, where, turned and sized how, playing what.
/// Rotation is radians about each axis; `Animation` is the clip `$Animation` names, or null without one.
public readonly record struct VeinModel(
    long Id, string Source,
    double X, double Y, double Z,
    double RotationX, double RotationY, double RotationZ,
    double ScaleX, double ScaleY, double ScaleZ,
    string? Animation, double AnimationSpeed, bool Hidden = false);

/// One collision box this frame (VeinGame.Colliders), from `*Vein.Game.Collision.$Collider`: centred on the
/// identity's place, HALF-extents on each axis (a 2D host ignores Z and HalfDepth), turned by `$Rotation`.
public readonly record struct VeinCollider(
    long Id, double X, double Y, double Z,
    double HalfWidth, double HalfHeight, double HalfDepth,
    double RotationX, double RotationY, double RotationZ);
