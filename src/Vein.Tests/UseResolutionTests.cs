using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// `use N` — the bare-name fallback.
//
// `use` lexed, parsed into a UseDecl, printed in the AST dump, and was then dropped by Lower under a
// comment reading "resolved away" that described a resolution nobody had written. Samples said
// `use Core` and got nothing for it.
//
// What it does now: after every LOCAL lookup misses, a bare name is looked for in the bundles this one
// `use`s. Local always wins, so the change is strictly additive — which is the property most of these
// tests exist to hold down, since the risk here is not "does it resolve" but "did resolving it change
// something that already worked".
//
// Events are deliberately not involved. A bare `@Response` already dispatches, because an emit lowers
// to its bare event name and Interp.Drain matches on that — samples rely on it (boot_shared.vein).
public class UseResolutionTests
{
    private static CompilationResult Compile(string src) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", src));

    /// Compile and run `src`, returning everything it printed.
    private static string Run(string src, int ticks = 0)
    {
        var r = Compile(src);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var sw = new StringWriter();
        new Interp { Ticks = ticks }.Run(r.Modules[0], new StringReader(""), sw);
        return sw.ToString();
    }

    private static IEnumerable<Diagnostic> Warnings(string src, string code) =>
        Compile(src).Diagnostics.Where(d => d.Code == code);

    // ---- what `use` buys ------------------------------------------------------------------------

    [Fact]
    public void A_used_bundles_SF_is_callable_by_its_bare_name()
    {
        var output = Run(
            "bundle T by me {\n" +
            "  use Console\n" +
            "  shard S { run once { print(\"hi\") } }\n}");

        Assert.Equal("hi", output.Trim());
    }

    [Fact]
    public void A_used_bundles_builder_is_reachable_by_its_bare_name()
    {
        // `bring Button(…)` with no qualifier resolved only against this bundle before.
        var output = Run(
            "bundle T by me {\n" +
            "  use Web\n" +
            "  shard S { run once { bring Button(\"Go\") } }\n" +
            "  shard L { hear @Html as h { *Vein.Console.Io.print(h.markup) } }\n}");

        // `$Button` carries id/class/onclick as well as the label, so a one-argument call fills the
        // label and leaves the rest empty. `label` is FIRST in the shape precisely so that this call
        // still means what it says — see the ordering note in stdlib/UI.vein.
        Assert.Contains(">Go</button>", output);
    }

    [Fact]
    public void A_used_bundles_shape_expands_in_a_bare_include()
    {
        var output = Run(
            "bundle T by me {\n" +
            "  use Math\n" +
            "  event @Where { $Vec2, tag: string }\n" +
            "  shard S { run once { emit @Where { x: 1.0, y: 2.0, tag: \"t\" } } }\n" +
            "  shard L { hear @Where as w { *Vein.Console.Io.print(\"at \" + w.x + \",\" + w.y + \" \" + w.tag) } }\n}");

        Assert.Contains("at 1,2 t", output);
    }

    // ---- the additive guarantee ------------------------------------------------------------------

    [Fact]
    public void Without_use_a_bare_call_still_resolves_to_nothing()
    {
        // The control for the first test. If this ever starts printing, `use` stopped being the thing
        // that opts a bundle in and became ambient stdlib scope — a different language.
        //
        // It now says so rather than compiling to a call that answers null. This test used to assert
        // the empty OUTPUT, which held for the right reason and the wrong one at once: `print` not
        // resolving looked identical to `print` resolving and printing nothing.
        Assert.Contains(Compile(
            "bundle T by me {\n" +
            "  shard S { run once { print(\"hi\") } }\n}").Diagnostics,
            d => d.Code == "VS0234");
    }

    [Fact]
    public void Without_use_a_bare_builder_is_still_unknown()
    {
        Assert.Contains(Compile(
            "bundle T by me {\n" +
            "  shard S { run once { bring Button(\"Go\") } }\n}").Diagnostics,
            d => d.Code == "VS0203");
    }

    [Fact]
    public void A_local_declaration_wins_over_a_used_one()
    {
        // Precedence is what makes this safe to add: every existing program resolves locally first, so
        // nothing can change meaning. A local `print` must shadow Console's.
        var output = Run(
            "bundle T by me {\n" +
            "  use Console\n" +
            "  SF print(text: string) { emit *Vein.Console.Io.@Print { text: \"local:\" + text } }\n" +
            "  shard S { run once { print(\"hi\") } }\n}");

        Assert.Equal("local:hi", output.Trim());
    }

    [Fact]
    public void A_built_in_wins_over_a_used_one_and_says_so()
    {
        // The same precedence rule as the test above, one level down: a BUILT-IN already resolves, so
        // `use` must not capture it either. `use Console` did — it exports `spawn(name, firsttext)`, a
        // console-window launcher, which took over bare `spawn()`. The call then built no entity and
        // reported nothing, so every `target` in the program matched an empty world and the symptom
        // surfaced nowhere near the `use` line that caused it.
        //
        // Asserting on the ENTITY is the point: a test that only checked the warning would still pass if
        // the call went back to launching console windows.
        const string src =
            "bundle T by me {\n" +
            "  use Console\n" +
            "  shape $Tag { n: int }\n" +
            "  shard S {\n" +
            "    run once { let e = spawn()\n" +
            "               attach $Tag to e { n: 7 } }\n" +
            "    settled { target $Tag as self { *Vein.Console.Io.print(\"found \" + self.Tag.n) } }\n" +
            "  }\n}";

        Assert.Equal("found 7", Run(src, ticks: 1).Trim());

        var hits = Warnings(src, "VS0217").ToList();
        Assert.Single(hits);
        Assert.Contains("Vein.Console.Io.spawn", hits[0].Message);
    }

    // ---- ambiguity is reported, not guessed ------------------------------------------------------

    [Fact]
    public void A_name_exported_by_two_used_bundles_is_reported()
    {
        // `send` is a real collision in the stdlib: Vein.Console.Io.send and Vein.Net.Peer.send, which
        // exist as one vocabulary in two bundles on purpose. Picking one silently would make the choice
        // depend on dictionary order.
        var hits = Warnings(
            "bundle T by me {\n" +
            "  use Console\n  use Net\n" +
            "  shard S { run once { send(#X, \"hi\") } }\n}", "VS0216").ToList();

        Assert.Single(hits);
        Assert.Contains("Vein.Console.Io.send", hits[0].Message);
        Assert.Contains("Vein.Net.Peer.send", hits[0].Message);
    }

    [Fact]
    public void One_used_bundle_alone_is_not_ambiguous()
    {
        // The other half: the collision above must not make `use Console` on its own report anything,
        // or the warning becomes noise on the common case.
        Assert.Empty(Warnings(
            "bundle T by me {\n" +
            "  use Console\n" +
            "  shard S { run once { send(#X, \"hi\") } }\n}", "VS0216"));
    }

    [Fact]
    public void Using_a_bundle_that_exports_nothing_by_that_name_changes_nothing()
    {
        // `use` widens what a bare name MAY mean; it does not make unrelated names resolve. Math
        // exports no `print`, so this is as unknown as it was with no `use` at all — and now reads as
        // VS0234 rather than as a program that runs and prints nothing.
        Assert.Contains(Compile(
            "bundle T by me {\n" +
            "  use Math\n" +
            "  shard S { run once { print(\"hi\") } }\n}").Diagnostics,
            d => d.Code == "VS0234");
    }

    // ---- attaching a shape that `use` resolved ------------------------------------------------------
    //
    // THE ATTACH WORKED AND THE READ DID NOT, which is the worst shape a bug can take. The entity
    // spawned, `target $Position #Moving` MATCHED it, and `m.Position.x` came back empty — because a
    // field access resolves to a component only when the module carries a component type of that name
    // (Interp.IsComponent), and nothing imported one.
    //
    // Lower already had `RegisterImportedShape` for exactly this, added when a builder INCLUDE hit the
    // same wall. The `attach` statement is the other way in and was missed.

    private const string UsedShape = """
        bundle T by me {
            use Transform
            mark #Moving
            shard Make {
                run once {
                    let e = spawn()
                    attach $Position to e { x: 1.5, y: 2.5, z: 0.0 }
                    mark e #Moving
                }
            }
            shard Show {
                each tick {
                    target $Position #Moving as m {
                        emit *Vein.Console.Io.@Print { text: "at " + m.Position.x + "," + m.Position.y }
                    }
                }
            }
        }
        """;

    [Fact]
    public void A_shape_reached_through_use_can_be_read_back_after_it_is_attached()
    {
        Assert.Contains("at 1.5,2.5", Run(UsedShape, ticks: 1));
    }

    [Fact]
    public void The_module_carries_the_imported_component_type()
    {
        // The mechanism behind the test above, asserted directly — and it is also what gives the C#
        // backend a component to emit, so the two runtimes cannot disagree about a shape one of them
        // has never heard of.
        var module = Assert.Single(Compile(UsedShape).Modules);

        Assert.Contains(module.Types, t =>
            t.Name == "Position" && t.Kind == Vein.Compiler.Ir.IrTypeKind.Component);
    }

    [Fact]
    public void The_backend_emits_the_imported_component_too()
    {
        // The other half, and the one that decides whether this fix created a DIVERGENCE instead of
        // curing one. The C# backend builds its component types from `module.Types`, so importing the
        // shape there is what stops the compiled program from knowing nothing about a shape the
        // interpreter can read — which would have been a worse bug than the one being fixed.
        var module = Assert.Single(Compile(UsedShape).Modules);
        var emitted = new Vein.Compiler.Backends.CSharpBackend().Emit(module);

        Assert.True(emitted.Success);
        Assert.Contains("struct Position", emitted.Files[0].Contents);
        Assert.Contains("public double x", emitted.Files[0].Contents);
    }

    [Fact]
    public void A_locally_declared_shape_is_not_replaced_by_a_used_one()
    {
        // `use` only ever WIDENS what a bare name may mean. A bundle that declares its own `$Position`
        // keeps it, fields and all, even while using a bundle that exports one too — otherwise adding a
        // `use` line could silently change the shape of data a program already stores.
        var result = Compile("""
            bundle T by me {
                use Transform
                shape $Position { label: string }
                mark #Here
                shard Make {
                    run once {
                        let e = spawn()
                        attach $Position to e { label: "home" }
                        mark e #Here
                    }
                }
                shard Show {
                    each tick {
                        target $Position #Here as p {
                            emit *Vein.Console.Io.@Print { text: "label " + p.Position.label }
                        }
                    }
                }
            }
            """);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Select(d => d.ToString())));

        var position = Assert.Single(result.Modules[0].Types,
            t => t.Name == "Position" && t.Kind == Vein.Compiler.Ir.IrTypeKind.Component);

        Assert.Contains(position.Fields, f => f.Name == "label");
        Assert.DoesNotContain(position.Fields, f => f.Name == "x");

        var output = new StringWriter();
        new Interp { Ticks = 1 }.Run(result.Modules[0], new StringReader(""), output);
        Assert.Contains("label home", output.ToString());
    }
}
