using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Vein.Compiler.Project;

namespace Vein.Workbench;

// One icon per VeinScript primitive — the art in Assets/primitives, addressed by concept.
//
// WHY A LOOKUP AND NOT A PATH AT EACH USE SITE. The panels that show primitives all ask the same
// question in different words: the outline asks by `SymbolKind`, the bundle explorer by
// `PrimitiveKind`, the bottom tabs by the name of a concept that is not a declaration at all
// (diagnostics, dependencies, the tick). Spelling `avares://…/shape.png` in each of them would be
// three places to update when the art moves, and three chances to typo a URI that fails at RUNTIME —
// `AssetLoader.Open` throws on a missing asset, so a mistyped path is a crash on the first paint of a
// panel, not a build error.
//
// DECODED SMALL, ONCE. The sources are 314x314 and there are sixteen of them: held at full size that
// is a few megabytes of bitmap for icons drawn at fourteen pixels. `DecodeToWidth` reads them at the
// size actually wanted, and the cache means a panel rebuilding its list a hundred times decodes
// nothing after the first.
internal static class PrimitiveIcons
{
    /// What the outline draws at. Small enough to sit on a text line, large enough that the 2x
    /// screenshot of a HiDPI display still has pixels to work with.
    public const int Size = 28;

    private static readonly Dictionary<string, Bitmap?> _cache = new(StringComparer.Ordinal);

    /// The icon for a declaration kind, or null when the kind has no art.
    ///
    /// Null rather than a placeholder: `Var` is a local, and a local is not a primitive. A caller that
    /// gets null shows what it showed before, which is the sigil and the name.
    public static Bitmap? For(SymbolKind kind) => Named(FileFor(kind));

    /// The icon for a concept that is not a declaration — `"diagnostic"`, `"dependency"`, `"tick"`,
    /// `"target"`, `"fold"`, `"import"`, `"identity"`. The manifest's names, used verbatim.
    public static Bitmap? Named(string? name)
    {
        if (name is null) return null;
        if (_cache.TryGetValue(name, out var hit)) return hit;

        Bitmap? bitmap;
        try
        {
            using var stream = AssetLoader.Open(
                new Uri($"avares://VeinScript-Workbench/Assets/primitives/{name}.png"));
            bitmap = Bitmap.DecodeToWidth(stream, Size);
        }
        catch (Exception)
        {
            // A missing icon is a cosmetic problem and must not be a crash on a panel's first paint.
            // Cached as null so a broken name is looked up once rather than throwing on every row.
            bitmap = null;
        }

        _cache[name] = bitmap;
        return bitmap;
    }

    /// SymbolKind to filename. `ShardView` takes the shard icon deliberately — a view IS a shard that
    /// renders, and inventing a second crystal to mean nearly the same thing would say less than
    /// reusing the first.
    private static string? FileFor(SymbolKind kind) => kind switch
    {
        SymbolKind.Bundle => "bundle",
        SymbolKind.Publicator => "publicator",
        SymbolKind.Shape => "shape",
        SymbolKind.Mark => "mark",
        SymbolKind.Event => "event",
        SymbolKind.Builder => "builder",
        SymbolKind.Shard or SymbolKind.ShardView => "shard",
        SymbolKind.Bridge => "bridge",
        SymbolKind.SF or SymbolKind.Fn => "function",
        _ => null,          // Var — a local, not a primitive
    };
}
