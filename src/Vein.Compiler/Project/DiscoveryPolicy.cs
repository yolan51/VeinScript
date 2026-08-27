namespace Vein.Compiler.Project;

// Discovery policy — controls what `*` wildcard discovery ENUMERATES (autocomplete / browsing), so a huge
// app never has to surface its entire dependency universe. Read from a plain-text `vein.discovery` file:
//
//     silent all                    # nuclear: nothing discoverable unless exposed
//     expose Vein.Console           # Author | Author.Bundle | Author.Bundle.Publicator
//     silent acme.Combat.Internal   # re-silence a child under an exposed parent
//
// `silent` controls DISCOVERY; `shared` still controls CONSUMPTION — an explicit `*Vein.Silent.X` always
// resolves. So this only filters discovery surfaces, never resolution.
public sealed class DiscoveryPolicy
{
    private static readonly Dictionary<string, DiscoveryPolicy> _cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly bool _rootExpose;
    private readonly Dictionary<string, bool> _rules;   // path (Author[.Bundle[.Pub]]) → expose(true)/silent(false)

    private DiscoveryPolicy(bool rootExpose, Dictionary<string, bool> rules)
    {
        _rootExpose = rootExpose;
        _rules = rules;
    }

    /// The permissive default when no `vein.discovery` file exists — everything is discoverable.
    public static DiscoveryPolicy Permissive { get; } = new(true, new(StringComparer.Ordinal));

    public static DiscoveryPolicy Load(string? startDir)
    {
        var file = Locate(startDir);
        if (file is null) return Permissive;
        if (_cache.TryGetValue(file, out var cached)) return cached;
        var policy = Parse(File.ReadAllLines(file));
        _cache[file] = policy;
        return policy;
    }

    public static DiscoveryPolicy Parse(IEnumerable<string> lines)
    {
        bool rootExpose = true;   // no `silent all`/`expose all` → permissive default
        var rules = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash].Trim();
            if (line.Length == 0) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0] is not ("expose" or "silent")) continue;
            bool expose = parts[0] == "expose";

            // `silent transitive <Principal>` — silence the whole transitive tree by default, but keep the
            // named bundle/app (the front door) discoverable. One line = principal visible + rest silent.
            if (parts.Length == 3 && parts[0] == "silent" && parts[1] == "transitive")
            {
                rootExpose = false;
                rules[parts[2].TrimStart('*')] = true;   // expose the principal
                continue;
            }
            if (parts.Length != 2) continue;

            // Accept a leading `*` on paths (`expose *MegaApp.Physics`); `all`/`transitive` (no target)
            // set the root default (`silent transitive` = imported/transitive bundles are silent).
            string target = parts[1].TrimStart('*');
            if (target is "all" or "transitive") rootExpose = expose;
            else rules[target] = expose;
        }
        return new DiscoveryPolicy(rootExpose, rules);
    }

    /// Is this identity offered by `*` discovery? Most-specific path wins; else the root default.
    public bool IsDiscoverable(string author, string? bundle = null, string? publicator = null)
    {
        // author.bundle.pub → author.bundle → author
        if (bundle is not null && publicator is not null && _rules.TryGetValue($"{author}.{bundle}.{publicator}", out var p3)) return p3;
        if (bundle is not null && _rules.TryGetValue($"{author}.{bundle}", out var p2)) return p2;
        if (_rules.TryGetValue(author, out var p1)) return p1;
        return _rootExpose;
    }

    public IEnumerable<QualifiedSymbol> Filter(IEnumerable<QualifiedSymbol> symbols) =>
        symbols.Where(s => IsDiscoverable(s.Author, s.Bundle, s.Publicator));

    private static string? Locate(string? startDir)
    {
        foreach (var seed in new[] { startDir, Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            if (string.IsNullOrEmpty(seed)) continue;
            for (var d = new DirectoryInfo(Path.GetFullPath(seed)); d is not null; d = d.Parent)
            {
                string candidate = Path.Combine(d.FullName, "vein.discovery");
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
