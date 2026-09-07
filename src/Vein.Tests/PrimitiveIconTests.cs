using System.Text.Json;
using System.Xml.Linq;
using Vein.Compiler.Project;
using Xunit;

namespace Vein.Tests;

// The primitive icons the Workbench draws in its outline.
//
// TESTED FROM DISK, not through the panel: `Vein.Tests` references only `Vein.Compiler` and
// `Vein.Cloud` — deliberately, per docs/WORKBENCH.md — so `PrimitiveIcons` itself is out of reach.
// What is reachable is everything that makes it work: the files exist, the csproj ships them, and the
// mapping covers the kinds it claims to. That is the half that fails silently.
//
// AND IT FAILS AT RUNTIME, WHICH IS WHY IT IS WORTH A TEST. `AssetLoader.Open` throws on a missing
// asset, so a renamed file or a dropped `<AvaloniaResource>` line is not a build error — it is an
// exception the first time somebody opens the Outline tab.
public class PrimitiveIconTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with stdlib/ not found");
    }

    private static string IconDir() =>
        Path.Combine(RepoRoot(), "src", "Vein.Workbench", "Assets", "primitives");

    /// The filenames `PrimitiveIcons.FileFor` maps to. Kept here rather than imported because the
    /// Workbench cannot be referenced — so this list is the contract, and a rename that breaks it
    /// fails here instead of on a panel's first paint.
    private static readonly string[] KindIcons =
        { "bundle", "publicator", "shape", "mark", "event", "builder", "shard", "bridge", "function" };

    [Fact]
    public void Every_icon_the_outline_asks_for_exists()
    {
        var missing = KindIcons.Where(n => !File.Exists(Path.Combine(IconDir(), n + ".png"))).ToList();
        Assert.True(missing.Count == 0, "missing icon(s): " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_kind_that_is_a_primitive_has_art()
    {
        // The enum is the source of truth for what a declaration can be. `Var` is a local rather than a
        // primitive and is the one deliberate omission — if a kind is ever added to SymbolKind, this
        // says so rather than leaving a silently blank row.
        var expected = Enum.GetValues<SymbolKind>().Where(k => k != SymbolKind.Var).ToList();

        var uncovered = expected.Where(k => FileFor(k) is null).ToList();
        Assert.True(uncovered.Count == 0,
            "SymbolKind with no icon: " + string.Join(", ", uncovered));
    }

    [Fact]
    public void The_csproj_ships_them_as_AvaloniaResource()
    {
        // EmbeddedResource would not do: `avares://` resolves AvaloniaResource, and the wrong one of
        // the two is a missing-asset throw rather than a build failure.
        var doc = XDocument.Load(Path.Combine(RepoRoot(), "src", "Vein.Workbench", "Vein.Workbench.csproj"));

        Assert.Contains(doc.Descendants("AvaloniaResource"),
            e => (string?)e.Attribute("Include") is { } i && i.Contains("primitives"));
    }

    [Fact]
    public void The_manifest_and_the_folder_agree()
    {
        // The manifest is what the art was delivered with, and it is the machine-readable half of the
        // README. If the two drift, the README is describing files that are not there.
        using var stream = File.OpenRead(Path.Combine(IconDir(), "manifest.json"));
        using var json = JsonDocument.Parse(stream);

        var listed = json.RootElement.GetProperty("icons")
            .EnumerateArray()
            .Select(e => e.GetProperty("file").GetString()!)
            .ToList();

        Assert.Equal(16, listed.Count);

        var absent = listed.Where(f => !File.Exists(Path.Combine(IconDir(), f))).ToList();
        Assert.True(absent.Count == 0, "manifest lists files that are not there: " + string.Join(", ", absent));
    }

    [Fact]
    public void The_concepts_that_are_not_declarations_are_still_available()
    {
        // Seven of the sixteen map to no SymbolKind — they are panel concepts, not declarations. They
        // are shipped anyway and reachable by name, so the Diagnostics, Dependencies and Execution
        // tabs have art waiting rather than needing a second delivery.
        foreach (string name in new[] { "identity", "import", "target", "fold", "tick", "diagnostic", "dependency" })
            Assert.True(File.Exists(Path.Combine(IconDir(), name + ".png")), $"{name}.png is missing");
    }

    /// Mirrors `PrimitiveIcons.FileFor`. Duplicated on purpose — see the class comment.
    private static string? FileFor(SymbolKind kind) => kind switch
    {
        SymbolKind.Bundle => "bundle",
        SymbolKind.Publicator => "publicator",
        SymbolKind.Shape => "shape",
        SymbolKind.Mark => "mark",
        SymbolKind.Event => "event",
        SymbolKind.Builder => "builder",
        SymbolKind.Shard or SymbolKind.ShardView => "shard",
        SymbolKind.Bridge => "bridge",
        SymbolKind.SF or SymbolKind.Fn => "function",
        _ => null,
    };
}
