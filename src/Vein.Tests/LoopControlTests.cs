using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// `break` and `continue`.
//
// THEY DID NOTHING. Not "were buggy" — did nothing at all. They lexed, parsed, lowered to IrBreak and
// IrContinue, and the C# backend emitted real `break;`/`continue;`; the interpreter's statement switch
// simply had no case for either and ran straight past. So the same program printed one thing
// interpreted and another compiled, for the entire life of the feature.
//
// Nothing caught it because no sample used either keyword, which is the whole argument for
// samples/loops.vein and for docs/SAMPLES.md. These tests are the second line: the sample proves the
// two runtimes agree, and these prove what they agree ON.
public class LoopControlTests
{
    private static string Run(string body, int ticks = 1)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by you {\n  shard S {\n    run once {\n" + body + "\n    }\n  }\n}"));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        var output = new StringWriter();
        new Interp { Ticks = ticks }.Run(r.Modules[0], new StringReader(""), output);
        return output.ToString().Replace("\r\n", "\n").Trim();
    }

    private const string Print = "      emit *Vein.Console.Io.@Print { text: ";

    // ---- while -------------------------------------------------------------------------------------

    [Fact]
    public void Break_leaves_a_while_loop()
    {
        Assert.Equal("1\n2", Run($$"""
                  var i = 0
                  while i < 10 {
                    i = i + 1
                    if i == 3 { break }
            {{Print}}"" + i }
                  }
            """));
    }

    [Fact]
    public void Continue_skips_the_rest_of_the_pass()
    {
        Assert.Equal("1\n3", Run($$"""
                  var i = 0
                  while i < 3 {
                    i = i + 1
                    if i == 2 { continue }
            {{Print}}"" + i }
                  }
            """));
    }

    [Fact]
    public void Continue_does_not_skip_the_condition_or_it_would_never_end()
    {
        // The loop still terminates: `continue` goes back to the test, it does not jump over the
        // increment that has already run. A `continue` placed before the increment would hang, which
        // is the author's problem and not this one's.
        Assert.Equal("done", Run($$"""
                  var i = 0
                  while i < 5 {
                    i = i + 1
                    continue
                  }
            {{Print}}"done" }
            """));
    }

    // ---- repeat ------------------------------------------------------------------------------------

    [Fact]
    public void Break_ends_a_counted_loop_early()
    {
        // The count is a limit, not a promise.
        Assert.Equal("0\n1", Run($$"""
                  repeat 10 as r {
                    if r == 2 { break }
            {{Print}}"" + r }
                  }
            """));
    }

    [Fact]
    public void Continue_works_in_a_counted_loop()
    {
        Assert.Equal("0\n2", Run($$"""
                  repeat 3 as r {
                    if r == 1 { continue }
            {{Print}}"" + r }
                  }
            """));
    }

    // ---- a collection loop -------------------------------------------------------------------------

    [Fact]
    public void Break_and_continue_work_over_a_list()
    {
        Assert.Equal("alpha\nbeta", Run($$"""
                  target words("alpha skip beta stop gamma") as w {
                    if w == "skip" { continue }
                    if w == "stop" { break }
            {{Print}}w }
                  }
            """));
    }

    // ---- an identity loop --------------------------------------------------------------------------

    [Fact]
    public void Continue_in_an_identity_loop_leaves_the_store_usable()
    {
        // THE ONE THAT COULD CORRUPT SOMETHING. Each entity in a `target` gets its own activation, and
        // a `continue` that skipped EndActivation would leave the store mid-activation for every shard
        // that ran after it in the frame. The second shard reporting correctly is the actual assertion;
        // the first one's output only proves the skip happened.
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", """
            bundle T by you {
                shape $Score { points: int }
                mark #P
                builder Unit { $Score   mark #P }

                shard Make { run once { bring Unit(5)  bring Unit(15)  bring Unit(25) } }

                shard Skip {
                    each tick {
                        target $Score #P as u {
                            if u.Score.points < 10 { continue }
                            emit *Vein.Console.Io.@Print { text: "kept " + u.Score.points }
                        }
                    }
                }

                shard After {
                    each tick {
                        target $Score #P as u {
                            emit *Vein.Console.Io.@Print { text: "saw " + u.Score.points }
                        }
                    }
                }
            }
            """));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        var output = new StringWriter();
        new Interp { Ticks = 2 }.Run(r.Modules[0], new StringReader(""), output);
        string text = output.ToString();

        Assert.Contains("kept 15", text);
        Assert.Contains("kept 25", text);
        Assert.DoesNotContain("kept 5", text);

        // Every entity still visible to the shard that runs afterwards.
        Assert.Contains("saw 5", text);
        Assert.Contains("saw 15", text);
        Assert.Contains("saw 25", text);
    }

    [Fact]
    public void Break_in_an_identity_loop_leaves_the_store_usable()
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", """
            bundle T by you {
                shape $Score { points: int }
                mark #P
                builder Unit { $Score   mark #P }

                shard Make { run once { bring Unit(1)  bring Unit(2)  bring Unit(3) } }
                shard Stop { each tick { target $Score #P as u { break } } }

                shard After {
                    each tick {
                        target $Score #P as u {
                            emit *Vein.Console.Io.@Print { text: "saw " + u.Score.points }
                        }
                    }
                }
            }
            """));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        var output = new StringWriter();
        new Interp { Ticks = 2 }.Run(r.Modules[0], new StringReader(""), output);
        string text = output.ToString();

        Assert.Contains("saw 1", text);
        Assert.Contains("saw 2", text);
        Assert.Contains("saw 3", text);
    }

    // ---- outside a loop ----------------------------------------------------------------------------

    [Fact]
    public void A_break_outside_any_loop_ends_the_block_rather_than_failing()
    {
        // It means nothing there. Ending the block is the same answer a stray `return` already gets,
        // and reporting a runtime error would turn a harmless mistake into a failed frame.
        Assert.Equal("before", Run($$"""
            {{Print}}"before" }
                  break
            {{Print}}"after" }
            """));
    }

    [Fact]
    public void Only_the_innermost_loop_is_affected()
    {
        // `break` binds to the nearest loop, as it does everywhere else. Without this the outer loop
        // would stop on the first inner break and the difference would look like an off-by-one.
        Assert.Equal("0-0\n1-0\n2-0", Run($$"""
                  repeat 3 as a {
                    repeat 3 as b {
                      if b == 1 { break }
            {{Print}}"" + a + "-" + b }
                    }
                  }
            """));
    }
}
