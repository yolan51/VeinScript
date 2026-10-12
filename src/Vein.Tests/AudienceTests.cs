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

    // ---- D19: `not` excludes, and `!Shard` names the sender ------------------------------------

    /// Three shards emit @Test; listeners filter them by NAME. Ledger is declared first, so if a
    /// barrier let the wrong one through, the first line would say so.
    private const string Senders = @"
  event @Test { n: int }
  shard Ledger  { run once { emit @Test { n: 1 } } }
  shard Billing { run once { emit @Test { n: 2 } } }
  shard Other   { run once { emit @Test { n: 3 } } }
";

    private static string[] RunLines(string body)
    {
        var sw = new StringWriter();
        new Interp().Run(Module(body), new StringReader(""), sw);
        return sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();
    }

    [Fact]
    public void Not_a_shard_blocks_what_that_shard_emits()
    {
        var output = RunLines(Senders +
            "  shard Ear { hear @Test as t audience not !Ledger not !Billing { " + P("\"saw \" + t.n") + " } }");

        Assert.Equal(new[] { "saw 3" }, output);
    }

    [Fact]
    public void A_named_shard_is_the_only_sender_admitted()
    {
        var output = RunLines(Senders +
            "  shard Ear { hear @Test as t audience !Ledger !Other { " + P("\"saw \" + t.n") + " } }");

        // Any ONE of the named shards: a sender is one shard, so "all of them" could never pass.
        Assert.Equal(new[] { "saw 1", "saw 3" }, output);
    }

    [Fact]
    public void Not_works_on_a_mark_the_sender_carries()
    {
        var output = RunLines(Carriers +
            "  shard Auditor { hear @Damage as d audience not #Trusted { " + P("\"saw \" + d.amount") + " } }");

        Assert.Equal(new[] { "saw 99" }, output);   // the carrier of #Trusted is the one kept out
    }

    [Fact]
    public void Not_works_on_a_shape_and_mixes_with_a_required_entry()
    {
        var output = RunLines(Carriers +
            "  shard Auditor { hear @Damage as d audience #Trusted not $Session { " + P("\"saw \" + d.amount") + " } }");

        // Trusted carries #Trusted but also $Session; Sneaky carries neither. Nobody gets through.
        Assert.Empty(output);
    }

    [Fact]
    public void A_runtime_sender_is_no_shard()
    {
        // @Boot comes from the runtime, which has no shard key: an excluded shard cannot match it, and a
        // required one refuses it.
        var output = RunLines(
            "  event @Boot { }\n  shard Ledger { }\n" +
            "  shard A { hear @Boot as b audience not !Ledger { " + P("\"a heard\"") + " } }\n" +
            "  shard B { hear @Boot as b audience !Ledger { " + P("\"b heard\"") + " } }\n" +
            "  start @Boot { }");

        Assert.Equal(new[] { "a heard" }, output);
    }

    [Fact]
    public void Not_on_a_console_address_blocks_that_console()
    {
        var output = Deliver(Listener("audience not #Alpha "), ("Alpha", "x"), ("Beta", "y"));

        Assert.Equal("Beta: y", output.Trim());
    }

    private static IReadOnlyList<Vein.Compiler.Diagnostics.Diagnostic> Errors(string body) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", "bundle T by me {\n" + body + "\n}"))
            .Diagnostics.Where(d => d.Severity == Vein.Compiler.Diagnostics.Severity.Error).ToList();

    [Fact]
    public void An_unknown_shard_in_an_audience_is_VS0352_with_a_suggestion()
    {
        var d = Assert.Single(Errors(Senders + "  shard Ear { hear @Test as t audience not !Ledgr { } }"));

        Assert.Equal("VS0352", d.Code);
        Assert.Contains("Did you mean 'Ledger'?", d.Message);
    }

    [Fact]
    public void A_shard_path_with_a_publicator_in_it_is_VS0352()
    {
        var d = Assert.Single(Errors(Senders + "  shard Ear { hear @Test as t audience not *a.B.Pub.!X { } }"));

        Assert.Equal("VS0352", d.Code);
        Assert.Contains("never inside a publicator", d.Message);
    }

    [Fact]
    public void Not_with_nothing_after_it_says_what_it_wanted()
    {
        var d = Assert.Single(Errors(Senders + "  shard Ear { hear @Test as t audience not { } }"));

        Assert.Equal("VS0100", d.Code);
        Assert.Contains("!Shard after `not`", d.Message);
    }

    [Fact]
    public void Another_bundles_shard_is_named_by_its_key_unchecked_until_linking()
    {
        var m = Module(Senders + "  shard Ear { hear @Test as t audience not *you.Billing.!Invoicer !Ledger { } }");

        var aud = m.Shards.Single(s => s.Name == "Ear").Methods[0].Attrs.Single(a => a.Name == "audience");
        Assert.Equal(new[] { "me.T.Ledger" }, (IReadOnlyList<string>)aud.Args[4]!);
        Assert.Equal(new[] { "you.Billing.Invoicer" }, (IReadOnlyList<string>)aud.Args[5]!);
    }

    [Fact]
    public void A_bare_shard_in_an_audience_is_a_reference_for_F12()
    {
        var unit = new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by me {\n" + Senders + "  shard Ear { hear @Test as t audience not !Ledger { } }\n}")).Ast!;
        var ix = Vein.Compiler.Tooling.DefinitionIndex.Analyze(unit);

        Assert.Equal(2, ix.All("Ledger", Vein.Compiler.Project.SymbolKind.Shard).Count);
    }

    [Fact]
    public void The_C_sharp_backend_says_it_does_not_enforce_an_audience()
    {
        var result = new Vein.Compiler.Backends.CSharpBackend().Emit(Module(Senders +
            "  shard Ear { hear @Test as t audience not !Ledger { } }"));

        Assert.Contains(result.Notes, n => n.Contains("`audience` not enforced"));
    }
}
