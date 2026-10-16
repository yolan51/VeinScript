using System.IO.Compression;
using Vein.Compiler.Project;
using Xunit;

namespace Vein.Tests;

// A project with no build of its own — a website, a tool, anything the Workbench opens — uses library
// PACKAGES (Vein.Bundles.Web, …) through `.veinproj`: `veinc add` records one, `restore` unpacks each into
// `.vein/libraries`, where veinc and the Workbench search. These run against a throwaway feed holding a real
// .nupkg — this test assembly, which embeds testkit.Hello the way the bundle packages embed theirs.
public class PackageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-packages-" + Guid.NewGuid().ToString("N"));
    private string Feed => Path.Combine(_dir, "feed");
    private string Project => Path.Combine(_dir, "site");

    public PackageTests()
    {
        Directory.CreateDirectory(Feed);
        Directory.CreateDirectory(Project);
        string dll = typeof(PackageTests).Assembly.Location;
        foreach (string version in new[] { "0.1.0-dev.20260101000000", "0.1.0-dev.20260202000000", "0.2.0" })
        {
            using var zip = ZipFile.Open(Path.Combine(Feed, $"Vein.Tests.{version}.nupkg"), ZipArchiveMode.Create);
            zip.CreateEntryFromFile(dll, "lib/net8.0/Vein.Tests.dll");
        }
        // A package in the feed that is not a VeinScript library: never offered.
        using (var other = ZipFile.Open(Path.Combine(Feed, "Some.Tool.1.0.0.nupkg"), ZipArchiveMode.Create))
            other.CreateEntryFromFile(typeof(Xunit.Assert).Assembly.Location, "lib/net8.0/Some.Tool.dll");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp folder left behind is harmless */ }
    }

    [Fact]
    public void The_feed_offers_its_library_packages_newest_version_first()
    {
        var feeds = LibraryPackage.Feeds(Feed);
        Assert.Equal(new[] { ("Vein.Tests", "0.2.0") }, LibraryPackage.Available(feeds));
        Assert.Equal("0.1.0-dev.20260202000000", LibraryPackage.Find("Vein.Tests", "0.1.0-*", feeds)?.Version);
        Assert.Equal("0.2.0", LibraryPackage.Find("Vein.Tests", "*", feeds)?.Version);
        Assert.Null(LibraryPackage.Find("Vein.Tests", "9.9.9", feeds));
    }

    [Fact]
    public void Adding_a_package_records_it_unpacks_it_and_lists_it_for_the_tools()
    {
        File.WriteAllText(Path.Combine(Project, VeinProject.FileName), "{\n  \"Principal\": \"site.vein\"\n}\n");
        Assert.DoesNotContain("testkit.Hello", BundleIndex.For(Project).Owners.Keys);

        LibraryPackage.SetPackage(Project, "Vein.Tests", "0.1.0-*");
        var restored = Assert.Single(LibraryPackage.Restore(Project, Feed));
        Assert.Null(restored.Problem);
        Assert.Equal("0.1.0-dev.20260202000000", restored.Version);
        Assert.True(BundleIndex.IsLibraryRoot(restored.Library!.Root));

        var project = VeinProject.Load(Project)!;
        Assert.Equal("site.vein", project.Principal);                                 // the rest of the file kept
        Assert.Equal("0.1.0-*", project.Packages!["Vein.Tests"]);
        Assert.Contains(restored.Library.Root, File.ReadAllText(Path.Combine(Project, BundleIndex.LibrariesFile)));
        Assert.Contains("testkit.Hello", BundleIndex.For(Project).Owners.Keys);        // `need` resolves now

        // Removing it takes it off the list the tools search.
        LibraryPackage.SetPackage(Project, "Vein.Tests", null);
        Assert.Empty(LibraryPackage.Restore(Project, Feed));
        Assert.DoesNotContain(restored.Library.Root, File.ReadAllText(Path.Combine(Project, BundleIndex.LibrariesFile)));
    }

    [Fact]
    public void A_package_the_feed_lacks_is_reported_and_the_others_still_restore()
    {
        LibraryPackage.SetPackage(Project, "Vein.Tests", "*");
        LibraryPackage.SetPackage(Project, "Vein.Missing", "1.0.0");
        var restored = LibraryPackage.Restore(Project, Feed);
        Assert.Null(restored.Single(r => r.Id == "Vein.Tests").Problem);
        Assert.Contains("is not in", restored.Single(r => r.Id == "Vein.Missing").Problem);
        Assert.Contains("testkit.Hello", BundleIndex.For(Project).Owners.Keys);
    }

    [Fact]
    public void Saving_a_veinproj_keeps_its_packages()
    {
        // The Workbench rewrites .veinproj when it saves a run configuration; the packages must survive it.
        LibraryPackage.SetPackage(Project, "Vein.Tests", "0.1.0-*");
        var project = VeinProject.Load(Project)!;
        project.Runs.Add(new SavedRun("serve", "serve", new List<string>(), new Dictionary<string, string>()));
        project.Save(Project);
        Assert.Equal(new[] { ("Vein.Tests", "0.1.0-*") }, LibraryPackage.Packages(Project));
    }
}
