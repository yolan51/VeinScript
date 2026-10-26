using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Project;
using Xunit;

namespace Vein.Tests;

// THREE BUNDLES, ONE WORD: samples/event_strum. alice.Guitar, bob.Banjo and carol.Harp each declare
// `@Strum` with their own payload, and a Band needs all three.
//
// Two ways a reference to one of them went nowhere, silently:
//   * a SHORTENED path, `*Banjo.Api.@Strum`, was keyed as written (`Banjo.Api.Strum`) instead of as its
//     owner (`bob.Banjo.Strum`): it compiled "ok" and nothing heard it;
//   * a bare `@Strum` with two owners in scope warned VS0216 at `:0:0`, once per internal lookup.
//
// Each test runs a copy of the sample with one line of band.vein changed.
public class EventPathTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-eventpath-" + Guid.NewGuid().ToString("N"));

    public EventPathTests()
    {
        string sample = Path.Combine(RepoRoot(), "samples", "event_strum");
        Directory.CreateDirectory(Path.Combine(_dir, "bundles"));
        foreach (var f in new[] { "band.vein", "event_strum.app.vein", "vein.discovery" })
            File.Copy(Path.Combine(sample, f), Path.Combine(_dir, f));
        foreach (var f in Directory.GetFiles(Path.Combine(sample, "bundles")))
            File.Copy(f, Path.Combine(_dir, "bundles", Path.GetFileName(f)));
    }

    public void Dispose()
    {
        // Only ever the temp directory this test just created — and the index entry it caused.
        BundleIndex.Invalidate(Path.Combine(_dir, "bundles"));
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with stdlib/ not found");
    }

    private (string Out, DiagnosticBag Diag) Run(string? from = null, string? to = null)
    {
        string band = Path.Combine(_dir, "band.vein");
        if (from is not null)
        {
            string text = File.ReadAllText(band);
            Assert.Contains(from, text);
            File.WriteAllText(band, text.Replace(from, to));
        }
        string app = Path.Combine(_dir, "event_strum.app.vein");
        var diag = new DiagnosticBag();
        var linked = AppLinker.Link(app, File.ReadAllText(app), diag);
        if (linked is null || diag.Items.Any(d => d.Severity == Severity.Error)) return ("", diag);
        var sw = new StringWriter();
        new Interp { Ticks = 1 }.Run(linked.Module, new StringReader(""), sw);
        return (sw.ToString(), diag);
    }

    [Fact]
    public void Each_Strum_reaches_only_its_own_owner_and_whoever_named_it()
    {
        var (output, diag) = Run();

        Assert.DoesNotContain(diag.Items, d => d.Severity == Severity.Error);
        Assert.Contains("[guitar] heard its own Strum: note E on fret 3", output);
        Assert.Contains("[harp]   heard its own Strum: C in octave 4", output);
        // the full path, and the shortened `*Banjo.Api.@Strum`, both reach bob's Banjo and the band's hear
        Assert.Contains("[banjo]  heard its own Strum: course 2, roll=true", output);
        Assert.Contains("[band]   heard bob's Strum: course 9, roll=false", output);
        Assert.Contains("[banjo]  heard its own Strum: course 9, roll=false", output);
        Assert.Equal(6, output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void A_path_shortened_past_unique_is_VS0306_at_its_line_naming_every_owner()
    {
        var (_, diag) = Run("// emit *Api.@Strum", "emit *Api.@Strum");

        var e = Assert.Single(diag.Items, d => d.Code == "VS0306");
        Assert.Equal(Severity.Error, e.Severity);
        Assert.True(e.Span.Line > 0);
        Assert.Contains("*alice.Guitar.Api.@Strum", e.Message);
        Assert.Contains("*bob.Banjo.Api.@Strum", e.Message);
        Assert.Contains("*carol.Harp.Api.@Strum", e.Message);
    }

    [Fact]
    public void A_bare_ambiguous_Strum_warns_once_at_its_line_and_the_alias_stays_out_of_it()
    {
        var (_, diag) = Run("// emit @Strum", "emit @Strum");

        var w = Assert.Single(diag.Items, d => d.Code == "VS0216");
        Assert.True(w.Span.Line > 0);
        Assert.Contains("*alice.Guitar.Api.@Strum", w.Message);
        Assert.Contains("*bob.Banjo.Api.@Strum", w.Message);
        Assert.DoesNotContain("carol", w.Message);   // `need "carol.Harp" as H` widens no bare name
    }
}
