using Vein.Compiler.Parsing;

namespace Vein.Compiler.Tooling;

/// One bundle in a linked app, and what it does with each event.
public sealed record BundleWiring(string Bundle, bool IsPrincipal, IReadOnlyList<string> Emits, IReadOnlyList<string> Hears);

/// Who emits an event and who hears it, across the bundles of one app.
public sealed record EventWiring(string Event, IReadOnlyList<string> Emitters, IReadOnlyList<string> Hearers)
{
    /// Crosses a bundle boundary — the whole reason an app is one runtime rather than several.
    public bool IsCrossBundle =>
        Emitters.Count > 0 && Hearers.Count > 0 &&
        Hearers.Any(h => !Emitters.Contains(h, StringComparer.Ordinal));
}

// WHY: `app_capabilities/shop.app.vein` links four bundles into ONE runtime with one handler table and
// one event queue — which is exactly what lets a `hear` in a capability bundle see an `emit` from the
// principal. That is the app's whole point and there was no way to see it: each bundle reads as a
// self-contained file, and the wiring between them exists only at link time.
//
// Per-bundle rather than per-file, because the composition is a fact about the LINK. A reader looking
// at Billing.vein cannot tell whether anything hears what it emits; a reader looking at this can.
public sealed class AppComposition
{
    public required IReadOnlyList<BundleWiring> Bundles { get; init; }
    public required IReadOnlyList<EventWiring> Events { get; init; }

    public static AppComposition Empty => new() { Bundles = Array.Empty<BundleWiring>(), Events = Array.Empty<EventWiring>() };

    /// Events that leave the bundle that emits them. These are the app's actual seams.
    public IEnumerable<EventWiring> CrossBundle => Events.Where(e => e.IsCrossBundle);

    /// Emitted somewhere in the app and heard nowhere. In a single bundle that is often fine — someone
    /// else may hear it — but inside a linked app there IS no one else, so it is dead.
    public IEnumerable<EventWiring> Unheard => Events.Where(e => e.Emitters.Count > 0 && e.Hearers.Count == 0);

    /// Build from the units the app links, principal first.
    public static AppComposition Analyze(IReadOnlyList<(string Name, CompilationUnit Unit, bool IsPrincipal)> linked)
    {
        var bundles = new List<BundleWiring>();
        var emitters = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var hearers = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (name, unit, principal) in linked)
        {
            var index = DefinitionIndex.Analyze(unit);

            var emits = index.Sites.Where(s => s.Kind == Project.SymbolKind.Event && s.Role == SiteRole.Emit)
                                   .Select(s => s.Name).Distinct(StringComparer.Ordinal)
                                   .OrderBy(n => n, StringComparer.Ordinal).ToList();
            var hears = index.Sites.Where(s => s.Kind == Project.SymbolKind.Event && s.Role == SiteRole.Hear)
                                   .Select(s => s.Name).Distinct(StringComparer.Ordinal)
                                   .OrderBy(n => n, StringComparer.Ordinal).ToList();

            bundles.Add(new BundleWiring(name, principal, emits, hears));

            foreach (string e in emits) Add(emitters, e, name);
            foreach (string h in hears) Add(hearers, h, name);
        }

        var events = emitters.Keys.Concat(hearers.Keys).Distinct(StringComparer.Ordinal)
            .OrderBy(e => e, StringComparer.Ordinal)
            .Select(e => new EventWiring(e,
                emitters.TryGetValue(e, out var em) ? em : Array.Empty<string>(),
                hearers.TryGetValue(e, out var he) ? he : Array.Empty<string>()))
            .ToList();

        return new AppComposition { Bundles = bundles, Events = events };
    }

    private static void Add(Dictionary<string, List<string>> map, string key, string bundle)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = new List<string>();
        if (!list.Contains(bundle, StringComparer.Ordinal)) list.Add(bundle);
    }
}
