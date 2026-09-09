using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// Handover U — `$Parent` makes a `$Position` LOCAL, and the runtime publishes the composed `$World`.
//
// `Vein.Core.Relations.$Parent { of: Entity }` existed from the beginning and meant nothing to anything:
// every position was absolute, and a kit that wanted a child to follow its parent rewrote the child's
// absolute position every tick at O(children x parents), could not order a deep chain, and could not
// express rotation about a parent at all.
//
// TWO SHAPES RATHER THAN COMPOSING IN PLACE, which is the decision the whole entry turns on. If a
// parented identity's `$Position` were simply read as composed, a collision pass subtracting one
// identity's local from another's absolute would not fault and would not look wrong — the renderer
// composes correctly, so the crate is DRAWN where it belongs and fails to collide with what it is
// visibly touching. `$Position` stays the program's own value; the composed one gets a name.
public class TransformHierarchyTests
{
    private static string Run(string body, int ticks = 1)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by me {\n  need \"Vein.Transform\"\n  need \"Vein.Core\"\n  mark #Thing\n" + body + "\n}"));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        var sw = new StringWriter();
        new Interp { Ticks = ticks }.Run(r.Modules[0], new StringReader(""), sw);
        return sw.ToString();
    }

    /// Report every `#Thing` as `local/world` on the x axis.
    private const string Show =
        "  shard Show { settled { target $Position $World #Thing as t {\n" +
        "    emit *Vein.Console.Io.@Print { text: t.Position.x + \"/\" + t.World.x } } } }";

    [Fact]
    public void A_child_position_is_local_to_its_parent()
    {
        var outp = Run(
            "  shard Boot { run once {\n" +
            "    let p = spawn()\n    attach $Position to p { x: 10.0, y: 0.0, z: 0.0 }\n    mark p #Thing\n" +
            "    let c = spawn()\n    attach $Position to c { x: 2.0, y: 0.0, z: 0.0 }\n" +
            "    attach $Parent to c { of: p }\n    mark c #Thing\n  } }\n" + Show);

        Assert.Contains("10/10", outp);
        Assert.Contains("2/12", outp);      // the child's own value is still 2
    }

    [Fact]
    public void A_chain_composes_parents_first()
    {
        var outp = Run(
            "  shard Boot { run once {\n" +
            "    let a = spawn()\n    attach $Position to a { x: 10.0, y: 0.0, z: 0.0 }\n    mark a #Thing\n" +
            "    let b = spawn()\n    attach $Position to b { x: 2.0, y: 0.0, z: 0.0 }\n" +
            "    attach $Parent to b { of: a }\n    mark b #Thing\n" +
            "    let c = spawn()\n    attach $Position to c { x: 0.5, y: 0.0, z: 0.0 }\n" +
            "    attach $Parent to c { of: b }\n    mark c #Thing\n  } }\n" + Show);

        Assert.Contains("0.5/12.5", outp);
    }

    [Fact]
    public void An_unparented_identity_still_has_a_world_equal_to_its_position()
    {
        // The reason a kit can read `$World` unconditionally. Publishing it only for children would make
        // every consumer need a fallback, and a conditional migration is where the silent bugs live.
        Assert.Contains("7/7", Run(
            "  shard Boot { run once {\n" +
            "    let e = spawn()\n    attach $Position to e { x: 7.0, y: 0.0, z: 0.0 }\n    mark e #Thing\n  } }\n" + Show));
    }

    [Fact]
    public void A_child_follows_a_moving_parent_without_anything_writing_to_it()
    {
        // The whole point of the feature: only the parent is ever written.
        var outp = Run(
            "  mark #P\n" +
            "  shard Boot { run once {\n" +
            "    let p = spawn()\n    attach $Position to p { x: 0.0, y: 0.0, z: 0.0 }\n    mark p #P\n" +
            "    let c = spawn()\n    attach $Position to c { x: 2.0, y: 0.0, z: 0.0 }\n" +
            "    attach $Parent to c { of: p }\n    mark c #Thing\n  } }\n" +
            "  shard Drive { each tick { target $Position #P as p { p.Position.x = p.Position.x + 5.0 } } }\n" + Show,
            ticks: 3);

        var lines = outp.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();
        Assert.Equal(new[] { "2/7", "2/12", "2/17" }, lines);
    }

    [Fact]
    public void Composition_happens_before_settled_so_a_reader_is_never_a_frame_behind()
    {
        // The ordering that makes this a runtime job rather than a host one. Collision runs in `settled`
        // precisely because positions are written during the tick and reconciled at its end — composing
        // after it would be a one-frame lag, invisible at 60fps and wrong at every speed. The parent
        // moves to 5 on frame 1, so the child's FIRST reported world is 7, not 2.
        var outp = Run(
            "  mark #P\n" +
            "  shard Boot { run once {\n" +
            "    let p = spawn()\n    attach $Position to p { x: 0.0, y: 0.0, z: 0.0 }\n    mark p #P\n" +
            "    let c = spawn()\n    attach $Position to c { x: 2.0, y: 0.0, z: 0.0 }\n" +
            "    attach $Parent to c { of: p }\n    mark c #Thing\n  } }\n" +
            "  shard Drive { each tick { target $Position #P as p { p.Position.x = p.Position.x + 5.0 } } }\n" + Show,
            ticks: 1);

        Assert.Equal("2/7", outp.Trim());
    }

    [Fact]
    public void A_cycle_terminates_and_every_identity_in_it_keeps_its_own_position()
    {
        // Not merely detected — HANDLED. Breaking only at the point of detection left the rest of the
        // loop composed against a half-answer: `a` parented to `b` parented to `a` came out at 4 and 3
        // rather than at 1 and 2, which is a number that looks like an answer.
        var outp = Run(
            "  shard Boot { run once {\n" +
            "    let a = spawn()\n    let b = spawn()\n" +
            "    attach $Position to a { x: 1.0, y: 0.0, z: 0.0 }\n" +
            "    attach $Position to b { x: 2.0, y: 0.0, z: 0.0 }\n" +
            "    attach $Parent to a { of: b }\n    attach $Parent to b { of: a }\n" +
            "    mark a #Thing\n    mark b #Thing\n  } }\n" + Show);

        Assert.Contains("1/1", outp);
        Assert.Contains("2/2", outp);
    }

    [Fact]
    public void A_parent_that_was_destroyed_leaves_the_child_where_it_is()
    {
        // The total reading, and what the kit this replaces already did deliberately: a detached child
        // keeps its last position and stops being moved.
        var outp = Run(
            "  mark #P\n" +
            "  shard Boot { run once {\n" +
            "    let p = spawn()\n    attach $Position to p { x: 10.0, y: 0.0, z: 0.0 }\n    mark p #P\n" +
            "    let c = spawn()\n    attach $Position to c { x: 2.0, y: 0.0, z: 0.0 }\n" +
            "    attach $Parent to c { of: p }\n    mark c #Thing\n  } }\n" +
            "  shard Kill { each tick { target $Position #P as p { destroy p } } }\n" + Show,
            ticks: 2);

        // Frame 1 the parent is gone, so the child is its own 2 — not 12, and not zero.
        Assert.Contains("2/2", outp);
    }

    [Fact]
    public void A_program_with_no_hierarchy_is_untouched()
    {
        // The pass asks whether the module declares the shapes before doing any work, so a program with
        // no positions pays nothing for a feature it does not use.
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by me {\n  shape $N { v: int }\n  mark #N\n" +
            "  shard Boot { run once { let e = spawn()   attach $N to e { v: 3 }   mark e #N } }\n" +
            "  shard S { settled { target $N #N as n { emit *Vein.Console.Io.@Print { text: \"\" + n.N.v } } } }\n}"));

        var sw = new StringWriter();
        new Interp { Ticks = 1 }.Run(r.Modules[0], new StringReader(""), sw);

        Assert.Equal("3", sw.ToString().Trim());
    }
}
