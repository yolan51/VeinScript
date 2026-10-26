using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Project;
using Xunit;

namespace Vein.Tests;

// A SHARED fn RUNS WHERE IT WAS WRITTEN (ROADMAP Tier 1 #4).
//
// A shared fn called from another bundle is imported into the caller, and its body used to be resolved
// against the CALLER: a bare call to its own sibling, its own private helper, or a function of a bundle its
// author `need`s was unknown there, so library authors had to qualify every call to their own code
// (`stdlib/Math.vein` says so in capitals). VeinChain's first real payout tripped on it: bob.App's tests
// called `afterHit`, whose bare `clamp` came from alice.Lib, which only bob.App needs.
//
// Now a bare call inside an imported body means what it means in the owner's file.
public class OwnerScopedImportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-ownerscope-" + Guid.NewGuid().ToString("N"));
    private readonly string _bundles;

    public OwnerScopedImportTests()
    {
        _bundles = Path.Combine(_dir, "bundles");
        Directory.CreateDirectory(_bundles);
        File.WriteAllText(Path.Combine(_bundles, "Lib.vein"), """
            bundle Lib by alice {
                publicator Api {
                    shared("Clamp v between lo and hi.")
                    fn clamp(v: int, lo: int, hi: int) return int {
                        var r = v
                        if r < lo { r = lo }
                        if r > hi { r = hi }
                        return r
                    }
                }
            }
            """);
        File.WriteAllText(Path.Combine(_bundles, "App.vein"), """
            bundle App by bob {
                need "alice.Lib"
                fn bonus(x: int) return int { return x + 1000 }
                publicator Api {
                    shared("Twice a value.")
                    fn twice(x: int) return int { return x * 2 }
                    shared("Health after a hit: clamped by alice.Lib, doubled by a sibling, a private bonus at zero.")
                    fn afterHit(hp: int, hit: int) return int {
                        let kept = clamp(hp - hit, 0, 100)
                        if kept == 0 { return bonus(kept) }
                        return twice(kept)
                    }
                }
            }
            """);
    }

    public void Dispose()
    {
        // Only ever the temp directory this test just created — and the index entry it caused.
        BundleIndex.Invalidate(_bundles);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private (string Out, DiagnosticBag Diag) Run(string game)
    {
        File.WriteAllText(Path.Combine(_dir, "Game.vein"), game);
        string app = Path.Combine(_dir, "G.app.vein");
        File.WriteAllText(app, "app G { load \"Game.vein\" }");
        var diag = new DiagnosticBag();
        var linked = AppLinker.Link(app, File.ReadAllText(app), diag);
        if (linked is null || diag.Items.Any(d => d.Severity == Severity.Error)) return ("", diag);
        var sw = new StringWriter();
        new Interp { Ticks = 1 }.Run(linked.Module, new StringReader(""), sw);
        return (sw.ToString(), diag);
    }

    [Fact]
    public void A_needed_fn_calls_its_owners_need_its_sibling_and_its_private_helper_by_bare_name()
    {
        var (output, diag) = Run("""
            bundle Game by you {
                need "bob.App"
                shard Boot {
                    run once {
                        emit *Vein.Console.Io.@Print { text: "hit30=" + afterHit(100, 30) }
                        emit *Vein.Console.Io.@Print { text: "over=" + afterHit(100, -80) }
                        emit *Vein.Console.Io.@Print { text: "ko=" + afterHit(10, 50) }
                    }
                }
            }
            """);

        Assert.DoesNotContain(diag.Items, d => d.Severity == Severity.Error);
        Assert.Contains("hit30=140", output);
        Assert.Contains("over=200", output);
        Assert.Contains("ko=1000", output);
    }

    [Fact]
    public void The_callers_own_fn_of_the_same_name_does_not_capture_the_imported_body()
    {
        // Game declares its own `clamp` that would give a different answer: the imported body still means
        // alice.Lib's, because that is what bob.App's author wrote against.
        var (output, diag) = Run("""
            bundle Game by you {
                need "bob.App"
                fn clamp(v: int, lo: int, hi: int) return int { return 7 }
                shard Boot {
                    run once {
                        emit *Vein.Console.Io.@Print { text: "hit30=" + afterHit(100, 30) }
                        emit *Vein.Console.Io.@Print { text: "own=" + clamp(1, 2, 3) }
                    }
                }
            }
            """);

        Assert.DoesNotContain(diag.Items, d => d.Severity == Severity.Error);
        Assert.Contains("hit30=140", output);
        Assert.Contains("own=7", output);
    }
}
