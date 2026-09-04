using Vein.Compiler.Project;
using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// A generated project that does not compile teaches that the language is fiddly before it teaches
// anything else. So every combination of structure and starting point is generated for real, on disk,
// and compiled — there is no version of this feature where that is optional.
public class NewProjectTests
{
    private static string Temp() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "vein-new-" + Guid.NewGuid().ToString("N"))).FullName;

    /// Every structure paired with every starting point, including the empty one.
    public static IEnumerable<object[]> Combinations =>
        from kind in new[] { ProjectKind.Scratch, ProjectKind.Bundle, ProjectKind.Solution }
        from workload in new string?[] { null, "cli", "chat", "site" }
        select new object[] { kind, workload ?? "" };

    [Theory]
    [MemberData(nameof(Combinations))]
    public void Every_combination_compiles_with_no_diagnostics(ProjectKind kind, string workload)
    {
        string root = Temp();
        try
        {
            var (_, mainFile) = ProjectScaffold.New(kind, root, "Demo", "you", workload.Length == 0 ? null : workload);

            // SourcePath is what pulls the fragments in — without it a seeded bundle compiles as its
            // main file alone and the publicators/ folder is never read.
            var r = new VeinCompilerService().Compile(new CompileRequest(
                Path.GetFileName(mainFile), File.ReadAllText(mainFile),
                ProjectDir: Path.GetDirectoryName(mainFile), SourcePath: mainFile));

            // Warning-free, not merely error-free. Every shipped .vein file is clean, and a scaffold
            // that opens with a warning teaches that warnings are normal.
            Assert.True(r.Success && r.Diagnostics.Count == 0,
                $"{kind}/{(workload.Length == 0 ? "empty" : workload)}:\n" +
                string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void Every_entry_file_declares_how_to_run_itself(ProjectKind kind, string workload)
    {
        // The header convention RunConfig reads. A generated file whose header is wrong breaks the
        // toolbar's run button on the very first file someone makes — which is why the workload
        // templates take the entry path rather than hardcoding one.
        string root = Temp();
        try
        {
            var (_, mainFile) = ProjectScaffold.New(kind, root, "Demo", "you", workload.Length == 0 ? null : workload);
            var configs = RunConfig.From(File.ReadAllText(mainFile), mainFile);

            Assert.True(configs.Count > 0, $"{kind}/{workload}: no run line in the header");
            Assert.All(configs, c => Assert.Equal(mainFile, c.Args[0]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_scratch_project_has_no_fragment_folders()
    {
        // The point is the ABSENCE: two empty directories ask a question you do not yet have an answer
        // to. Everything fits in one file until it does not.
        string root = Temp();
        try
        {
            var (dir, _) = ProjectScaffold.New(ProjectKind.Scratch, root, "Demo", "you");

            Assert.False(Directory.Exists(Path.Combine(dir, "publicators")));
            Assert.False(Directory.Exists(Path.Combine(dir, "shards")));
            Assert.Single(Directory.GetFiles(dir));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_seeded_bundle_puts_each_primitive_where_the_loader_reads_it()
    {
        string root = Temp();
        try
        {
            var (dir, mainFile) = ProjectScaffold.New(ProjectKind.Bundle, root, "Combat", "you");

            foreach (string file in new[] { "Shapes.vein", "Marks.vein", "Events.vein", "Builders.vein" })
                Assert.True(File.Exists(Path.Combine(dir, "publicators", file)), $"missing publicators/{file}");
            Assert.True(File.Exists(Path.Combine(dir, "shards", "Boot.vein")));

            // The fragments actually MERGE — the loader read them, rather than them merely being on disk.
            var ast = new VeinCompilerService().Compile(new CompileRequest(
                Path.GetFileName(mainFile), File.ReadAllText(mainFile),
                ProjectDir: dir, SourcePath: mainFile)).Ast!;

            var index = DefinitionIndex.Analyze(ast);
            Assert.NotNull(index.Define("Gauge", Vein.Compiler.Project.SymbolKind.Shape));
            Assert.NotNull(index.Define("Active", Vein.Compiler.Project.SymbolKind.Mark));
            Assert.NotNull(index.Define("Drained", Vein.Compiler.Project.SymbolKind.Event));
            Assert.NotNull(index.Define("Unit", Vein.Compiler.Project.SymbolKind.Builder));
            Assert.NotNull(index.Define("Boot", Vein.Compiler.Project.SymbolKind.Shard));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void The_file_name_is_the_publicator_name()
    {
        // The property the layout depends on, and the reason the main file documents it: `$Gauge` in
        // Shapes.vein is reached as *you.Combat.Shapes.$Gauge, so renaming a fragment renames its
        // publicator. If this ever stopped being true the seeded layout would silently change every
        // public path in a generated bundle.
        string root = Temp();
        try
        {
            var (dir, mainFile) = ProjectScaffold.New(ProjectKind.Bundle, root, "Combat", "you");
            var ast = new VeinCompilerService().Compile(new CompileRequest(
                Path.GetFileName(mainFile), File.ReadAllText(mainFile),
                ProjectDir: dir, SourcePath: mainFile)).Ast!;

            var owner = DefinitionIndex.Analyze(ast).Define("Gauge", Vein.Compiler.Project.SymbolKind.Shape)!.Owner;
            Assert.Equal("Shapes", owner);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_workload_replaces_the_seeds_rather_than_joining_them()
    {
        // Seeding both would declare $Gauge beside the program's own shapes — a project that contradicts
        // itself on the first read. The folders stay, empty, because splitting into them is next.
        string root = Temp();
        try
        {
            var (dir, _) = ProjectScaffold.New(ProjectKind.Bundle, root, "Demo", "you", "site");

            Assert.True(Directory.Exists(Path.Combine(dir, "publicators")));
            Assert.False(File.Exists(Path.Combine(dir, "publicators", "Shapes.vein")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_solution_has_a_principal_and_somewhere_to_import_into()
    {
        // `bundles/` is a real import root (BundleIndex), not a convention — which is what makes the
        // third kind different from a bundle rather than a bundle with extra folders.
        string root = Temp();
        try
        {
            var (dir, appFile) = ProjectScaffold.New(ProjectKind.Solution, root, "MyGame", "you");

            Assert.True(File.Exists(appFile));
            Assert.True(Directory.Exists(Path.Combine(dir, "bundles")));
            Assert.True(File.Exists(Path.Combine(dir, "MyGame", "MyGame.vein")), "principal bundle");
            Assert.Contains("load \"MyGame/MyGame.vein\"", File.ReadAllText(appFile));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_seeded_bundle_actually_runs()
    {
        // It brings a Unit and hears @Drained, so the skeleton does something the moment it is made
        // rather than merely parsing.
        string root = Temp();
        try
        {
            var (dir, mainFile) = ProjectScaffold.New(ProjectKind.Bundle, root, "Combat", "you");
            var r = new VeinCompilerService().Compile(new CompileRequest(
                Path.GetFileName(mainFile), File.ReadAllText(mainFile), ProjectDir: dir, SourcePath: mainFile));

            var interp = new Vein.Compiler.Ir.Interp();
            interp.Boot(r.Modules[0]);

            Assert.Equal(1, interp.World.EntityCount);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
