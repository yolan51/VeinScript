using Vein.Compiler.Parsing;
using Vein.Compiler.Tooling;

namespace Vein.Compiler.Project;

// The combined, qualified symbol table for a multi-bundle app (see ProjectLoader). Every named member
// across all loaded bundles is recorded with its full author→bundle→publicator path, so the `*`
// discovery/reference mechanism can list them and disambiguate name collisions between authors.
// This is the surface + tooling layer: it does NOT link or run the bundles.

public enum SymbolKind { Bundle, Publicator, Shape, Event, Builder, Shard, ShardView, Bridge, SF, Var }

/// One discoverable member, fully qualified. The canonical name is `*Author.Bundle[.Publicator].member`
/// with the member carrying its sigil (`@`/`$`) where it has one.
public sealed record QualifiedSymbol(
    string Author, string Bundle, string? Publicator,
    SymbolKind Kind, string Name, string? Type = null, string? Doc = null)
{
    public string Sigil => Kind switch { SymbolKind.Event => "@", SymbolKind.Shape => "$", _ => "" };

    /// The owner path (author → bundle → publicator). The member is NOT part of it.
    public IReadOnlyList<string> PathSegments =>
        Publicator is null ? new[] { Author, Bundle } : new[] { Author, Bundle, Publicator };

    /// `*Author.Bundle.Publicator.@Event` — the collision-proof canonical form.
    public string QualifiedName => "*" + string.Join(".", PathSegments) + "." + Sigil + Name;

    /// The member as written after the last dot (`@Event`, `$Shape`, or a plain name).
    public string SigilName => Sigil + Name;
}

/// A loaded bundle's boot signature, so an app dev can discover what its `start` payload looks like and
/// which fields a load-site `start { … }` override may fill.
public sealed record BundleStart(string Author, string Bundle, string Event, IReadOnlyList<Sig.Field> Fields)
{
    public string Signature =>
        $"start @{Event} {{ {string.Join(", ", Fields.Select(f => f.Name + ": " + f.Type))} }}";
}

public enum ResolveStatus { Resolved, Unresolved, Ambiguous }
public sealed record ResolveResult(ResolveStatus Status, QualifiedSymbol? Symbol, IReadOnlyList<QualifiedSymbol> Candidates);

public sealed class ProjectModel
{
    public required string AppName { get; init; }
    public required IReadOnlyList<QualifiedSymbol> Symbols { get; init; }

    /// The boot signature of every loaded bundle that declares a `start`.
    public IReadOnlyList<BundleStart> Starts { get; init; } = Array.Empty<BundleStart>();

    /// Simple member names (with sigil) reachable under more than one distinct owner path — these are
    /// the collisions that FORCE the user to qualify a `*` reference with more segments (up to author).
    public IReadOnlyList<string> Collisions =>
        Symbols.GroupBy(s => s.SigilName)
               .Where(g => g.Select(s => string.Join(".", s.PathSegments)).Distinct(StringComparer.Ordinal).Count() > 1)
               .Select(g => g.Key)
               .OrderBy(x => x, StringComparer.Ordinal)
               .ToList();

    /// Resolve a `*` path by trailing-segment match: the given `path` must be a contiguous suffix of a
    /// symbol's owner path, and `member` (sigil+name) must match. 0 → Unresolved, 1 → Resolved, >1 →
    /// Ambiguous (the caller should tell the user to add more leading segments, i.e. the author).
    public ResolveResult Resolve(IReadOnlyList<string> path, string member)
    {
        var hits = Symbols.Where(s => s.SigilName == member && EndsWith(s.PathSegments, path)).ToList();
        return hits.Count switch
        {
            0 => new ResolveResult(ResolveStatus.Unresolved, null, hits),
            1 => new ResolveResult(ResolveStatus.Resolved, hits[0], hits),
            _ => new ResolveResult(ResolveStatus.Ambiguous, null, hits)
        };
    }

    private static bool EndsWith(IReadOnlyList<string> full, IReadOnlyList<string> suffix)
    {
        if (suffix.Count > full.Count) return false;
        for (int i = 0; i < suffix.Count; i++)
            if (!string.Equals(full[full.Count - suffix.Count + i], suffix[i], StringComparison.Ordinal)) return false;
        return true;
    }

    /// A human-readable listing grouped by author→bundle, flagging collisions.
    public string Render()
    {
        var collisions = Collisions.ToHashSet(StringComparer.Ordinal);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"app {AppName}  ({Symbols.Count} symbols, {collisions.Count} name collision(s))");
        foreach (var g in Symbols.GroupBy(s => (s.Author, s.Bundle)).OrderBy(g => g.Key.Author).ThenBy(g => g.Key.Bundle))
        {
            sb.AppendLine($"  bundle {g.Key.Bundle} by {g.Key.Author}");
            var boot = Starts.FirstOrDefault(x => x.Author == g.Key.Author && x.Bundle == g.Key.Bundle);
            if (boot is not null) sb.AppendLine($"    {boot.Signature}   (boot — fill via `load … start {{ … }}`)");
            foreach (var s in g.OrderBy(x => x.Publicator ?? "").ThenBy(x => x.Kind).ThenBy(x => x.Name, StringComparer.Ordinal))
            {
                string flag = collisions.Contains(s.SigilName) ? "   [COLLISION — qualify with author]" : "";
                sb.AppendLine($"    {s.QualifiedName}   ({s.Kind}){flag}");
            }
        }
        return sb.ToString();
    }
}
