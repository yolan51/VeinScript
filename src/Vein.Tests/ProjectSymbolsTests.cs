using Vein.Compiler.Diagnostics;
using Vein.Compiler.Project;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// Which file's symbol table describes "this project" — the question the Symbols tab has to answer
// before it can show anything.
//
// IT IS THE MANIFEST, because an app is the only thing that links: `ProjectLoader` follows its `load`
// lines and everything those bundles `need`, transitively. Starting from the open file would answer a
// narrower question while looking like the wide one.
public class ProjectSymbolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-projsym-" + Guid.NewGuid().ToString("N"));

    public ProjectSymbolsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string relative, string text = "")
    {
        string path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void Any_app_vein_counts_not_only_the_literal_name()
    {
        // THE BUG THIS EXISTS TO AVOID. `veinc new` scaffolds `<name>.app.vein`, and so is the shipped
        // sample `kitdemo.app.vein` — a check for the exact name `app.vein` matches none of them. The
        // Project Explorer's semantic tree still makes that check, which is why it never fires for
        // samples/kitdemo.
        string app = Write("kitdemo.app.vein", "app KitDemo { }");

        Assert.Equal(app, ProjectSymbols.Source(_dir, null));
    }

    [Fact]
    public void The_manifest_wins_over_the_open_file()
    {
        string app = Write("game.app.vein", "app G { }");
        string open = Write("other.vein", "bundle B by you { }");

        Assert.Equal(app, ProjectSymbols.Source(_dir, open));
    }

    [Fact]
    public void With_no_manifest_the_open_file_answers()
    {
        // A bundle asked about directly still reports its own API and everything it needs, which is a
        // narrower question honestly answered rather than an empty panel.
        string open = Write("Solo.vein", "bundle Solo by you { }");

        Assert.Equal(open, ProjectSymbols.Source(_dir, open));
    }

    [Fact]
    public void A_manifest_in_a_subfolder_is_not_this_project_s()
    {
        // A kit under bundles/ may ship its own demo manifest. That describes the kit's demo, not the
        // project you have open.
        Write(Path.Combine("bundles", "kit.app.vein"), "app Kit { }");

        Assert.Null(ProjectSymbols.Source(_dir, null));
    }

    [Fact]
    public void The_one_named_after_its_folder_wins()
    {
        // Several manifests is a project with several programs. Folder-named first, then alphabetical
        // — so the answer is at least the SAME one every time, rather than whatever the filesystem
        // happened to return first.
        string mine = Write(Path.GetFileName(_dir) + ".app.vein", "app Mine { }");
        Write("aaa.app.vein", "app Other { }");

        Assert.Equal(mine, ProjectSymbols.Source(_dir, null));
    }

    [Fact]
    public void Otherwise_the_first_alphabetically_and_always_the_same_one()
    {
        Write("zeta.app.vein", "app Z { }");
        string first = Write("alpha.app.vein", "app A { }");

        Assert.Equal(first, ProjectSymbols.Source(_dir, null));
        Assert.Equal(first, ProjectSymbols.Source(_dir, null));
    }

    [Fact]
    public void Nothing_to_go_on_is_null_rather_than_a_throw()
    {
        Assert.Null(ProjectSymbols.Source(null, null));
        Assert.Null(ProjectSymbols.Source(_dir, null));
        Assert.Null(ProjectSymbols.Source(_dir, "notes.txt"));      // not a .vein
        Assert.Null(ProjectSymbols.Source(Path.Combine(_dir, "gone"), null));
    }

    [Fact]
    public void The_shipped_kit_sample_loads_and_names_its_bundles()
    {
        // End to end on a real project: the panel's whole content, without the window. If this stops
        // producing kit.Movement, the tab has stopped showing what an installed kit contributes.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples"))) dir = dir.Parent;
        Assert.NotNull(dir);

        string kitdemo = Path.Combine(dir!.FullName, "samples", "kitdemo");
        string? source = ProjectSymbols.Source(kitdemo, null);

        Assert.Equal("kitdemo.app.vein", Path.GetFileName(source));

        var model = ProjectLoader.Load(source!, new DiagnosticBag());
        var mover = Assert.Single(model.Symbols, s => s.Name == "Mover");

        Assert.Equal("kit", mover.Author);
        Assert.Equal("Movement", mover.Bundle);
        Assert.Equal("Drive", mover.Publicator);
        Assert.Equal("*kit.Movement.Drive.$Mover", mover.QualifiedName);

        // And it can be opened, which is what makes the rows worth double-clicking.
        Assert.NotNull(mover.At);
        Assert.True(File.Exists(mover.At!.File));
    }
}
