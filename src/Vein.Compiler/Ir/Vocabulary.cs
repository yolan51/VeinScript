using System.Text.RegularExpressions;

namespace Vein.Compiler.Ir;

/// The standard shapes (D32, docs/VOCABULARY.md): names every domain shares, and that hosts, collision and Coop read
/// BY NAME. Shapes unify by bare name (RULES 15b), so a bundle that says `$Position` gets the one everyone reads. A
/// bundle that says `$Position2d` gets a new, unrelated component nothing else reads — its identity does not draw,
/// move or sync, and nothing said why. That second case is what `LookalikeOf` catches (VS0411).
///
/// The core layer only: it is in the stdlib today. The game layer (`$Health`, `$Mana` …) joins when it exists in
/// VeinScriptBundle (D32 stage 3), not before — a warning naming a standard that is not declared anywhere would send
/// the author looking for nothing.
public static class Vocabulary
{
    /// Name → what it is, as the warning says it. Keep in step with the core table of docs/VOCABULARY.md.
    public static readonly IReadOnlyDictionary<string, string> CoreShapes = new Dictionary<string, string>
    {
        ["Name"] = "a display name",
        ["Owner"] = "the identity this one belongs to",
        ["Parent"] = "the tree parent",
        ["Place"] = "the order among siblings",
        ["Layer"] = "the sorting / rendering layer",
        ["Position"] = "the world position (local under a $Parent); a 2D game uses $Position with z = 0",
        ["World"] = "where the identity is after its parents, written by the runtime",
        ["Rotation"] = "the Euler angles, radians; a 2D game uses z",
        ["Scale"] = "the scale; a 2D game leaves z at 1",
        ["Velocity"] = "world units per second, folds sum; a 2D game uses z = 0",
        ["Rect"] = "screen / layout space, pixels",
        ["Text"] = "the text an identity shows",
        ["Image"] = "the picture an identity shows",
        ["Tint"] = "the colour multiplier",
        ["Collider"] = "the collision half-extents",
        ["Body"] = "the physical response",
        ["Amount"] = "money, in cents",
    };

    /// Words that say "another copy of the same thing", not "a different thing". `$PoolSize` and `$WorkPool` carry a
    /// real second word and are left alone; `$MyPosition`, `$Position3D` and `$PositionData` do not.
    private static readonly HashSet<string> CopyWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "my", "new", "old", "base", "custom", "simple", "basic", "local", "the", "own",
        "data", "component", "comp", "info", "value", "val", "state", "type", "v",
    };

    // Camel words, with a digit run and its `d`/`D` as one word: Position2d -> Position | 2d, HTTPRect -> HTTP | Rect.
    private static readonly Regex Word = new(@"\d+[dD]?|[A-Z]+(?=[A-Z][a-z]|\d|_|$)|[A-Z]?[a-z]+", RegexOptions.Compiled);

    /// The standard shape `name` copies, or null. Whole camel words only — `$Player` holds the letters of `Layer`
    /// but not the word — and every other word must be a copy word, or a digit run (`2`, `2d`, `3D`; `V2` is `V` and `2`).
    public static string? LookalikeOf(string name)
    {
        if (CoreShapes.ContainsKey(name)) return null;
        var words = Word.Matches(name).Select(m => m.Value).ToList();
        if (words.Count < 2) return null;

        string? found = null;
        foreach (var w in words)
        {
            string cap = char.ToUpperInvariant(w[0]) + w[1..];
            if (found is null && CoreShapes.ContainsKey(cap)) { found = cap; continue; }
            if (CopyWords.Contains(w) || char.IsDigit(w[0])) continue;
            return null;
        }
        return found;
    }
}
