using Vein.Compiler.Project;
using Xunit;

namespace Vein.Tests;

// What leaves your machine when you publish. Every assertion here is about a way that could go wrong
// silently: a fragment left behind so the bundle does not compile when someone restores it, build
// output shipped as source, a path that only works on the platform it was made on, or two publishes of
// an unchanged project disagreeing about what changed.
public class ProjectPackageTests
{
    private static string Temp() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "vein-pkg-" + Guid.NewGuid().ToString("N"))).FullName;

    private static ProjectPackage Scaffold(string root, ProjectKind kind, string name = "Demo")
    {
        ProjectScaffold.New(kind, root, name, "you");
        return ProjectPackage.Create(Path.Combine(root, name));
    }

    [Theory]
    [InlineData(ProjectKind.Scratch)]
    [InlineData(ProjectKind.Bundle)]
    [InlineData(ProjectKind.Solution)]
    public void Every_kind_packages_its_entry_and_is_recognised(ProjectKind kind)
    {
        string root = Temp();
        try
        {
            var pkg = Scaffold(root, kind);

            Assert.Equal(kind, pkg.Kind);
            Assert.NotEmpty(pkg.EntryPath);
            Assert.Contains(pkg.Files, f => f.Path == pkg.EntryPath);
            Assert.Empty(pkg.Skipped);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_bundles_fragments_travel_with_it()
    {
        // THE failure this guards. `publicators/` and `shards/` are structural — BundleLoader merges
        // them INTO the bundle — so a package that dropped them would restore as source that does not
        // compile, and the person who restored it would have no way to know what was missing.
        string root = Temp();
        try
        {
            var pkg = Scaffold(root, ProjectKind.Bundle);

            Assert.Contains(pkg.Files, f => f.Path.StartsWith("publicators/", StringComparison.Ordinal));
            Assert.Contains(pkg.Files, f => f.Path.StartsWith("shards/", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Build_output_is_not_source()
    {
        string root = Temp();
        try
        {
            ProjectScaffold.New(ProjectKind.Bundle, root, "Demo", "you");
            string dir = Path.Combine(root, "Demo");

            // Every excluded folder, each holding something that WOULD have matched by name.
            foreach (string skip in new[] { "bin", "obj", "out", ".git" })
            {
                Directory.CreateDirectory(Path.Combine(dir, skip));
                File.WriteAllText(Path.Combine(dir, skip, "leftover.vein"), "bundle Junk by you { }");
            }

            var pkg = ProjectPackage.Create(dir);

            Assert.DoesNotContain(pkg.Files, f => f.Path.Contains("leftover"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Packaging_the_same_project_twice_is_identical()
    {
        // What makes "what changed since v1?" answerable at all. If the order or the hashing moved
        // between runs, every re-publish would report every file as changed and the answer would be
        // worthless.
        string root = Temp();
        try
        {
            ProjectScaffold.New(ProjectKind.Bundle, root, "Demo", "you");
            string dir = Path.Combine(root, "Demo");

            var a = ProjectPackage.Create(dir);
            var b = ProjectPackage.Create(dir);

            Assert.Equal(a.Files.Select(f => f.Path), b.Files.Select(f => f.Path));
            Assert.Equal(a.Files.Select(f => f.Sha256), b.Files.Select(f => f.Sha256));
            Assert.Equal(a.TotalBytes, b.TotalBytes);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Paths_are_relative_and_use_forward_slashes()
    {
        // These paths are used to write files back onto someone else's disk. A backslash in one is a
        // path that only works where it was made, and an absolute one is a path that writes wherever
        // it likes.
        string root = Temp();
        try
        {
            var pkg = Scaffold(root, ProjectKind.Bundle);

            Assert.All(pkg.Files, f =>
            {
                Assert.DoesNotContain('\\', f.Path);
                Assert.False(Path.IsPathRooted(f.Path), f.Path);
                Assert.DoesNotContain("..", f.Path);
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void The_hash_is_of_the_bytes_that_will_be_stored()
    {
        // Saved with a BOM, read back, and it has to package as the same bytes as the same text saved
        // plainly — otherwise "unchanged" depends on which editor last touched the file.
        string root = Temp();
        try
        {
            string plain = Path.Combine(root, "plain");
            string bom = Path.Combine(root, "bom");
            Directory.CreateDirectory(plain);
            Directory.CreateDirectory(bom);

            const string source = "bundle Same by you { }\n";
            File.WriteAllText(Path.Combine(plain, "a.vein"), source, new System.Text.UTF8Encoding(false));
            File.WriteAllText(Path.Combine(bom, "a.vein"), source, new System.Text.UTF8Encoding(true));

            var one = ProjectPackage.Create(plain).Files.Single();
            var two = ProjectPackage.Create(bom).Files.Single();

            Assert.Equal(one.Sha256, two.Sha256);
            Assert.Equal(one.Bytes, two.Bytes);
            Assert.Matches("^[0-9a-f]{64}$", one.Sha256);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void The_name_and_authors_come_from_the_source_not_the_folder()
    {
        // `bundle Combat by alice` is the only place either fact is written down, so a folder called
        // something else still publishes as Combat by alice.
        string root = Temp();
        try
        {
            string dir = Path.Combine(root, "combat-experiments");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "combat-experiments.vein"), "bundle Combat by alice { }\n");

            var pkg = ProjectPackage.Create(dir);

            Assert.Equal("Combat", pkg.Name);
            Assert.Equal(new[] { "alice" }, pkg.Authors);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Authors_is_every_by_in_the_package_not_just_the_entrys()
    {
        // The check that matters before publishing: a package is yours only when every bundle in it is.
        // A single `Author` field would report "alice" and hide the second one entirely.
        string root = Temp();
        try
        {
            string dir = Path.Combine(root, "mixed");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "mixed.vein"), "bundle Mixed by alice { }\n");
            File.WriteAllText(Path.Combine(dir, "other.vein"), "bundle Other by bob { }\n");

            var pkg = ProjectPackage.Create(dir);

            Assert.Equal(new[] { "alice", "bob" }, pkg.Authors);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_bundle_with_no_by_is_local_rather_than_missing()
    {
        // `local` is what BundleDecl.Author defaults to. Reporting it as such is what lets the publish
        // dialog say "this says `bundle X` with no author — publish as `by <you>`?" instead of showing
        // a blank.
        string root = Temp();
        try
        {
            string dir = Path.Combine(root, "anon");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "anon.vein"), "bundle Anon { }\n");

            Assert.Equal(new[] { "local" }, ProjectPackage.Create(dir).Authors);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void An_oversized_file_is_reported_rather_than_dropped()
    {
        // "17 files" and "17 files, one of which was silently dropped" are different packages, and only
        // one of them compiles when someone restores it.
        string root = Temp();
        try
        {
            string dir = Path.Combine(root, "big");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "big.vein"), "bundle Big by you { }\n");
            File.WriteAllText(Path.Combine(dir, "data.txt"), new string('x', ProjectPackage.MaxFileBytes + 1));

            var pkg = ProjectPackage.Create(dir);

            Assert.DoesNotContain(pkg.Files, f => f.Path == "data.txt");
            Assert.Contains(pkg.Skipped, s => s.Path == "data.txt" && s.Reason.Contains("larger than"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_veinproj_principal_decides_the_entry()
    {
        // That file exists precisely to name the entry when a folder has several candidates, so the
        // package must not go and guess a different one.
        string root = Temp();
        try
        {
            string dir = Path.Combine(root, "several");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "aaa.vein"), "bundle Aaa by you { }\n");
            File.WriteAllText(Path.Combine(dir, "zzz.vein"), "bundle Zzz by you { }\n");
            new VeinProject { Principal = "zzz.vein" }.Save(dir);

            var pkg = ProjectPackage.Create(dir);

            Assert.Equal("zzz.vein", pkg.EntryPath);
            Assert.Equal("Zzz", pkg.Name);

            // And the project file itself travels: it names the entry for whoever restores this.
            Assert.Contains(pkg.Files, f => f.Path == VeinProject.FileName);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
