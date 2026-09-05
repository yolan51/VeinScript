using Vein.Compiler.Diagnostics;
using Vein.Compiler.Project;
using Xunit;

namespace Vein.Tests;

// The gate between someone's folder and a database other people will build against. It exists because
// no single call in the compiler answers "does this whole project compile" — Compile is one bundle,
// AppLinker is one app, ProjectLoader is surface only — and publishing needs all three.
//
// [Collection] because BundleSearch.Scope and the BundleIndex caches are process-global, and a
// concurrent test that scopes elsewhere would make these resolve against the wrong stdlib.
[Collection("BundleIndex")]
public class PublishGateTests
{
    private static string Temp() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "vein-gate-" + Guid.NewGuid().ToString("N"))).FullName;

    private static PublishCheck Check(string root, ProjectKind kind, string name = "Demo")
    {
        ProjectScaffold.New(kind, root, name, "you");
        return PublishGate.Check(ProjectPackage.Create(Path.Combine(root, name)));
    }

    [Theory]
    [InlineData(ProjectKind.Scratch)]
    [InlineData(ProjectKind.Bundle)]
    [InlineData(ProjectKind.Solution)]
    public void A_scaffolded_project_is_publishable(ProjectKind kind)
    {
        string root = Temp();
        try
        {
            var check = Check(root, kind);

            // Warning-free too, though only errors block. Every shipped .vein file in this repo meets
            // that bar, and the scaffold is what a person's first project is made of — if THAT opens
            // with a warning, the gate's warning count is noise from the first publish onward.
            Assert.True(check.Ok, check.Summary());
            Assert.Equal(0, check.Errors);
            Assert.Equal(0, check.Warnings);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_syntax_error_blocks_the_publish_and_says_where()
    {
        string root = Temp();
        try
        {
            ProjectScaffold.New(ProjectKind.Bundle, root, "Demo", "you");
            string dir = Path.Combine(root, "Demo");
            File.WriteAllText(Path.Combine(dir, "Demo.vein"), "bundle Demo by you { this is not veinscript");

            var check = PublishGate.Check(ProjectPackage.Create(dir));

            Assert.False(check.Ok);
            Assert.True(check.Errors > 0);
            Assert.NotEmpty(check.Summary());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_broken_fragment_blocks_it_too()
    {
        // The one a per-file check would miss. A fragment carries no `bundle` header, so it is never
        // compiled on its own — it reaches the compiler only through its bundle's main file. Without
        // SourcePath threading that through, this project would publish as "compiles fine".
        string root = Temp();
        try
        {
            ProjectScaffold.New(ProjectKind.Bundle, root, "Demo", "you");
            string dir = Path.Combine(root, "Demo");
            File.WriteAllText(Path.Combine(dir, "shards", "Broken.vein"), "shard Broken { run once { ( } }");

            var check = PublishGate.Check(ProjectPackage.Create(dir));

            Assert.False(check.Ok);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_bundle_that_is_not_the_entry_is_checked_as_well()
    {
        // A folder can hold more than one bundle. Checking only the entry would let source into the
        // corpus that nobody can build — and the person who finds out is not the one who published it.
        string root = Temp();
        try
        {
            string dir = Path.Combine(root, "several");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "several.vein"), "bundle Several by you { }\n");
            File.WriteAllText(Path.Combine(dir, "other.vein"), "bundle Other by you { shard S { run once {");

            var check = PublishGate.Check(ProjectPackage.Create(dir));

            Assert.False(check.Ok);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_warning_is_counted_and_does_not_block()
    {
        // Warning-free is the right bar for the standard library and a hostile one for a stranger's
        // first upload. The count travels with the version instead, so quality stays visible without
        // being a gate.
        string root = Temp();
        try
        {
            string dir = Path.Combine(root, "warned");
            Directory.CreateDirectory(dir);

            // VS0228: `bring` with fewer arguments than the builder has required slots. `max` is left
            // as a typed zero — compiles and runs, and is very often not what the author meant.
            File.WriteAllText(Path.Combine(dir, "warned.vein"), """
                bundle Warned by you {
                    shape $Gauge { hp: int, max: int }
                    mark #Active
                    builder Unit { $Gauge   mark #Active }

                    shard S {
                        run once { bring Unit(10) }
                    }
                }
                """);

            var check = PublishGate.Check(ProjectPackage.Create(dir));

            Assert.True(check.Ok, check.Summary());
            Assert.Equal(0, check.Errors);
            Assert.True(check.Warnings > 0, "expected the unheard event to warn");
            Assert.Contains(check.Diagnostics, d => d.Severity == Severity.Warning);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void An_empty_folder_is_refused_rather_than_passed()
    {
        // Nothing to compile is not the same as compiles. Without this, a mis-detected entry publishes
        // an empty version that reports a clean build.
        string root = Temp();
        try
        {
            string dir = Path.Combine(root, "empty");
            Directory.CreateDirectory(dir);

            var check = PublishGate.Check(ProjectPackage.Create(dir));

            Assert.False(check.Ok);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void An_imported_bundle_does_not_fail_the_project_importing_it()
    {
        // `bundles/` holds other people's code, carried along so a restored solution still links. It is
        // not this project's code to be judged on — otherwise a dependency's warning arrives as yours,
        // and a dependency's error makes your own project unpublishable.
        string root = Temp();
        try
        {
            ProjectScaffold.New(ProjectKind.Solution, root, "Demo", "you");
            string dir = Path.Combine(root, "Demo");
            string vendor = Path.Combine(dir, "bundles", "Theirs");
            Directory.CreateDirectory(vendor);
            File.WriteAllText(Path.Combine(vendor, "Theirs.vein"), "bundle Theirs by them { this will not parse");

            var check = PublishGate.Check(ProjectPackage.Create(dir));

            Assert.True(check.Ok, check.Summary());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
