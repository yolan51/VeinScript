using System.Diagnostics;
using System.Runtime.InteropServices;

// `veinc build <file>` — compile a VeinScript program to a standalone console executable. The exe embeds
// the source and runs the interpreter (Interp.Run) at startup, so `@Print`/`@Input` bridge to its own
// console window. It publishes a throwaway net8 console project (self-contained single-file by default).
internal static class BuildCommand
{
    public static int Build(string sourcePath, string source, string? outPath, string? rid, bool selfContained)
    {
        // A MANIFEST IS REFUSED HERE, LOUDLY, and that is the whole fix rather than a limitation newly
        // introduced. `build` embeds ONE source file and re-parses it inside the published exe; an app
        // manifest declares `load`s and no bundles, so the embedded program had zero bundles, ran
        // nothing, and exited 0 — the same silence `emit` used to give, and the least useful answer a
        // command can produce.
        //
        // `emit` DOES link a manifest now, because the C# backend takes a merged module and a merged
        // module is exactly what linking produces. Embedding a whole app's file set is a different job,
        // and saying so beats pretending.
        if (source.Contains("\napp ", StringComparison.Ordinal) || source.TrimStart().StartsWith("app ", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"build: '{Path.GetFileName(sourcePath)}' is an app manifest, and `build` embeds a single " +
                "bundle's source. Build the principal bundle directly, or use `veinc emit` — it links a " +
                "manifest into one module and writes the C# for the whole app.");
            return 1;
        }

        // vein.lock records what the program uses from bundles/ when it is built, as when it is run. The
        // build itself compiles inside the published exe, so this compile exists only to take that record,
        // and only from a program that compiles.
        string sourceDir = Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ".";
        var compiled = new Vein.Compiler.Service.VeinCompilerService().Compile(new Vein.Compiler.Service.CompileRequest(
            Path.GetFileName(sourcePath), source, ProjectDir: sourceDir, SourcePath: sourcePath));
        if (compiled.Success) LockCommand.Record(sourceDir, compiled.OutsideUses);

        string name = Sanitize(Path.GetFileNameWithoutExtension(sourcePath));
        rid ??= RuntimeInformation.RuntimeIdentifier;

        // The extension belongs to the TARGET, not to the machine doing the building. Taking it from the
        // host meant every cross-build looked for the wrong file: `--rid linux-x64` on Windows published
        // `server` successfully and then failed with "published exe not found: server.exe", which reads
        // like the compile broke when in fact only the lookup did. Deploying a Linux server from a
        // Windows box is the ordinary case for this command, not an exotic one.
        string exeExt = rid.StartsWith("win", StringComparison.OrdinalIgnoreCase) ? ".exe" : "";

        string compilerDll = Path.Combine(AppContext.BaseDirectory, "Vein.Compiler.dll");
        if (!File.Exists(compilerDll))
        {
            Console.Error.WriteLine($"build error: Vein.Compiler.dll not found next to the CLI ({compilerDll}).");
            return 1;
        }

        string temp = Path.Combine(Path.GetTempPath(), "veinbuild-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // THE PROGRAM IS EVERY FILE `veinc run` READS, not the one file named on the command line. The
            // exe used to embed `program.vein` alone and re-parse it, so a bundle split into `shards/` and
            // `publicators/` lost every fragment, and a `need` on something in `bundles/` resolved nothing.
            // So the exe carries the main file, its fragments (BundleLoader's own list, in its own layout)
            // and the project's `bundles/`, and loads them inside exactly as `run` does.
            string project = Path.Combine(temp, "project");
            string mainName = Path.GetFileName(sourcePath);
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, mainName), source);
            foreach (string fragment in Vein.Compiler.Project.BundleLoader.SourceFiles(sourcePath).Skip(1))
            {
                string target = Path.Combine(project, Path.GetRelativePath(sourceDir, fragment));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(fragment, target, overwrite: true);
            }
            if (ProjectBundles(sourceDir) is { } bundlesDir)
                foreach (string file in Directory.EnumerateFiles(bundlesDir, "*.vein", SearchOption.AllDirectories))
                {
                    string target = Path.Combine(project, "bundles", Path.GetRelativePath(bundlesDir, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);
                }
            // Which file is the main one, and which build this is — the exe unpacks into a folder named for
            // the build, so a fragment deleted since an earlier build of the same name cannot linger there
            // and be merged back in.
            File.WriteAllText(Path.Combine(temp, "main.txt"), mainName);
            File.WriteAllText(Path.Combine(temp, "build.txt"), Guid.NewGuid().ToString("N"));

            // THE STANDARD LIBRARY GOES INTO THE EXE, or the program inside it cannot see it.
            //
            // The built host used to construct `new Lower(diag)` with no bundle index and nothing to
            // index — the published exe had `program.vein` and nothing else. So `use Console` resolved
            // nothing, `bring Console(#Screen2, "…")` was VS0203 "Unknown builder" and lowered to a
            // NO-OP, and that error landed in `diag` after the host's only HasErrors check, which
            // discarded it. Both surrounding `@Print`s still fired, because Print is matched by name in
            // Drain regardless. So the same program spawned a second window under `veinc run` and
            // shipped as one window, with nothing said. An afternoon went into narrowing that from
            // outside; the fix is that the exe carries what it needs and says so when it does not.
            //
            // The files are copied into the build folder and embedded as resources with their relative
            // paths, so the host can put them back on disk at startup and point the index at them —
            // a single-file exe has no folder to ship beside.
            if (Vein.Compiler.Project.BundleIndex.LocateNamed(null, "stdlib") is { } stdlibDir)
            {
                string into = Path.Combine(temp, "stdlib");
                foreach (string file in Directory.EnumerateFiles(stdlibDir, "*.vein", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(stdlibDir, file);
                    string target = Path.Combine(into, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);
                }
            }
            else Console.Error.WriteLine("build warning: no stdlib folder found — the built program will not resolve *Vein.* references.");
            File.WriteAllText(Path.Combine(temp, "app.csproj"), Csproj(name, compilerDll));
            File.WriteAllText(Path.Combine(temp, "Program.cs"), HostProgram);

            string outDir = Path.Combine(temp, "out");
            Console.Error.WriteLine($"building {name}{exeExt} ({(selfContained ? "self-contained" : "framework-dependent")}, {rid}) — this can take a while…");

            var psi = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = temp
            };
            foreach (var a in new[]
            {
                "publish", Path.Combine(temp, "app.csproj"), "-c", "Release", "-r", rid,
                "--self-contained", selfContained ? "true" : "false",
                "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true", "-o", outDir
            }) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi)!;
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine("  " + e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine("  " + e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            if (p.ExitCode != 0) { Console.Error.WriteLine($"build error: `dotnet publish` failed (exit {p.ExitCode})."); return 1; }

            string produced = Path.Combine(outDir, name + exeExt);
            if (!File.Exists(produced)) { Console.Error.WriteLine($"build error: published exe not found: {produced}"); return 1; }

            string dest = outPath ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? ".", name + exeExt);
            File.Copy(produced, dest, overwrite: true);
            Console.Error.WriteLine($"built {dest}");
            return 0;
        }
        finally { try { Directory.Delete(temp, recursive: true); } catch { /* best-effort cleanup */ } }
    }

    /// The `bundles/` folder this program's `need`s resolve from: the nearest one at or above its own
    /// folder. Only the program's ancestors are searched, not the shell's current folder, which a
    /// `veinc run` also falls back to. A build is a package, and whatever folder `veinc build` happened to
    /// be typed in has no business inside it.
    private static string? ProjectBundles(string sourceDir)
    {
        for (var d = new DirectoryInfo(sourceDir); d is not null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, "bundles");
            if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*.vein", SearchOption.AllDirectories).Any())
                return candidate;
        }
        return null;
    }

    private static string Sanitize(string n) =>
        new(n.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());

    private static string Csproj(string name, string compilerDll) => $"""
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net8.0</TargetFramework>
            <Nullable>enable</Nullable>
            <ImplicitUsings>enable</ImplicitUsings>
            <AssemblyName>{name}</AssemblyName>
            <InvariantGlobalization>true</InvariantGlobalization>
          </PropertyGroup>
          <ItemGroup>
            <Reference Include="Vein.Compiler"><HintPath>{compilerDll}</HintPath></Reference>
            <EmbeddedResource Include="main.txt" LogicalName="main.txt" />
            <EmbeddedResource Include="build.txt" LogicalName="build.txt" />
            <EmbeddedResource Include="project\**\*.vein" LogicalName="project/%(RecursiveDir)%(Filename)%(Extension)" />
            <EmbeddedResource Include="stdlib\**\*.vein" LogicalName="stdlib/%(RecursiveDir)%(Filename)%(Extension)" />
          </ItemGroup>
        </Project>
        """;

    // The generated host: read the embedded .vein, compile it, run the console interpreter.
    private const string HostProgram = """
        using Vein.Compiler.Diagnostics;
        using Vein.Compiler.Ir;
        using Vein.Compiler.Project;

        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        string Text(string res) { using var s = asm.GetManifestResourceStream(res)!; return new StreamReader(s).ReadToEnd().Trim(); }

        // Put the embedded files back on disk: the standard library, and the program as `veinc run` would
        // read it — the main file, its `shards/` and `publicators/`, and its `bundles/`. A single-file exe
        // has no folder beside it, so a temp folder stands in, NAMED FOR THIS BUILD so a fragment an older
        // build had cannot linger and be merged in. BundleIndex walks UP from the project looking for
        // `stdlib` and `bundles`, so the project sits one level below the stdlib.
        string root = Path.Combine(Path.GetTempPath(), "vein-" + asm.GetName().Name + "-" + Text("build.txt"));
        foreach (string res in asm.GetManifestResourceNames())
        {
            if (!res.StartsWith("stdlib/", StringComparison.Ordinal) && !res.StartsWith("project/", StringComparison.Ordinal)) continue;
            // `%(RecursiveDir)` keeps the BUILD machine's separator, so a resource name can hold either.
            string target = Path.Combine(root, res.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var rs = asm.GetManifestResourceStream(res)!;
            using var fs = File.Create(target);
            rs.CopyTo(fs);
        }
        string projectDir = Path.Combine(root, "project");
        string main = Path.Combine(projectDir, Text("main.txt"));

        var diag = new DiagnosticBag();
        var unit = BundleLoader.Load(main, diag);
        if (diag.HasErrors) { foreach (var d in diag.Items) Console.Error.WriteLine(d); return 1; }

        // One Lower per bundle — a shared one leaked each bundle's `use` list into the next.
        var modules = new List<IrModule>();
        foreach (var b in unit.Bundles) modules.Add(new Lower(diag, projectDir).LowerBundle(b));

        // CHECKED AFTER LOWERING, which is where a missing builder, an unknown shape or a bad `use`
        // are found. The check above only covers the parse; a program that lowered `bring Console(…)`
        // to a no-op used to run past this point printing everything else and spawning nothing, with
        // the error sitting in `diag` unread.
        if (diag.HasErrors) { foreach (var d in diag.Items) Console.Error.WriteLine(d); return 1; }

        // `--ticks N` runs a fixed count and stops, matching `veinc run`; with no flag the program gets
        // a real frame loop if it declares frame work, and none if it does not. A batch tool or a
        // server pays nothing; a game runs.
        int ticks = 0;
        double fps = 60;
        // Everything that is not the exe's own `--ticks N` / `--fps N` is the PROGRAM's, for `args()`:
        // `tasks.exe add "buy milk"` hands the program ["add", "buy milk"].
        var programArgs = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--ticks" && i + 1 < args.Length) { int.TryParse(args[++i], out ticks); continue; }
            if (args[i] == "--fps" && i + 1 < args.Length)
            {
                double.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out fps);
                continue;
            }
            programArgs.Add(args[i]);
        }

        int exit = 0;
        foreach (var m in modules)
        {
            // A relative `@WriteFile` path is anchored at the EXE, not at wherever the shortcut that
            // launched it happened to start. A game is not launched from a shell, and a save that lands
            // in an unpredictable folder is silent: the write succeeds and the next run starts anew.
            string here = AppContext.BaseDirectory;
            var interp = ticks > 0 ? new Interp { Ticks = ticks, BaseDirectory = here, Args = programArgs, ErrorOutput = Console.Error }
                                   : new Interp { FrameRate = fps, BaseDirectory = here, Args = programArgs, ErrorOutput = Console.Error };
            interp.Run(m, Console.In, Console.Out, messaging: true);
            // The program's own exit code — `@Quit { code }`, or 1 after an error nobody heard — so a
            // script or a shell can tell success from failure.
            if (interp.ExitCode != 0) exit = interp.ExitCode;
            if (interp.Stopped) break;
        }
        return exit;
        """;
}
