using System.Text.Json;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Project;
using Vein.Compiler.Tooling;

/// `veinc settings` — a program's SETTINGS (D21): every shard var under `tune("…")`, its own bundles' and
/// every needed bundle's, with the value the project's `vein.settings` gives it.
///
///   veinc settings game.vein [--json]                    list them, by bundle
///   veinc settings game.vein set kit.Cameras.Fly.speed=12  write one to vein.settings (type-checked)
///   veinc settings game.vein reset kit.Cameras.Fly.speed   remove it: back to the source's default
static class SettingsCommand
{
    public static int Run(string path, string[] args)
    {
        var result = SettingsIndex.For(path);
        string verb = args.Length > 2 ? args[2] : "";

        if (verb is "set" or "reset")
        {
            if (args.Length < 4) { Console.Error.WriteLine($"usage: veinc settings <file> {verb} Author.Bundle.Shard.var{(verb == "set" ? "=value" : "")}"); return 2; }
            string arg = args[3];
            int eq = arg.IndexOf('=');
            string key = verb == "set" && eq > 0 ? arg[..eq].Trim() : arg.Trim();
            var setting = result.Settings.FirstOrDefault(s => s.Key == key);
            if (setting is null)
            {
                Console.Error.WriteLine($"no setting `{key}` in this program — `veinc settings {path}` lists them.");
                return 1;
            }
            if (verb == "reset")
            {
                bool removed = SettingsFile.Remove(result.File.Path, setting.Section, setting.Name);
                Console.WriteLine(removed ? $"{key} reset to its default, {setting.Default}" : $"{key} was already its default, {setting.Default}");
                return 0;
            }
            if (eq <= 0) { Console.Error.WriteLine("usage: veinc settings <file> set Author.Bundle.Shard.var=value"); return 2; }
            string value = arg[(eq + 1)..].Trim();
            if (SettingsFile.ParseValue(value, SettingsIndex.KindOf(setting.Type), default) is null)
            {
                Console.Error.WriteLine($"`{value}` is not a {setting.Type} — {key} is a {setting.Type} setting (its default is {setting.Default}).");
                return 1;
            }
            SettingsFile.Set(result.File.Path, setting.Section, setting.Name, value);
            Console.WriteLine($"{key} = {value}   ({result.File.Path})");
            return 0;
        }

        if (args.Contains("--json"))
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                file = result.File.Path,
                settings = result.Settings.Select(s => new
                {
                    key = s.Key, section = s.Section, shard = s.Shard, name = s.Var, type = s.Type,
                    @default = s.Default, value = s.Value, current = s.Current, doc = s.Doc,
                    file = s.File, line = s.Line, library = s.IsLibrary,
                }),
                problems = result.Problems.Select(d => d.ToString()),
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        else
        {
            if (result.Settings.Count == 0) Console.WriteLine("no settings — mark a shard var with `tune(\"what it changes\")` to make it one.");
            foreach (var section in result.Settings.GroupBy(s => s.Section))
            {
                Console.WriteLine($"[{section.Key}]{(section.First().IsLibrary ? "   (library)" : "")}");
                int width = section.Max(s => s.Name.Length + 3 + s.Current.Length);
                foreach (var s in section)
                {
                    string entry = $"{s.Name} = {s.Current}".PadRight(width + 2);
                    string changed = s.Value is null ? "" : $"   (default {s.Default})";
                    Console.WriteLine($"  {entry}{s.Doc}{changed}   {Path.GetFileName(s.File)}:{s.Line}");
                }
            }
            Console.WriteLine($"file: {result.File.Path}{(File.Exists(result.File.Path) ? "" : " (not written yet)")}");
        }
        foreach (var d in result.Problems) Console.Error.WriteLine(d);
        return result.Problems.Any(d => d.Severity == Severity.Error) ? 1 : 0;
    }

    /// `veinc check` reports what is wrong in `vein.settings` too: the problems Lower did not already raise
    /// (it reports a bad value where it applies one; only the index knows a line names nothing).
    public static void AddProblems(string path, DiagnosticBag diagnostics)
    {
        IReadOnlyList<Diagnostic> problems;
        try { problems = SettingsIndex.For(path).Problems; }
        catch (Exception) { return; }
        foreach (var d in problems)
            if (!diagnostics.Items.Any(x => x.Code == d.Code && x.Span.Line == d.Span.Line && x.Span.File == d.Span.File))
                diagnostics.Add(d);
    }
}
