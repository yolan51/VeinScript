using Vein.Compiler.Project;

// `veinc new bundle <Name>` / `veinc new app <Name>` — scaffold a new VeinScript project on disk with a
// standard folder skeleton (see ProjectScaffold). Handled before the file-reading path in Program.cs
// because its second arg is `bundle|app`, not a `.vein` file.
internal static class NewCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: veinc new <bundle|app|scratch> <Name> [--author <A>] [-o <parentDir>] [--workload <cli|chat|site|site-bundles>]");
            return 2;
        }

        string kind = args[1];
        string name = args[2];
        string author = "you";
        string parentDir = Directory.GetCurrentDirectory();
        string? workload = null;   // a starting program (WorkloadTemplates): cli, chat, site, site-bundles
        for (int i = 3; i < args.Length; i++)
        {
            if (args[i] == "--author" && i + 1 < args.Length) author = args[++i];
            else if (args[i] == "--workload" && i + 1 < args.Length) workload = args[++i];
            else if (args[i] == "-o" && i + 1 < args.Length) parentDir = args[++i];
        }

        if (workload is not null && WorkloadTemplates.All.All(t => t.Key != workload))
        {
            Console.Error.WriteLine($"unknown workload '{workload}' (expected {string.Join(", ", WorkloadTemplates.All.Select(t => t.Key))}).");
            return 2;
        }

        try
        {
            switch (kind)
            {
                case "bundle":
                {
                    var (dir, main) = ProjectScaffold.NewBundle(parentDir, name, author, workload: workload);
                    Console.Error.WriteLine($"created bundle {name} at {dir}");
                    Console.Error.WriteLine($"  main: {main}");
                    Console.Error.WriteLine($"  folders: {string.Join(", ", ProjectScaffold.BundleFolders)}");
                    return 0;
                }
                case "app":
                {
                    var (dir, app) = ProjectScaffold.NewApp(parentDir, name, author, workload);
                    Console.Error.WriteLine($"created app {name} at {dir}");
                    Console.Error.WriteLine($"  manifest: {app}");
                    Console.Error.WriteLine($"  principal bundle: {Path.Combine(dir, name)}");
                    return 0;
                }
                case "scratch":
                {
                    // One folder, one file — the Workbench's "Scratch" structure.
                    var (dir, main) = ProjectScaffold.NewScratch(parentDir, name, author, workload);
                    Console.Error.WriteLine($"created {name} at {dir}");
                    Console.Error.WriteLine($"  main: {main}");
                    if (LibraryPackage.Packages(dir) is { Count: > 0 } packages)
                        Console.Error.WriteLine($"  packages: {string.Join(", ", packages.Select(p => p.Id))} (veinc restore if the feed lacked them)");
                    return 0;
                }
                default:
                    Console.Error.WriteLine($"unknown kind '{kind}' (expected 'bundle', 'app' or 'scratch').");
                    return 2;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            Console.Error.WriteLine($"new error: {ex.Message}");
            return 1;
        }
    }
}
