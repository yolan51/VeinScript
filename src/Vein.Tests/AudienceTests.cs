using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// The `audience` barrier is the language's answer to "who may reach this handler": a `hear` states the
// shapes/marks an emitter must CARRY, and anything else is refused.
//
// It had no test coverage at all, and it stopped at the process boundary — a message off the console bus
// carries a bare address as its `from` rather than a structured emitter, so every requirement failed and
// an `audience` on `hear @Message` silently blocked everything. These cover both sides of that boundary.
[Collection(ConsoleRuntime.Name)]
public class AudienceTests
{
    private static IrModule Module(string body)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", "bundle T by me {\n" + body + "\n}"));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        return r.Modules[0];
    }

    private static string P(string expr) => "emit *Vein.Console.Io.@Print { text: " + expr + " }";

    // ---- in process: the emitter's carried shapes/marks ---------------------------------------

    /// Two shards emit the same event; only one carries what the listener demands.
    private const string Carriers = @"
  event @Boot { }
  event @Damage { amount: int }
  shard Trusted $Session #Trusted { hear @Boot as b { emit @Damage { amount: 5 } } }
  shard Sneaky                    { hear @Boot as b { emit @Damage { amount: 99 } } }
  start @Boot { }
";

    [Fact]
    public void A_carrier_passes_the_barrier_and_a_non_carrier_is_refused()
    {
        var sw = new StringWriter();
        new Interp().Run(Module(Carriers +
            "  shard Auditor { hear @Damage as d audience $Session #Trusted { " + P("\"saw \" + d.amount") + " } }"),
            new StringReader(""), sw);

        Assert.Equal("saw 5", sw.ToString().Trim());   // 99 never arrives
    }

    [Fact]
    public void With_no_audience_every_emitter_is_heard()
    {
        var sw = new StringWriter();
        new Interp().Run(Module(Carriers +
            "  shard Auditor { hear @Damage as d { " + P("\"saw \" + d.amount") + " } }"),
            new StringReader(""), sw);

        Assert.Equal(new[] { "saw 5", "saw 99" },
                     sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
    }

    [Fact]
    public void A_partial_match_is_still_refused()
    {
        // Trusted carries $Session #Trusted but not #Admin — "ALL required" means all.
        var sw = new StringWriter();
        new Interp().Run(Module(Carriers +
            "  shard Auditor { hear @Damage as d audience $Session #Admin { " + P("\"saw \" + d.amount") + " } }"),
            new StringReader(""), sw);

        Assert.Equal("", sw.ToString().Trim());
    }

    // ---- across the console bus: the sender's address -----------------------------------------

    /// A console listening for messages, optionally behind a barrier.
    private static string Listener(string audience) =>
        "  shard Ear { hear *Vein.Console.Io.@Message as m " + audience + "{ " + P("m.from + \": \" + m.text") + " } }";

    private static string Deliver(string source, params (string From, string Text)[] messages)
    {
        var sw = new StringWriter();
        var interp = new Interp();
        interp.Run(Module(source), new StringReader(""), sw);
        foreach (var (from, text) in messages) interp.Receive(from, text);
        return sw.ToString();
    }

    [Fact]
    public void An_audience_on_a_console_message_admits_only_that_address()
    {
        // The whole point: `audience #Alpha` = "only Alpha may reach this handler".
        var output = Deliver(Listener("audience #Alpha "), ("Alpha", "mine"), ("Beta", "not mine"));

        Assert.Contains("Alpha: mine", output);
        Assert.DoesNotContain("not mine", output);
    }

    [Fact]
    public void Without_an_audience_a_console_message_from_anyone_arrives()
    {
        // The regression that matters — this path is shared by every event in the language.
        var output = Deliver(Listener(""), ("Alpha", "one"), ("Beta", "two"));

        Assert.Contains("Alpha: one", output);
        Assert.Contains("Beta: two", output);
    }

    [Fact]
    public void From_still_reads_as_the_bare_address()
    {
        // `m.from` is concatenated and replied to all over the samples, so it must stay a plain string.
        var output = Deliver(Listener("audience #Alpha "), ("Alpha", "x"));

        Assert.Equal("Alpha: x", output.Trim());
    }

    [Fact]
    public void A_shape_requirement_refuses_every_console_message()
    {
        // Correct rather than broken: nothing tells the runtime what shapes a console holds, so it
        // carries only its address. Demanding a shape of a console can never be satisfied.
        var output = Deliver(Listener("audience $Session "), ("Alpha", "x"));

        Assert.Equal("", output.Trim());
    }
}
