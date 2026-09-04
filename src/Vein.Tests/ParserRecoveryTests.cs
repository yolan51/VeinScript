using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// A compiler may reject a file; it may not hang on one. These pin the progress guarantee in the three
// error-recovery loops — the top-level one had none, and a nine-line file was enough to spin it until
// the process died on memory rather than on the error it had already found.
public class ParserRecoveryTests
{
    /// Compiles with a hard stop. A hang is the failure being tested for, so it cannot be waited on.
    private static (bool Finished, int Errors) Compile(string source)
    {
        var task = Task.Run(() => new VeinCompilerService().Compile(new CompileRequest("t.vein", source)));
        bool finished = task.Wait(TimeSpan.FromSeconds(20));
        return (finished, finished ? task.Result.Diagnostics.Count : 0);
    }

    [Fact]
    public void A_parse_error_followed_by_another_shard_terminates()
    {
        // The exact reproduction. `count` is a reserved keyword, so `var count: int` fails inside shard
        // A; recovery leaves `shard B` at TOP level, where it is neither `bundle` nor `app`. Synchronize
        // returns without advancing on a declaration keyword, so VS0101 was reported against that same
        // token forever.
        var (finished, errors) = Compile("""
            bundle T by you {
                shard A {
                    var count: int
                    run once { count = 0 }
                }
                shard B {
                    run once { emit *Vein.Console.Io.@Print { text: "b" } }
                }
            }
            """);

        Assert.True(finished, "the parser hung on a file it had already found an error in");
        Assert.True(errors > 0, "a rejected file should say why");
    }

    [Fact]
    public void A_stray_declaration_keyword_at_top_level_terminates()
    {
        // The shape of the bug without the shard around it: a declaration keyword where a `bundle` was
        // expected is both a VS0101 and a Synchronize stopping point, which is what closed the loop.
        var (finished, _) = Compile("shape $Loose { n: int }");

        Assert.True(finished, "a declaration outside a bundle must be reported, not spun on");
    }

    [Theory]
    [InlineData("bundle T by you { shard A { var count: int } shard B { } shard C { } }")]
    [InlineData("shard A { }")]
    [InlineData("publicator P { }")]
    [InlineData("bundle T by you { shape $X { n: int } } shape $Y { n: int }")]
    [InlineData("}}}")]
    [InlineData("bundle")]
    [InlineData("bundle T by you {")]
    public void Malformed_input_always_terminates(string source)
    {
        // Half-written code is the normal state while typing, and the Workbench now compiles after every
        // pause in typing — so any input that spins the parser freezes the editor rather than merely
        // failing a build.
        Assert.True(Compile(source).Finished, $"the parser hung on: {source}");
    }

    [Fact]
    public void A_rejected_file_does_not_report_the_same_position_forever()
    {
        // The symptom that made the hang expensive rather than merely annoying: each spin appended a
        // diagnostic, so the run died on memory. A bounded count is the observable form of progress.
        var (finished, errors) = Compile("""
            bundle T by you {
                shard A {
                    var count: int
                }
                shard B { }
            }
            """);

        Assert.True(finished);
        Assert.True(errors < 100, $"expected a handful of diagnostics, got {errors}");
    }
}
