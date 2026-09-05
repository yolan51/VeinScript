using Vein.Compiler.Project;
using Xunit;

namespace Vein.Tests;

// The published name IS the address. Every assertion here is about a way a bare filename would have
// gone wrong: two bundles overwriting each other's `Boot.vein`, or a file coming back without the
// folder that decides whether it is API or behaviour.
public class VeinNameTests
{
    private static string Temp() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "vein-name-" + Guid.NewGuid().ToString("N"))).FullName;

    [Theory]
    [InlineData("Combat.vein",                    "alice.Combat.vein")]
    [InlineData("publicators/Shapes.vein",        "alice.Combat.publicators.Shapes.vein")]
    [InlineData("shards/Boot.vein",               "alice.Combat.shards.Boot.vein")]
    [InlineData("app.vein",                       "alice.Combat.app.vein")]
    [InlineData("publicators/ui/Panels.vein",     "alice.Combat.publicators.ui.Panels.vein")]
    public void A_path_becomes_its_address_and_comes_back(string path, string expected)
    {
        Assert.Equal(expected, VeinNames.ToName("alice", "Combat", path));

        var parsed = VeinNames.Parse(expected);
        Assert.NotNull(parsed);
        Assert.Equal("alice", parsed!.Author);
        Assert.Equal("Combat", parsed.Bundle);
        Assert.Equal(path, parsed.RelativePath);
    }

    [Fact]
    public void Two_bundles_no_longer_collide_on_Boot()
    {
        // THE bug this exists for. Every bundle the New Project scaffold makes contains shards/Boot.vein,
        // so a name-keyed upsert would have the second push overwrite the first — and report success.
        string a = VeinNames.ToName("alice", "Combat", "shards/Boot.vein")!;
        string b = VeinNames.ToName("alice", "Inventory", "shards/Boot.vein")!;

        Assert.NotEqual(a, b);
        Assert.Equal("alice.Combat.shards.Boot.vein", a);
        Assert.Equal("alice.Inventory.shards.Boot.vein", b);
    }

    [Fact]
    public void Two_authors_may_both_have_a_Combat()
    {
        // Which is exactly how *Author.Bundle already disambiguates inside the language.
        Assert.NotEqual(VeinNames.ToName("alice", "Combat", "Combat.vein"),
                        VeinNames.ToName("bob", "Combat", "Combat.vein"));
    }

    [Fact]
    public void The_folder_survives_the_round_trip()
    {
        // `publicators/` and `shards/` are structural — BundleLoader merges them into the bundle, and a
        // decl in the wrong one is VS0321. A name that lost the folder could not be restored into a
        // project that compiles.
        foreach (string folder in BundleLoader.FragmentFolders)
        {
            string name = VeinNames.ToName("alice", "Combat", $"{folder}/Thing.vein")!;
            Assert.Equal($"{folder}/Thing.vein", VeinNames.ToPath(name));
        }
    }

    [Fact]
    public void Every_file_of_every_scaffolded_kind_addresses_and_restores()
    {
        // Against what the Workbench actually generates, rather than against paths I invented.
        string root = Temp();
        try
        {
            foreach (var kind in new[] { ProjectKind.Scratch, ProjectKind.Bundle, ProjectKind.Solution })
            {
                string parent = Path.Combine(root, kind.ToString());
                Directory.CreateDirectory(parent);
                ProjectScaffold.New(kind, parent, "Demo", "alice");

                var pkg = ProjectPackage.Create(Path.Combine(parent, "Demo"));
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var file in pkg.Files)
                {
                    // .veinproj and vein.discovery travel in the package but are not .vein source.
                    if (!file.Path.EndsWith(".vein", StringComparison.Ordinal)) continue;

                    string? name = VeinNames.ToName("alice", "Demo", file.Path);
                    Assert.True(name is not null,
                        $"{kind}: {file.Path} — {VeinNames.Reason("alice", "Demo", file.Path)}");

                    Assert.True(seen.Add(name!), $"{kind}: two files claimed the name {name}");
                    Assert.Equal(file.Path, VeinNames.ToPath(name!));
                }
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void An_app_manifest_is_not_mistaken_for_a_publicator()
    {
        var parsed = VeinNames.Parse("alice.MyGame.app.vein");

        Assert.NotNull(parsed);
        Assert.Equal("app.vein", parsed!.RelativePath);
    }

    [Fact]
    public void A_dotted_filename_is_refused_with_a_reason_rather_than_guessed()
    {
        // `shop.app.vein` and `shop/app.vein` would produce the same name, and only one of them could
        // come back. Silently picking is worse than saying so — and the message names the fix.
        Assert.Null(VeinNames.ToName("alice", "Shop", "shop.app.vein"));

        string? why = VeinNames.Reason("alice", "Shop", "shop.app.vein");
        Assert.NotNull(why);
        Assert.Contains("app.vein", why!);
    }

    [Fact]
    public void A_path_that_escapes_the_folder_is_refused()
    {
        // This path is used to write files back onto someone else's disk.
        Assert.Null(VeinNames.ToName("alice", "Combat", "../../.ssh/id_rsa.vein"));
        Assert.Null(VeinNames.ToName("alice", "Combat", "/etc/thing.vein"));
        Assert.Null(VeinNames.ToName("alice", "Combat", "C:/Windows/thing.vein"));
    }

    [Fact]
    public void A_name_from_somewhere_else_parses_to_nothing()
    {
        Assert.Null(VeinNames.Parse("Boot.vein"));            // no author, no bundle
        Assert.Null(VeinNames.Parse("alice.Combat.txt"));     // not .vein
        Assert.Null(VeinNames.Parse("alice..Boot.vein"));     // empty segment
        Assert.Null(VeinNames.Parse(""));
    }

    [Fact]
    public void Backslashes_are_normalised_on_the_way_in()
    {
        // Windows hands out `shards\Boot.vein`. A backslash in a published name is a name that only
        // works on the platform it was made on.
        Assert.Equal("alice.Combat.shards.Boot.vein",
                     VeinNames.ToName("alice", "Combat", @"shards\Boot.vein"));
    }
}
