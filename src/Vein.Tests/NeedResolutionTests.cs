using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// `need "Author.Bundle"` — the bare-name fallback, and the alias that deliberately is not one.
//
// These are the resolution rules, all of which the retired `use` already had and `need` keeps: after
// every LOCAL lookup misses, a bare name is looked for in the bundles this one needs. Local always
// wins, then built-ins, then the fallback — which is the property most of these tests exist to hold
// down, since the risk is not "does it resolve" but "did resolving it change something that worked".
//
// NeedDeclarationTests covers what `need` added on top: the author being part of the name, and the
// declaration being validated where it is written.
//
// Events are deliberately not involved. A bare `@Response` already dispatches, because an emit lowers
// to its bare event name and Interp.Drain matches on that — samples rely on it (boot_shared.vein).
public class NeedResolutionTests
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
            "  need \"Vein.Console\"\n" +
            "  shard S { run once { print(\"hi\") } }\n}");

        Assert.Equal("hi", output.Trim());
    }

    [Fact]
    public void A_used_bundles_builder_is_reachable_by_its_bare_name()
    {
        // `bring Button(…)` with no qualifier resolved only against this bundle before.
        var output = Run(
            "bundle T by me {\n" +
            "  need \"Vein.Web\"\n" +
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
            "  need \"Vein.Math\"\n" +
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
            "  need \"Vein.Console\"\n" +
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
            "  need \"Vein.Console\"\n" +
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
            "  need \"Vein.Console\"\n  need \"Vein.Net\"\n" +
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
            "  need \"Vein.Console\"\n" +
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
            "  need \"Vein.Math\"\n" +
            "  shard S { run once { print(\"hi\") } }\n}").Diagnostics,
            d => d.Code == "VS0234");
    }

    // ---- `use X as Y` — importing qualified ---------------------------------------------------------
    //
    // The syntax parsed from the day `use` was added — `UseDecl(Name, Alias, Span)` — and NOTHING
    // consumed the alias. Only the AST printer echoed it. So `use Combat as C` silently behaved as a
    // plain `use Combat`: it widened bare names, `*C.…` resolved nowhere, and the one thing an alias
    // exists to do was the one thing it did not.
    //
    // AN ALIAS IMPORTS QUALIFIED, NOT BARE, and that is the whole point. `use Combat` beside `use UI`
    // when both export `Damage` is VS0216 — ambiguous — and the only escape was writing the full
    // `*author.Combat.Fx.Damage` at every use site. If an alias ALSO widened, aliasing both would leave
    // bare `Damage` just as ambiguous and would have solved nothing. So it does not widen: it names the
    // bundle segment of a `*` path instead, and two aliased bundles cannot collide because neither
    // contributes a bare name at all. `import numpy as np`, not `from numpy import *`.

    [Fact]
    public void An_alias_names_the_bundle_segment_of_a_qualified_path()
    {
        var output = Run("""
            bundle T by me {
                need "Vein.Math" as M
                shard S {
                    run once {
                        emit *Vein.Console.Io.@Print { text: "sqrt " + *M.Roots.sqrt(16.0) }
                    }
                }
            }
            """);

        Assert.Contains("sqrt 4", output);
    }

    [Fact]
    public void An_alias_does_NOT_widen_bare_names()
    {
        // The load-bearing half. Without this an alias is a plain `use` with extra syntax.
        Assert.Contains(Compile("""
            bundle T by me {
                need "Vein.Math" as M
                shard S { run once { let x = sqrt(16.0) } }
            }
            """).Diagnostics, d => d.Code == "VS0234");
    }

    [Fact]
    public void A_plain_use_still_widens()
    {
        // The regression guard for the line above: the alias branch must not have taken the bare path
        // away from an ordinary `use`.
        Assert.DoesNotContain(Compile("""
            bundle T by me {
                need "Vein.Math"
                shard S { run once { let x = sqrt(16.0) } }
            }
            """).Diagnostics, d => d.Code == "VS0234");
    }

    [Fact]
    public void Two_aliased_bundles_cannot_be_ambiguous_with_each_other()
    {
        // The reason the feature exists. Neither contributes a bare name, so there is nothing to be
        // ambiguous about — VS0216 cannot fire, and both vocabularies stay reachable.
        var diags = Compile("""
            bundle T by me {
                need "Vein.Math" as M
                need "Vein.Console" as C
                shard S {
                    run once {
                        emit *Vein.Console.Io.@Print { text: "" + *M.Round.floor(-2.5) }
                    }
                }
            }
            """).Diagnostics;

        Assert.DoesNotContain(diags, d => d.Code == "VS0216");
        Assert.DoesNotContain(diags, d => d.Severity == Vein.Compiler.Diagnostics.Severity.Error);
    }

    [Fact]
    public void Only_the_first_segment_is_substituted()
    {
        // An alias names a BUNDLE. A publicator or member that happens to share its spelling is not one,
        // so substitution stops after the head — otherwise `use X as Round` would rewrite the publicator
        // segment of `*Vein.Math.Round.floor` and resolve somewhere absurd.
        var output = Run("""
            bundle T by me {
                need "Vein.Math" as Round
                shard S {
                    run once {
                        emit *Vein.Console.Io.@Print { text: "" + *Round.Round.floor(-2.5) }
                    }
                }
            }
            """);

        Assert.Contains("-3", output);
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
            need "Vein.Transform"
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
                need "Vein.Transform"
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
