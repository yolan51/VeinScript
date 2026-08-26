using System.Diagnostics;
using System.Runtime.InteropServices;

// `veinc build <file>` — compile a VeinScript program to a standalone console executable. The exe embeds
// the source and runs the interpreter (Interp.Run) at startup, so `@Print`/`@Input` bridge to its own
// console window. It publishes a throwaway net8 console project (self-contained single-file by default).
internal static class BuildCommand
{
    public static int Build(string sourcePath, string source, string? outPath, string? rid, bool selfContained)
    {
        string name = Sanitize(Path.GetFileNameWithoutExtension(sourcePath));
        string exeExt = OperatingSystem.IsWindows() ? ".exe" : "";
        rid ??= RuntimeInformation.RuntimeIdentifier;

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
            File.WriteAllText(Path.Combine(temp, "program.vein"), source);
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
            <EmbeddedResource Include="program.vein" LogicalName="program.vein" />
          </ItemGroup>
        </Project>
        """;

    // The generated host: read the embedded .vein, compile it, run the console interpreter.
    private const string HostProgram = """
        using Vein.Compiler.Diagnostics;
        using Vein.Compiler.Ir;
        using Vein.Compiler.Lexing;
        using Vein.Compiler.Parsing;

        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream("program.vein")!;
        string src = new StreamReader(stream).ReadToEnd();

        var diag = new DiagnosticBag();
        var unit = new Parser(new Lexer(src, "program.vein", diag).Tokenize(), diag).ParseUnit();
        if (diag.HasErrors) { foreach (var d in diag.Items) Console.Error.WriteLine(d); return 1; }

        var lower = new Lower(diag);
        foreach (var b in unit.Bundles) new Interp().Run(lower.LowerBundle(b), Console.In, Console.Out);
        return 0;
        """;
}
