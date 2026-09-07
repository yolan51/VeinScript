using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Vein.Compiler.Backends;
using Vein.Compiler.Ir;

namespace Vein.Jit;

/// A compiled program, or why it could not be built.
///
/// `Source` is kept on both outcomes on purpose. When the generated C# does not compile, the errors
/// name lines in a file nobody wrote and cannot open — so the file has to come back with them, or the
/// message is unactionable.
public sealed record JitResult(
    bool Success,
    Assembly? Assembly,
    IReadOnlyList<string> Errors,
    string Source)
{
    public string Report() => Errors.Count == 0 ? "ok" : string.Join("\n", Errors);
}

/// Compiling a lowered VeinScript module to a real assembly, in memory.
///
/// WHY THIS EXISTS. `veinc build` emits `.cs` files and shells out to `dotnet build` — correct for
/// producing a redistributable, and useless for a Play button: seconds of latency and a folder of
/// artefacts for something a person expects to be instant. Nothing in the toolchain referenced Roslyn,
/// so a host that wanted the compiled path had no way to reach it. The interpreter is the honest
/// alternative and it is 12–19× slower (`tools/check-perf.sh`), which is the difference between a toy
/// scene and a real one.
///
/// WHAT IT IS NOT. It does not re-implement the backend. `CSharpBackend` produces the source, exactly
/// as `veinc emit` does, and this compiles that string — so there is one definition of what generated
/// C# looks like, and `tools/check-backend.sh` still guards it. A second emitter would be the drift
/// that check exists to catch.
public static class VeinBuild
{
    /// Compile `module` to an in-memory assembly.
    ///
    /// `Release` by default because this exists for speed; a host debugging generated code can ask for
    /// Debug and get line-accurate stacks.
    public static JitResult Compile(IrModule module, bool optimize = true, string? assemblyName = null)
    {
        var emitted = new CSharpBackend().Emit(module);
        if (!emitted.Success || emitted.Files.Count == 0)
            return new JitResult(false, null, new[] { "the backend produced no file for this module" }, "");

        string source = emitted.Files[0].Contents;
        return CompileSource(source, optimize, assemblyName ?? "vein_" + Sanitize(module.Name));
    }

    /// The same, from C# source already in hand — what the tests use, and what a host would use to
    /// compile a file `veinc emit` wrote earlier.
    public static JitResult CompileSource(string source, bool optimize = true, string assemblyName = "vein_gen")
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));

        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { tree },
            References(),
            new CSharpCompilationOptions(
                OutputKind.ConsoleApplication,
                optimizationLevel: optimize ? OptimizationLevel.Release : OptimizationLevel.Debug,
                // The generated file is machine-written and does not annotate nullability; treating its
                // warnings as anything would report a style opinion about code no person maintains.
                nullableContextOptions: NullableContextOptions.Disable));

        using var peStream = new MemoryStream();
        var result = compilation.Emit(peStream);

        if (!result.Success)
        {
            var errors = result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToList();

            return new JitResult(false, null, errors, source);
        }

        peStream.Seek(0, SeekOrigin.Begin);
        return new JitResult(true, Assembly.Load(peStream.ToArray()), Array.Empty<string>(), source);
    }

    /// Run a compiled program's entry point for `ticks` frames.
    ///
    /// The generated file carries its own `Program.Main(string[])` that reads `--ticks`, so this passes
    /// the flag rather than reaching past it into the world — the same entry point `dotnet run` would
    /// use, which is what keeps a JIT run and a built run the same program.
    public static void Run(Assembly assembly, int ticks)
    {
        var main = assembly.EntryPoint
            ?? throw new InvalidOperationException("the compiled assembly has no entry point");

        // A Main declared `void Main(string[])` takes the args; one declared `void Main()` takes none.
        object?[] args = main.GetParameters().Length == 0
            ? Array.Empty<object?>()
            : new object?[] { new[] { "--ticks", ticks.ToString() } };

        main.Invoke(null, args);
    }

    /// Everything the generated code needs to link against.
    ///
    /// The framework set comes from TRUSTED_PLATFORM_ASSEMBLIES — the reference assemblies this process
    /// is already running on — rather than from a hard-coded list, because a list would be a second
    /// statement of which .NET this targets and would rot the first time the target moved.
    ///
    /// On top of it, the assemblies the emitted code actually names: the SECS runtime that `VeinWorld`
    /// lives in and the contracts it implements. Loading them by `typeof` rather than by filename means
    /// they are found wherever the host put them, including inside a single-file publish.
    private static IReadOnlyList<MetadataReference> References()
    {
        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
            foreach (string path in tpa.Split(Path.PathSeparator))
                if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && seen.Add(path) && File.Exists(path))
                    refs.Add(MetadataReference.CreateFromFile(path));

        foreach (var type in new[] { typeof(Vein.Runtime.SECS.VeinWorld), typeof(object) })
            if (type.Assembly.Location is { Length: > 0 } loc && seen.Add(loc))
                refs.Add(MetadataReference.CreateFromFile(loc));

        // The contracts SECS implements — reached through the runtime rather than named, so this file
        // does not have to know the assembly's name.
        foreach (var referenced in typeof(Vein.Runtime.SECS.VeinWorld).Assembly.GetReferencedAssemblies())
        {
            try
            {
                string? loc = Assembly.Load(referenced).Location;
                if (loc is { Length: > 0 } && seen.Add(loc)) refs.Add(MetadataReference.CreateFromFile(loc));
            }
            catch (Exception) { /* a framework facade with no file — the TPA set already covers those */ }
        }

        return refs;
    }

    /// A bundle name as a legal assembly name. Bundle names are identifiers already, so this only has
    /// to survive the qualified ones.
    private static string Sanitize(string name)
    {
        var chars = name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        return chars.Length == 0 ? "module" : new string(chars);
    }
}
