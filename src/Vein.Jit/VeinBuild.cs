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

/// A program compiled for a host (VeinBuild.CompileForHost): its `__Host`, or the reason there is none —
/// backend notes (a behaviour the compiled code would lack) or C# errors.
public sealed record HostBuild(
    Vein.Runtime.SECS.IVeinCompiledProgram? Program,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Errors,
    string Source)
{
    public bool Compiled => Program is not null;

    /// Why it is not compiled, in one line ("" when it is) — the first note or error.
    public string Reason => Compiled ? "" : (Notes.FirstOrDefault() ?? Errors.FirstOrDefault() ?? "not compiled");
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

    /// Compile `module` for a HOST that drives it frame by frame (Vein.Hosting): the program's generated
    /// `__Host`, ready to `Create()` a world — or why it was not compiled.
    ///
    /// NOTES REFUSE IT. A note is the backend saying "this compiles, but a behaviour is missing" (`every N`
    /// not scheduled, an `audience` not enforced, a built-in left on the interpreter). `Compile` ignores
    /// them because a test wants the assembly anyway; a host must not run a game that silently does less
    /// than its source says, so it gets the notes back and runs the interpreter instead.
    ///
    /// LOADED BESIDE THE RUNTIME THE HOST ALREADY HAS. An engine (Godot) loads its game assemblies into
    /// its own load context. `Assembly.Load(bytes)` would let the generated code bind to a second copy of
    /// Vein.Runtime.SECS — and the `VeinWorld` it returns would not be the type the host holds. The
    /// program goes into a collectible context that resolves every reference from the context
    /// `VeinWorld` was loaded into, so there is one copy, and a reload can be unloaded.
    public static HostBuild CompileForHost(IrModule module, bool optimize = true)
    {
        var emitted = new CSharpBackend().Emit(module);
        if (!emitted.Success || emitted.Files.Count == 0)
            return new HostBuild(null, Array.Empty<string>(), new[] { "the backend produced no file for this module" }, "");

        string source = emitted.Files[0].Contents;
        if (emitted.Notes.Count > 0) return new HostBuild(null, emitted.Notes, Array.Empty<string>(), source);

        var compilation = CSharpCompilation.Create(
            "vein_" + Sanitize(module.Name) + "_" + Guid.NewGuid().ToString("N")[..8],   // unique per reload
            new[] { CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest)) },
            References(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: optimize ? OptimizationLevel.Release : OptimizationLevel.Debug,
                nullableContextOptions: NullableContextOptions.Disable));

        using var pe = new MemoryStream();
        var result = compilation.Emit(pe);
        if (!result.Success)
            return new HostBuild(null, Array.Empty<string>(),
                result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToList(), source);

        pe.Seek(0, SeekOrigin.Begin);
        var assembly = new ProgramLoadContext().LoadFromStream(pe);
        var hostType = assembly.GetTypes().FirstOrDefault(t => typeof(Vein.Runtime.SECS.IVeinCompiledProgram).IsAssignableFrom(t) && !t.IsAbstract);
        if (hostType is null)
            return new HostBuild(null, Array.Empty<string>(), new[] { "the compiled program has no __Host — regenerate it with this backend" }, source);
        return new HostBuild((Vein.Runtime.SECS.IVeinCompiledProgram)Activator.CreateInstance(hostType)!, Array.Empty<string>(), Array.Empty<string>(), source);
    }

    /// Where a compiled program lives: collectible, and resolving every reference — Vein.Runtime.SECS,
    /// ShardECS — from the load context the host's own `VeinWorld` came from (see CompileForHost).
    private sealed class ProgramLoadContext : System.Runtime.Loader.AssemblyLoadContext
    {
        private static System.Runtime.Loader.AssemblyLoadContext Runtime =>
            GetLoadContext(typeof(Vein.Runtime.SECS.VeinWorld).Assembly) ?? Default;

        public ProgramLoadContext() : base("vein-program", isCollectible: true) { }

        protected override Assembly? Load(AssemblyName name)
        {
            foreach (var loaded in Runtime.Assemblies)
                if (AssemblyName.ReferenceMatchesDefinition(name, loaded.GetName())) return loaded;
            try { return Runtime.LoadFromAssemblyName(name); }
            catch (Exception) { return null; }   // the framework, from the default context
        }
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
                // Through the runtime's own load context, not the default one: under an engine (Godot)
                // ShardECS is not on the default probing path, and Assembly.Load would not find it.
                var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(typeof(Vein.Runtime.SECS.VeinWorld).Assembly)
                              ?? System.Runtime.Loader.AssemblyLoadContext.Default;
                string? loc = context.LoadFromAssemblyName(referenced).Location;
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
