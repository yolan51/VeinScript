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
            foreach (var bundle in unit.Bundles)
            {
                string section = (bundle.Author is null ? "" : bundle.Author + ".") + bundle.Name;
                foreach (var need in bundle.Members.OfType<NeedDecl>())
                    if (index.Owners.TryGetValue(need.Key, out var owner)) queue.Enqueue(Path.GetFullPath(owner));
                foreach (var (shard, members) in Shards(bundle))
                    foreach (var v in members.OfType<VarDecl>().Where(v => v.Tune && v.Init is not null))
                    {
                        string type = v.Type?.Name ?? Lower.SettingKind(v)?.ToString().ToLowerInvariant() ?? "";
                        string? value = file.Find(section, shard + "." + v.Name)?.Value;
                        settings.Add(new Setting(section, shard, v.Name, type, Spell(v.Init!), value, v.Doc ?? "",
                            BundleLoader.PathOf(v.Span, sources) ?? path, v.Span.Line, library));
                    }
            }
        }

        // What vein.settings says that matches nothing, or does not fit.
        var problems = new DiagnosticBag();
        foreach (var (line, text) in file.Malformed)
            problems.Error("VS0358", $"`{text.Trim()}` in {SettingsFile.FileName} is not `Shard.var = value` under a `[Author.Bundle]` section.",
                new SourceSpan(file.Path, line, 1, text.Length));
        foreach (var entry in file.Entries)
        {
            var span = new SourceSpan(file.Path, entry.Line, 1, entry.Name.Length);
            var setting = settings.FirstOrDefault(s => s.Section == entry.Section && s.Name == entry.Name);
            if (setting is null)
                problems.Warning("VS0357",
                    $"`{entry.Key}` in {SettingsFile.FileName} names no setting of this program — no `tune` var `{entry.Name}` in bundle {entry.Section}. " +
                    "It is ignored; remove it, or check the spelling against `veinc settings`.", span);
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

    private static IEnumerable<(string Name, IReadOnlyList<Node> Members)> Shards(BundleDecl bundle)
    {
        foreach (var d in bundle.Members)
            switch (d)
            {
                case ShardDecl s: yield return (s.Name, s.Members); break;
                case ViewDecl v: yield return (v.Name, v.Members); break;
                case BridgeDecl b: yield return (b.Name, b.Members); break;
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
}
