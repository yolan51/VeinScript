using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;
using Vein.Compiler.Project;

namespace Vein.Compiler.Tooling;

/// Every SETTING a program has (D21): the shard vars under `tune("…")` in its own bundles AND in every
/// bundle it needs — a library's included, which is the point: its source is a read-only copy, and
/// `vein.settings` is the only place its values can change. Each with its default (the source literal),
/// the project's value when `vein.settings` has one, its doc and where it is declared.
///
/// What `veinc settings` prints, `VeinGame.Settings` serves a host, and an editor's Settings panel lists.
/// Read from the AST, not the IR: the IR keeps neither spans nor the literal's text.
public static class SettingsIndex
{
    public sealed record Setting(
        string Section, string Shard, string Var, string Type, string Default, string? Value,
        string Doc, string File, int Line, bool IsLibrary)
    {
        /// `Shard.var` — the name under its `[Author.Bundle]` section in vein.settings.
        public string Name => Shard + "." + Var;
        /// `Author.Bundle.Shard.var`.
        public string Key => Section + "." + Name;
        /// What the program runs with: the project's value, else the default.
        public string Current => Value ?? Default;
    }

    /// The settings, the project's file, and what is wrong in that file: an unknown key (VS0357), a value
    /// that does not fit its setting or a line that is not `name = value` under a section (VS0358).
    public sealed record Result(IReadOnlyList<Setting> Settings, SettingsFile File, IReadOnlyList<Diagnostic> Problems);

    public static Result For(string programPath)
    {
        string full = Path.GetFullPath(programPath);
        string dir = Path.GetDirectoryName(full)!;
        var file = SettingsFile.Load(dir);
        var index = BundleIndex.For(dir);
        var settings = new List<Setting>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        var linked = new HashSet<string>(StringComparer.Ordinal);   // every `Author.Bundle` this program links

        // The program: a bundle, or an app manifest's loads.
        var bag = new DiagnosticBag();
        var top = SafeLoad(full, bag);
        if (top is { Apps.Count: > 0 })
            foreach (var load in top.Apps.SelectMany(a => a.Loads))
                queue.Enqueue(Path.GetFullPath(Path.Combine(dir, load.Path)));
        else queue.Enqueue(full);

        while (queue.Count > 0)
        {
            string path = queue.Dequeue();
            if (!seen.Add(path)) continue;
            var unit = SafeLoad(path, new DiagnosticBag());
            if (unit is null) continue;
            var sources = BundleLoader.SourceFiles(path);
            bool library = index.Roots.Any(r => BundleIndex.IsLibraryRoot(r) && IsUnder(path, r));
            foreach (var written in unit.Bundles)
            {
                string section = Section(written);
                linked.Add(section);
                foreach (var need in written.Members.OfType<NeedDecl>())
                    if (index.Owners.TryGetValue(need.Key, out var owner)) queue.Enqueue(Path.GetFullPath(owner));
                // A template's copy (`shard Files from *…!DocumentFiles { tune maxRecents = 10 }`, D28) has the
                // template's settings under its own name, starting where the use site says — as Lower sees it.
                var bundle = TemplateExpand.Apply(written, new DiagnosticBag(), key =>
                    index.Owners.TryGetValue(key, out var source)
                        ? SafeLoad(source, new DiagnosticBag())?.Bundles.FirstOrDefault(b => (b.Author ?? "local") + "." + b.Name == key)
                        : null);
                foreach (var (shard, at, members) in Shards(bundle))
                    foreach (var v in members.OfType<VarDecl>().Where(v => v.Tune && v.Init is not null))
                    {
                        string type = v.Type?.Name ?? Lower.SettingKind(v)?.ToString().ToLowerInvariant() ?? "";
                        string? value = file.Find(section, shard + "." + v.Name)?.Value;
                        // Declared in a library's template: shown where the program copies it.
                        var where = BundleLoader.PathOf(v.Span, sources) is { } own ? (own, v.Span.Line) : (BundleLoader.PathOf(at, sources) ?? path, at.Line);
                        settings.Add(new Setting(section, shard, v.Name, type, Spell(v.Init!), value, v.Doc ?? "",
                            where.Item1, where.Item2, library));
                    }
            }
        }

        // What vein.settings says that matches nothing, or does not fit.
        var problems = new DiagnosticBag();
        foreach (var (line, text) in file.Malformed)
            problems.Error("VS0358", $"`{text.Trim()}` in {SettingsFile.FileName} is not `Shard.var = value` under a `[Author.Bundle]` section.",
                new SourceSpan(file.Path, line, 1, text.Length));
        // ONE FILE, SEVERAL PROGRAMS. `vein.settings` belongs to the folder (or the project) and every program
        // in it reads the same one — VeinScriptBundle's tests/core holds four tests and one file, whose
        // `[core.Save] Store.folder` is for save.vein alone. Checked as though the file were this program's
        // own, every line for a bundle it does not link "named no setting of this program": inventory, loot
        // and timers each warned about a line that is right, and a warning that is wrong three times in four
        // teaches people to stop reading it. So a line is this program's to judge only when it links the
        // section's bundle; a section another program here can link (a bundle on the search path, or one a
        // program beside the file declares) is that program's, and its check reports what is wrong with it.
        // What stays loud is a section that no program can link at all — a misspelt `[Author.Bundle]`.
        HashSet<string>? beside = null;
        foreach (var entry in file.Entries)
        {
            var span = new SourceSpan(file.Path, entry.Line, 1, entry.Name.Length);
            var setting = settings.FirstOrDefault(s => s.Section == entry.Section && s.Name == entry.Name);
            if (setting is null)
            {
                if (linked.Contains(entry.Section))
                    problems.Warning("VS0357",
                        $"`{entry.Key}` in {SettingsFile.FileName} names no setting of this program — no `tune` var `{entry.Name}` in bundle {entry.Section}. " +
                        "It is ignored; remove it, or check the spelling against `veinc settings`.", span);
                else if (!Exists(entry.Section, index) && !(beside ??= BundlesBeside(file.Path)).Contains(entry.Section))
                    problems.Warning("VS0357",
                        $"`[{entry.Section}]` in {SettingsFile.FileName} names a bundle that is on no search path and that no program beside the file declares, " +
                        $"so `{entry.Name}` sets nothing anywhere. Check the section's spelling — `veinc settings` lists the bundles a program can tune.", span);
            }
            else if (SettingsFile.ParseValue(entry.Value, KindOf(setting.Type), span) is null)
                problems.Error("VS0358",
                    $"`{entry.Key} = {entry.Value}` in {SettingsFile.FileName}: '{setting.Var}' is a {setting.Type} setting, and that value is not one. " +
                    "The source's default is used until the line is fixed or removed.", span);
        }
        return new Result(settings, file, problems.Items);
    }

    /// The literal kind a setting's type name takes, for checking a value before it is written.
    public static LiteralKind? KindOf(string type) => type switch
    {
        "int" => LiteralKind.Int, "float" => LiteralKind.Float, "string" => LiteralKind.String,
        "bool" => LiteralKind.Bool, "percent" => LiteralKind.Percent, _ => null,
    };

    private static IEnumerable<(string Name, SourceSpan Span, IReadOnlyList<Node> Members)> Shards(BundleDecl bundle)
    {
        foreach (var d in bundle.Members)
            switch (d)
            {
                case ShardDecl s: yield return (s.Name, s.Span, s.Members); break;
                case ViewDecl v: yield return (v.Name, v.Span, v.Members); break;
                case BridgeDecl b: yield return (b.Name, b.Span, b.Members); break;
            }
    }

    /// A setting's literal as written: `5.0`, `-2`, `30%`, `"text"`, `true`.
    private static string Spell(Expr e) => e switch
    {
        UnaryExpr { Op: UnOp.Neg, Operand: var o } => "-" + Spell(o),
        LiteralExpr { Kind: LiteralKind.String, Value: var s } => "\"" + (s as string ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        LiteralExpr { Kind: LiteralKind.Bool, Value: var b } => b is true ? "true" : "false",
        LiteralExpr { Kind: LiteralKind.Percent, Value: var p } => Convert.ToString(p, System.Globalization.CultureInfo.InvariantCulture) + "%",
        LiteralExpr { Kind: LiteralKind.Float, Value: double d } => FloatText(d),
        LiteralExpr { Value: var v } => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        _ => "",
    };

    /// A float keeps its `.0`, so it reads back as a float.
    public static string FloatText(double d)
    {
        string t = d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        return t.Contains('.') || t.Contains('E') || t.Contains("Infinity") || t.Contains("NaN") ? t : t + ".0";
    }

    private static CompilationUnit? SafeLoad(string path, DiagnosticBag bag)
    {
        try { return File.Exists(path) ? BundleLoader.Load(path, bag) : null; }
        catch (Exception) { return null; }
    }

    private static bool IsUnder(string path, string root) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// A bundle's `vein.settings` section: `[Author.Bundle]`, or `[Bundle]` for one written without an author.
    private static string Section(BundleDecl bundle) => (bundle.Author is null ? "" : bundle.Author + ".") + bundle.Name;

    /// Whether some program could link the bundle a section names: it is on the search path. The index keys an
    /// author-less bundle `local.Name`, where its section is plain `Name`.
    private static bool Exists(string section, BundleIndex index) =>
        index.Owners.ContainsKey(section) || (!section.Contains('.') && index.Owners.ContainsKey("local." + section));

    /// Folders that hold no program of the project's own: build output, tool caches, an engine's copies.
    private static readonly HashSet<string> NotPrograms = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "out", "build", "artifacts", "node_modules", "addons", ".git", ".vs", ".vscode", ".idea", ".godot", ".vein" };

    /// The sections of the bundles the programs SHARING this settings file declare — a program's own bundle is
    /// on no search path, but a line for it is that program's. Which programs share a file is where
    /// `SettingsFile.Locate` would find it from: the folder it sits in, and everything below that folder when
    /// it is a project's (beside a `.veinproj`). Read only when a line names a section that nothing else
    /// explains, which is rare, so a project is walked at most once per check and usually never.
    private static HashSet<string> BundlesBeside(string settingsPath)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        string? dir = Path.GetDirectoryName(Path.GetFullPath(settingsPath));
        if (dir is null || !Directory.Exists(dir)) return found;
        bool project = File.Exists(Path.Combine(dir, VeinProject.FileName));

        var folders = new Stack<string>();
        folders.Push(dir);
        while (folders.Count > 0)
        {
            string folder = folders.Pop();
            try
            {
                foreach (string file in Directory.EnumerateFiles(folder, "*.vein"))
                {
                    if (BundleLoader.IsFragment(file) || file.EndsWith(".app.vein", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var bundle in SafeLoad(file, new DiagnosticBag())?.Bundles ?? new List<BundleDecl>())
                        found.Add(Section(bundle));
                }
                if (!project) continue;
                // A link is not followed: it leads somewhere that is not this project, or back into it.
                foreach (var sub in new DirectoryInfo(folder).EnumerateDirectories())
                    if (!NotPrograms.Contains(sub.Name) && !sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        folders.Push(sub.FullName);
            }
            catch (Exception) { /* an unreadable folder holds nothing this check can use */ }
        }
        return found;
    }
}
