using Vein.Compiler.Project;

/// `veinc add | remove | restore | packages` — VeinScript library PACKAGES (Vein.Bundles.Web, …) for a project
/// that has no build of its own: a website, a tool, anything the Workbench opens. A Godot game references them
/// in its csproj instead. The packages come from the local feed every `tools/pack.sh` fills.
///
///   veinc add Vein.Bundles.Web [--version 0.1.0-*] [--source <feed>] [--project <dir>]
///   veinc remove Vein.Bundles.Web [--project <dir>]
///   veinc restore [--source <feed>] [--project <dir>]     (a fresh clone, or after re-packing)
///   veinc packages [--source <feed>]                      (what the feed offers)
///
/// The project is the nearest folder holding `.veinproj`, walking up from `--project` or the current folder.
/// `add` records the package there (`Packages`), unpacks it, and lists it in `.vein/libraries` — where veinc
/// and the Workbench look — so the next line can be `need "web.Page"`.
static class PackageCommand
{
    public static int Run(string[] args)
    {
        string verb = args[0];
        string? Option(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        string? positional = null;   // the package id: the first argument that is not an option or its value
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal)) { i++; continue; }
            positional ??= args[i];
        }
        string? source = Option("--source");
        string project = LibraryPackage.ProjectDir(Option("--project") ?? Directory.GetCurrentDirectory());
        var feeds = LibraryPackage.Feeds(source);

        switch (verb)
        {
            case "packages":
            {
                var available = LibraryPackage.Available(feeds);
                if (available.Count == 0) { Console.WriteLine($"no VeinScript library packages in {string.Join(", ", feeds)}"); return 0; }
                foreach (var (id, version) in available) Console.WriteLine($"{id,-28} {version}");
                return 0;
            }
            case "add":
            {
                if (positional is null) { Console.Error.WriteLine("usage: veinc add <PackageId> [--version 0.1.0-*] [--source <feed>] [--project <dir>]"); return 2; }
                string pattern = Option("--version") ?? "0.1.0-*";
                if (LibraryPackage.Find(positional, pattern, feeds) is null)
                {
                    Console.Error.WriteLine($"{positional} {pattern} is not in {string.Join(", ", feeds)}. `veinc packages` lists what is.");
                    return 1;
                }
                LibraryPackage.SetPackage(project, positional, pattern);
                return Report(LibraryPackage.Restore(project, source), project, added: positional);
            }
            case "remove":
            {
                if (positional is null) { Console.Error.WriteLine("usage: veinc remove <PackageId> [--project <dir>]"); return 2; }
                if (!LibraryPackage.Packages(project).Any(p => string.Equals(p.Id, positional, StringComparison.OrdinalIgnoreCase)))
                {
                    Console.Error.WriteLine($"{positional} is not one of this project's packages ({Path.Combine(project, VeinProject.FileName)}).");
                    return 1;
                }
                LibraryPackage.SetPackage(project, positional, null);
                return Report(LibraryPackage.Restore(project, source), project, added: null);
            }
            case "restore":
                return Report(LibraryPackage.Restore(project, source), project, added: null);
            default:
                return 2;
        }
    }

    private static int Report(IReadOnlyList<LibraryPackage.Restored> restored, string project, string? added)
    {
        foreach (var r in restored)
        {
            if (r.Problem is not null) { Console.Error.WriteLine($"{r.Id}: {r.Problem}"); continue; }
            var bundles = r.Library!.Files.Where(f => f.Count(c => c == '/') == 1)
                .Select(f => f.Split('/')[0]).Distinct().OrderBy(b => b, StringComparer.Ordinal);
            Console.WriteLine($"{(r.Id == added ? "added" : "ok")}  {r.Id} {r.Version} — {string.Join(", ", bundles)}");
        }
        if (restored.Count == 0) Console.WriteLine("no packages in " + Path.Combine(project, VeinProject.FileName));
        Console.WriteLine($"libraries listed in {Path.Combine(project, BundleIndex.LibrariesFile)}");
        return restored.Any(r => r.Problem is not null) ? 1 : 0;
    }
}
