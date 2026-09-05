using System.Xml.Linq;
using Vein.Compiler.Project;
using Xunit;

namespace Vein.Tests;

// WHAT HAS TO BE IN THE BOX for the Workbench to work on a machine that has never seen this repository.
//
// The failure these guard against is silent and total: published without `stdlib/`, every program a
// person writes fails to resolve `*Vein.Console.Io.@Print` and nothing compiles — while the build, the
// tests and the developer's own copy all stay perfectly green, because a developer's copy finds the
// real `stdlib/` by walking up out of `bin/`.
public class ShippedLayoutTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with stdlib/ not found");
    }

    private static XDocument Workbench() =>
        XDocument.Load(Path.Combine(RepoRoot(), "src", "Vein.Workbench", "Vein.Workbench.csproj"));

    [Fact]
    public void The_standard_library_is_copied_beside_the_application()
    {
        // `BundleIndex.LocateNamed` starts at AppContext.BaseDirectory and looks for a folder named
        // `stdlib` holding .vein files. Copying it to the output is the whole mechanism — there is no
        // special case anywhere in the compiler for "installed".
        var content = Workbench().Descendants()
            .Where(e => e.Name.LocalName == "Content")
            .FirstOrDefault(e => (string?)e.Attribute("Include") is { } i && i.Contains("stdlib"));

        Assert.True(content is not null, "Vein.Workbench.csproj must ship stdlib/*.vein");
        Assert.Equal("stdlib", (string?)content!.Attribute("LinkBase"));
        Assert.Equal("PreserveNewest", (string?)content.Attribute("CopyToOutputDirectory"));
    }

    [Fact]
    public void The_cli_ships_with_the_workbench()
    {
        // ▶ and the terminal shell out to `veinc`. Without it they fall back to building the CLI from
        // source, which needs the SDK and the source tree — a machine that installed a .exe has
        // neither, so the run button simply does nothing anyone can diagnose.
        Assert.Contains(Workbench().Descendants().Where(e => e.Name.LocalName == "ProjectReference"),
            e => (string?)e.Attribute("Include") is { } i && i.Contains("Vein.Cli"));
    }

    [Fact]
    public void The_stdlib_lands_in_this_test_projects_own_output_shape()
    {
        // A cheap proxy for the published layout: the same Content rule, exercised. If `stdlib/` is
        // beside a built binary, LocateNamed finds it from AppContext.BaseDirectory without walking up
        // at all — which is what an installed copy relies on.
        string shipped = Path.Combine(RepoRoot(), "src", "Vein.Workbench", "bin", "Debug", "net8.0", "stdlib");

        if (!Directory.Exists(shipped)) return;   // the Workbench has not been built in this configuration

        Assert.NotEmpty(Directory.EnumerateFiles(shipped, "*.vein"));
        Assert.Contains(Directory.EnumerateFiles(shipped, "*.vein").Select(Path.GetFileName), n => n == "Console.vein");
    }

    [Fact]
    public void Locate_finds_a_stdlib_that_sits_directly_in_the_search_directory()
    {
        // The exact shape an install has: stdlib/ as an immediate child, nothing above it. If this
        // stopped being true, shipping the folder would no longer be enough.
        string root = Path.Combine(Path.GetTempPath(), "vein-ship-" + Guid.NewGuid().ToString("N"));
        string stdlib = Path.Combine(root, "stdlib");
        Directory.CreateDirectory(stdlib);

        try
        {
            File.WriteAllText(Path.Combine(stdlib, "Console.vein"), "bundle Console by Vein { }\n");

            Assert.Equal(stdlib, BundleIndex.LocateNamed(root, "stdlib"));
        }
        finally
        {
            BundleIndex.Invalidate(stdlib);
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void The_checks_menu_is_named_so_it_can_be_hidden_off_the_repository()
    {
        // Its four items run `dotnet test src/Vein.Tests` and two bash scripts under tools/. On an
        // installed copy none of that exists, so the submenu is hidden — which needs the name.
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Vein.Workbench", "MainWindow.axaml"));

        Assert.Contains("Name=\"ChecksMenu\"", xaml);
    }
}
