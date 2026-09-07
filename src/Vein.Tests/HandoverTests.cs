using Vein.Compiler.Backends;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// The game editor's second handover, found by building things with the editor rather than by reading
// the compiler. Each entry arrived with a reproduction that failed; each test here is that reproduction.
public class HandoverTests
{
    private static CompilationResult Compile(string src) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", src));

    private static IrModule Module(string src)
    {
        var r = Compile(src);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        return r.Entry!;
    }

    // ---- B. `!=` — deliberate, and now the lexer says so ---------------------------------------------
    //
    // SYNTAX-DECISIONS D12: VeinScript has no `!=`, no `!x`, and no `!` token. Logic is spelled in words.
    // That was the decision; the DIAGNOSTIC was the bug. `x != 0` produced "Unexpected character '!'"
    // and then, because the `=` was left for the parser, "Expected '{'" — two errors pointing at a brace,
    // neither saying the language has no `!=`.

    [Fact]
    public void Not_equals_is_one_error_that_names_the_spelling()
    {
        var diags = Compile("bundle T by you { shard S { run once { let x = 1.0\n if x != 0.0 { } } } }").Diagnostics;

        var d = Assert.Single(diags, x => x.Code == "VS0006");
        Assert.Contains("not (a == b)", d.Message);
        // ONE error, not three. The lexer hands the parser the token the author meant, so there is no
        // "Expected '{'" from the leftover `=` and no "Expected 'bundle'" from the unbalanced brace
        // after it — the exact cascade that pointed everyone at the wrong line.
        Assert.Single(diags);
    }

    [Fact]
    public void Bang_alone_names_not()
    {
        var d = Assert.Single(Compile("bundle T by you { shard S { run once { let x = true\n if !x { } } } }").Diagnostics,
                              x => x.Code == "VS0006");
        Assert.Contains("not x", d.Message);
    }

    [Fact]
    public void The_spelling_the_message_names_actually_works()
    {
        var output = new StringWriter();
        new Interp().Run(Module("""
            bundle T by you {
                shard S { run once { let x = 1.0
                    if not (x == 0.0) { emit *Vein.Console.Io.@Print { text: "not works" } } } }
            }
            """), new StringReader(""), output);

        Assert.Contains("not works", output.ToString());
    }

    // ---- C. A host entry point for @Ticked -----------------------------------------------------------

    [Fact]
    public void A_host_can_deliver_the_clock()
    {
        // stdlib/Time.vein: "a runtime emits @Ticked when appropriate". The six Fire* methods were
        // keyboard and mouse only, so a program hearing the clock compiled, ran, and heard nothing.
        var output = new StringWriter();
        var interp = new Interp { Output = output };
        interp.Boot(Module("""
            bundle T by you {
                shard Physics {
                    hear *Vein.Time.Clock.@Ticked as t {
                        emit *Vein.Console.Io.@Print { text: "frame " + t.frame + " dt " + t.delta }
                    }
                }
            }
            """));

        interp.FireTicked(7, 0.016);

        Assert.Contains("frame 7 dt 0.016", output.ToString());
    }

    // ---- D. Audio ----------------------------------------------------------------------------------
    //
    // A sound is the one stdlib effect the interpreter cannot perform itself. So it is TRANSPORT — in
    // HostEvents beside @Print and @Fetch — and it reaches whatever host has a speaker through a seam
    // in the shape NetHttp.Hook already uses.

    [Fact]
    public void PlaySound_is_transport_not_data()
    {
        // The half that is easy to miss, and the one HostEvents' own comment warns about: an event
        // added to Drain and not to this set is quietly compiled into a no-op — a mute game.
        Assert.Contains("PlaySound", Interp.HostEvents);
    }

    [Fact]
    public void A_host_with_a_speaker_receives_the_sound()
    {
        var played = new List<(string, double)>();
        AudioOut.Hook = (src, vol) => played.Add((src, vol));
        try
        {
            new Interp { Output = TextWriter.Null }.Boot(Module("""
                bundle T by you {
                    shard S { run once { emit *Vein.Audio.Sound.@PlaySound { source: "hit.wav", volume: 0.5 } } }
                }
                """));
        }
        finally { AudioOut.Hook = null; }

        var (source, volume) = Assert.Single(played);
        Assert.Equal("hit.wav", source);
        Assert.Equal(0.5, volume);
    }

    [Fact]
    public void Volume_defaults_to_full_and_is_clamped()
    {
        var played = new List<(string, double)>();
        AudioOut.Hook = (src, vol) => played.Add((src, vol));
        try
        {
            new Interp { Output = TextWriter.Null }.Boot(Module("""
                bundle T by you {
                    shard S { run once {
                        emit *Vein.Audio.Sound.@PlaySound { source: "a.wav" }
                        emit *Vein.Audio.Sound.@PlaySound { source: "b.wav", volume: 9.0 } } }
                }
                """));
        }
        finally { AudioOut.Hook = null; }

        Assert.Equal(1.0, played[0].Item2);
        Assert.Equal(1.0, played[1].Item2);
    }

    [Fact]
    public void With_no_host_the_sound_is_dropped_and_recorded()
    {
        // Honest for a console: `veinc run` in a terminal has no speaker. Recorded rather than lost,
        // so a test — or a host — can see that the program asked.
        AudioOut.Hook = null;
        AudioOut.Dropped.Clear();

        new Interp { Output = TextWriter.Null }.Boot(Module("""
            bundle T by you {
                shard S { run once { emit *Vein.Audio.Sound.@PlaySound { source: "lost.wav" } } }
            }
            """));

        Assert.Contains(AudioOut.Dropped, d => d.Source == "lost.wav");
    }

    [Fact]
    public void The_backend_refuses_PlaySound_with_the_transport_note()
    {
        // The difference between "silent in a terminal" and "silent once built": the first is honest,
        // the second would be a game that compiles, runs and never makes a sound.
        var module = Module("""
            bundle T by you {
                shard S { run once { emit *Vein.Audio.Sound.@PlaySound { source: "x.wav" } } }
            }
            """);
        var emitted = new CSharpBackend().Emit(module);

        Assert.Contains(emitted.Notes, n => n.Contains("@PlaySound") && n.Contains("transport lives on the interpreter"));
    }

    // ---- A shape cannot include a shape — now said, not discovered -----------------------------------

    [Theory]
    [InlineData("shape $Ticking { *Vein.Time.Clock.$Clock }")]
    [InlineData("shape $Ticking { $Other }")]
    public void An_include_inside_a_shape_names_the_rule_and_the_fix(string decl)
    {
        // RULES 15b was the rule; the parse error for breaking it said "Expected field name" and left
        // the author to try the other spelling. Builders and events CAN include, which is what made it
        // look reasonable.
        var diags = Compile($"bundle T by you {{ shape $Other {{ n: int }}\n {decl}\n shard S {{ run once {{ }} }} }}").Diagnostics;

        var d = Assert.Single(diags, x => x.Code == "VS0007");
        Assert.Contains("cannot include a shape", d.Message);
        Assert.Contains("builder", d.Message);
    }

    [Fact]
    public void A_shape_with_only_fields_is_untouched()
    {
        Assert.True(Compile("bundle T by you { shape $H { hp: int, max: int }\n shard S { run once { } } }").Success);
    }

    // ---- `not x == y` — the trap D12 creates, and the warning that closes it ----------------------
    //
    // `not` takes a UNARY operand, so `not x == y` is `(not x) == y`. For a number the two readings
    // agree by accident — `(not a) == 0` collapses to "is a truthy", which is what `not (a == 0)` means
    // too — so the mistake is invisible exactly until the operand is a string: `not name == ""`
    // negates the string, compares the bool to empty text, and is FALSE for every input.

    [Fact]
    public void Not_before_a_comparison_warns_and_names_the_fix()
    {
        var r = Compile("bundle T by you { shard S { run once { let name = \"Ada\"\n if not name == \"\" { } } } }");

        var w = Assert.Single(r.Diagnostics, d => d.Code == "VS0008");
        Assert.Equal(Severity.Warning, w.Severity);
        Assert.Contains("not (x == y)", w.Message);
        Assert.True(r.Success, "a warning, not an error — the parse is legal and a program that meant it stays a program");
    }

    [Fact]
    public void The_parenthesised_form_does_not_warn()
    {
        var r = Compile("bundle T by you { shard S { run once { let name = \"Ada\"\n if not (name == \"\") { } } } }");
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "VS0008");
    }

    [Fact]
    public void Every_comparison_operator_is_covered()
    {
        // The trap is not specific to `==`: `(not a) < 3` is just as meaningless.
        var r = Compile("bundle T by you { shard S { run once { let a = 5\n if not a < 3 { } } } }");
        var w = Assert.Single(r.Diagnostics, d => d.Code == "VS0008");
        Assert.Contains("not (x < y)", w.Message);
    }

    [Fact]
    public void The_warning_describes_a_real_wrong_answer()
    {
        // The runtime proof that this is worth a diagnostic: the two spellings disagree on a string.
        var output = new StringWriter();
        new Interp().Run(Module("""
            bundle T by you {
                shard S { run once { let name = "Ada"
                    if not (name == "") { emit *Vein.Console.Io.@Print { text: "A yes" } }
                    if not name == ""   { emit *Vein.Console.Io.@Print { text: "B yes" } } } }
            }
            """), new StringReader(""), output);

        Assert.Contains("A yes", output.ToString());
        Assert.DoesNotContain("B yes", output.ToString());
    }
}
