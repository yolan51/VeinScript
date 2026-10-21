using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// What VeinScript has instead of try/catch: a fault becomes an EVENT, and `hear` is the catch.
//
// This is the idiom Vein.Net already used — @Fetch answered by @Failed, @Send by @Undelivered — applied
// to the one thing that had no answer: a unit of work that threw. There is no block to encircle, because
// `bring` and `emit` cannot fail where they are written.
[Collection(ConsoleRuntime.Name)]
public class DiagnosticsTests
{
    private static string Run(string body, int ticks = 1)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", "bundle T by me {\n" + body + "\n}"));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var sw = new StringWriter();
        new Interp { Ticks = ticks }.Run(r.Modules[0], new StringReader(""), sw);
        return sw.ToString();
    }

    private static string P(string expr) => "emit *Vein.Console.Io.@Print { text: " + expr + " }";

    /// `% 0` is the one thing reachable from VeinScript that genuinely throws. Almost nothing else does:
    /// the interpreter reads an out-of-range index as null and stops runaway recursion by returning null,
    /// on the stated principle that a runtime is not a place to throw.
    private const string Throws = "(5 % 0)";

    // ---- a fault becomes a diagnostic ---------------------------------------------------------------

    [Fact]
    public void A_fault_names_the_shard_and_the_phase_it_died_in()
    {
        // "something threw" is useless; "which unit of work died" is what a person needs. The message
        // has to survive being read at 3am with no debugger.
        var output = Run("  shard Compute { run once { " + P(Throws) + " } }");

        Assert.Contains("Compute (once)", output);
        Assert.Contains("divide by zero", output);
    }

    [Fact]
    public void The_faulting_block_stops_but_its_siblings_still_run()
    {
        // The unit is the BLOCK, not the statement. Resuming mid-block would leave the world in a state
        // no source line describes — but one broken shard must not take the program with it.
        var output = Run(
            "  shard A { run once {\n" +
            "    " + P("\"A before\"") + "\n" +
            "    " + P(Throws) + "\n" +
            "    " + P("\"A after\"") + " } }\n" +
            "  shard B { run once { " + P("\"B ran\"") + " } }");

        Assert.Contains("A before", output);
        Assert.DoesNotContain("A after", output);
        Assert.Contains("B ran", output);
    }

    [Fact]
    public void A_shard_can_hear_the_fault_and_that_is_the_catch()
    {
        var output = Run(
            "  shard Compute { run once { " + P(Throws) + " } }\n" +
            "  shard Catcher { hear *Vein.Diagnostics.Report.@DiagnosticRaised as d {\n" +
            "    " + P("\"caught[\" + d.severity + \"]: \" + d.message") + " } }");

        // Severity 2 = error, per stdlib/Diagnostics.vein's documented codes.
        Assert.Contains("caught[2]: Compute (once)", output);
    }

    [Fact]
    public void A_fault_in_a_hear_handler_is_reported_too()
    {
        var output = Run(
            "  event @Go { }\n" +
            "  shard Kick { run once { emit @Go { } } }\n" +
            "  shard Handler { hear @Go as g { " + P(Throws) + " } }");

        Assert.Contains("Handler (hear @Go)", output);
    }

    // ---- a diagnostic is never dropped --------------------------------------------------------------

    [Fact]
    public void warn_reaches_the_console_when_no_shard_collects_it()
    {
        // The bug this closes. `*Vein.Diagnostics.Report.warn(…)` emitted @DiagnosticRaised into a queue
        // nobody read, so in any program without a collector shard it printed NOTHING — a diagnostics
        // library silently losing its own diagnostic, which is the one thing it must never do.
        var output = Run("  shard S { run once { do *Vein.Diagnostics.Report.warn(\"look at this\") } }");

        Assert.Contains("warning", output);
        Assert.Contains("look at this", output);
    }

    [Fact]
    public void The_three_severities_are_named_not_numbered_on_the_console()
    {
        var output = Run(
            "  shard S { run once {\n" +
            "    do *Vein.Diagnostics.Report.info(\"eye\")\n" +
            "    do *Vein.Diagnostics.Report.warn(\"ear\")\n" +
            "    do *Vein.Diagnostics.Report.error(\"nose\") } }");

        // …and each says WHERE: the shard, the unit and the line it starts on.
        Assert.Contains("(info: eye — S (once) at t.vein:2)", output);
        Assert.Contains("(warning: ear — S (once) at t.vein:2)", output);
        Assert.Contains("(error: nose — S (once) at t.vein:2)", output);
    }

    [Fact]
    public void A_collector_takes_over_from_the_console_rather_than_doubling_it()
    {
        // The console is a FALLBACK, not a default. A program that collects diagnostics has said where
        // it wants them, and printing as well would be the library overriding that.
        var output = Run(
            "  shard S { run once { do *Vein.Diagnostics.Report.warn(\"once only\") } }\n" +
            "  shard C { hear *Vein.Diagnostics.Report.@DiagnosticRaised as d {\n" +
            "    " + P("\"mine: \" + d.message") + " } }");

        Assert.Contains("mine: once only", output);
        Assert.DoesNotContain("(warning:", output);
    }

    [Fact]
    public void A_fault_inside_the_collector_does_not_loop()
    {
        // A @DiagnosticRaised handler that throws cannot raise another @DiagnosticRaised — that is a
        // cycle the queue guard would only end after 10,000 iterations, with the original cause buried.
        var output = Run(
            "  shard S { run once { do *Vein.Diagnostics.Report.warn(\"first\") } }\n" +
            "  shard C { hear *Vein.Diagnostics.Report.@DiagnosticRaised as d { " + P(Throws) + " } }");

        Assert.Contains("while reporting", output);
        // One report of the broken collector, not a queue full of them.
        Assert.Equal(1, output.Split("while reporting").Length - 1);
    }

    // ---- what a WRONG ARGUMENT does, which is not this ----------------------------------------------
    //
    // A fault raises @DiagnosticRaised. A bad argument does not, because it does not fault — and that
    // is the question people arrive with. These pin the behaviour as it actually is, so that a future
    // check cannot be added without someone noticing these and updating them deliberately.

    private static IReadOnlyList<Vein.Compiler.Diagnostics.Diagnostic> Diagnose(string body) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", "bundle T by me {\n" + body + "\n}")).Diagnostics;

    private const string RowDecl =
        "  shape $Row { title: string, rank: int }\n  mark #Row\n  builder Row { $Row   mark #Row }\n";

    [Fact]
    public void A_bring_with_too_many_arguments_is_a_compile_error()
    {
        // The one arity mistake anything catches — and it is caught at the right time, before the
        // program runs, which is better than any runtime event could manage.
        var d = Diagnose(RowDecl + "  shard S { run once { bring Row(\"a\", 1, 99, \"x\") } }");

        Assert.Contains(d, x => x.Code == "VS0204");
    }

    [Fact]
    public void A_bring_with_too_few_arguments_is_VS0228_and_the_field_is_still_empty()
    {
        // Reported now, but the RUNTIME is unchanged and that half still matters: a warning does not
        // stop the program, so the row is built with an empty rank either way. The check tells you
        // before it runs; it does not rescue you afterwards.
        var d = Diagnose(RowDecl + "  shard S { run once { bring Row(\"only-title\") } }");
        Assert.Contains(d, x => x.Code == "VS0228");

        var output = Run(RowDecl +
            "  shard S { run once { bring Row(\"only-title\") } }\n" +
            "  shard R { settled { target $Row #Row as r { " + P("\"rank=[\" + r.Row.rank + \"]\"") + " } } }");

        Assert.Contains("rank=[]", output);
    }

    [Fact]
    public void Leaving_a_field_empty_on_purpose_is_not_reported()
    {
        // Writing the empty value is the author saying "I am knowingly leaving this empty", and a check
        // that fired here would punish the one spelling that states the intent. (A saved `?` used to say
        // it; `?` is now the Workbench's shortcut and writes exactly this.)
        var d = Diagnose(RowDecl + "  shard S { run once { bring Row(\"only-title\", 0) } }");

        Assert.DoesNotContain(d, x => x.Severity == Vein.Compiler.Diagnostics.Severity.Error);
        Assert.DoesNotContain(d, x => x.Code == "VS0228");
    }

    [Theory]
    [InlineData("bring Row(\"only-title\", ?)")]
    [InlineData("bring Row ?")]
    [InlineData("emit @Order ?")]
    [InlineData("emit @Order { item: \"x\", ? }")]
    public void A_question_mark_saved_in_a_file_is_VS0347_at_the_question_mark(string statement)
    {
        // `?` is the Workbench's "show me the signature" key. Saved, it used to be a silent fill-with-zeros —
        // hiding exactly the fields `?` exists to show. Now it is an error whose Quick Fix writes them.
        var d = Diagnose(RowDecl + "  event @Order { item: string, qty: int = 1 }\n" +
                         "  shard S { run once { " + statement + " } }");

        var e = Assert.Single(d, x => x.Code == "VS0347");
        Assert.Equal(Vein.Compiler.Diagnostics.Severity.Error, e.Severity);
        Assert.Equal(statement.IndexOf('?') + "  shard S { run once { ".Length + 1, e.Span.Col);
    }

    [Fact]
    public void Retired_fill_rest_keeps_the_old_line_honest()
    {
        // The VS0228 check is unchanged by the retirement: leaving a required field out is still reported.
        var d = Diagnose(RowDecl + "  shard S { run once { bring Row(\"only-title\") } }");
        Assert.Contains(d, x => x.Code == "VS0228");
    }

    // ---- `base` — "the value this parameter's declaration gave it" ---------------------------------

    [Fact]
    public void base_takes_the_declared_default_for_that_slot()
    {
        // Why it exists: binding is positional with no named arguments, so reaching `accent` meant
        // retyping `body` — copying a declared default into a call site, where it can drift from the
        // declaration it came from.
        var output = Run(
            "  shape $C { title: string, body: string = \"(none)\" }\n" +
            "  shape $S { cls: string = \"panel\", accent: string = \"#00f\" }\n  mark #P\n" +
            "  builder P { $C $S   mark #P }\n" +
            "  shard S { run once { bring P(\"Danger\", base, \"warn\", \"#f00\") } }\n" +
            "  shard R { settled { target $C #P as p {\n" +
            "    " + P("\"body=[\" + p.C.body + \"]\"") + " } } }");

        Assert.Contains("body=[(none)]", output);
    }

    [Fact]
    public void base_fills_a_slot_and_does_not_skip_one()
    {
        // The distinction that keeps it honest: `base` is an ARGUMENT, so the count still has to match.
        // Three params, two arguments — one of them `base` — is still two arguments.
        var d = Diagnose(
            "  shape $C { a: string, b: string = \"-\", c: string }\n  mark #P\n" +
            "  builder P { $C   mark #P }\n" +
            "  shard S { run once { bring P(\"x\", base) } }");

        Assert.Contains(d, x => x.Code == "VS0228");
    }

    [Fact]
    public void base_on_a_parameter_with_no_default_is_VS0231()
    {
        // `base` is a claim about the DECLARATION — that there is a default to take. When there is not,
        // the author has the wrong picture of the signature, so it says so instead of quietly zeroing.
        var d = Diagnose(RowDecl + "  shard S { run once { bring Row(\"t\", base) } }");

        var hit = Assert.Single(d, x => x.Code == "VS0231");
        Assert.Contains("'rank' has no default", hit.Message);
    }

    [Fact]
    public void base_is_a_bring_argument_and_nothing_else()
    {
        // There is nothing it could evaluate to elsewhere: what it means is decided by the parameter it
        // lands on, and outside an argument list there is no parameter.
        var d = Diagnose("  shard S { run once { let x = base } }");

        Assert.Contains(d, x => x.Severity == Vein.Compiler.Diagnostics.Severity.Error);
    }

    [Fact]
    public void base_is_still_legal_as_a_field_and_member_name()
    {
        // It is a keyword in EXPRESSION position only — the same treatment `from`, `count` and `to`
        // get. stdlib/Rest.vein's `$Connection { base: string }` depends on this, and so does every
        // program that reads `c.Connection.base`.
        var output = Run(
            "  shape $Conn { base: string, key: string }\n  mark #C\n" +
            "  builder Conn { $Conn   mark #C }\n" +
            "  shard S { run once { bring Conn(\"https://x.test\", \"k\") } }\n" +
            "  shard R { settled { target $Conn #C as c { " + P("\"base=\" + c.Conn.base") + " } } }");

        Assert.Contains("base=https://x.test", output);
    }

    [Fact]
    public void A_default_in_the_MIDDLE_is_still_a_required_slot()
    {
        // Arguments bind positionally and there are no named arguments, so nothing can skip past a
        // default to reach what follows it. `{ x, y = 99, z }` is three required slots: `bring T(1, 2)`
        // puts 2 into `y` and leaves `z` empty.
        //
        // The first version of this check counted defaulted params instead of finding the last required
        // one, so it made exactly this case pass — samples/builder_defaults.vein caught it.
        var d = Diagnose(
            "  shape $A { x: int, y: int = 99 }\n  shape $B { z: int }\n  mark #T\n" +
            "  builder T { $A $B   mark #T }\n" +
            "  shard S { run once { bring T(1, 2) } }");

        var hit = Assert.Single(d, x => x.Code == "VS0228");
        Assert.Contains("needs 3", hit.Message);
        Assert.Contains("z will be empty", hit.Message);
    }

    [Fact]
    public void A_trailing_run_of_defaults_is_what_actually_makes_a_call_shorter()
    {
        // The layout that works, and the one the sample is built around: everything optional last.
        var d = Diagnose(
            "  shape $C { title: string, body: string = \"-\" }\n" +
            "  shape $S { cls: string = \"panel\", accent: string = \"#00f\" }\n  mark #P\n" +
            "  builder P { $C $S   mark #P }\n" +
            "  shard S { run once { bring P(\"just a title\") } }");

        Assert.DoesNotContain(d, x => x.Code == "VS0228");
    }

    [Fact]
    public void A_parameter_with_a_default_is_optional_and_not_reported()
    {
        var d = Diagnose(
            "  shape $Card { title: string, note: string = \"\" }\n  mark #Card\n" +
            "  builder Card { $Card   mark #Card }\n" +
            "  shard S { run once { bring Card(\"just a title\") } }");

        Assert.DoesNotContain(d, x => x.Code == "VS0228");
    }

    [Fact]
    public void A_bring_with_swapped_literal_types_is_VS0230()
    {
        var d = Diagnose(RowDecl + "  shard S { run once { bring Row(42, \"not-a-number\") } }");

        // Both arguments are wrong, and both are named — reporting only the first would leave the
        // second to be discovered on the next compile.
        Assert.Equal(2, d.Count(x => x.Code == "VS0230"));

        // And the value still lands exactly as written: nothing converts it.
        var output = Run(RowDecl +
            "  shard S { run once { bring Row(42, \"not-a-number\") } }\n" +
            "  shard R { settled { target $Row #Row as r {\n" +
            "    " + P("\"title=[\" + r.Row.title + \"] rank=[\" + r.Row.rank + \"]\"") + " } } }");

        Assert.Contains("title=[42] rank=[not-a-number]", output);
    }

    [Fact]
    public void An_int_passed_where_a_float_is_declared_is_not_a_mismatch()
    {
        // Widening. `IrLiteral` keeps int-ness and arithmetic preserves it, so this is the ordinary way
        // to write a whole number — flagging it would make every `0` in a float field an error.
        var d = Diagnose(
            "  shape $P { x: float, y: float }\n  mark #P\n  builder P { $P   mark #P }\n" +
            "  shard S { run once { bring P(0, 3) } }");

        Assert.DoesNotContain(d, x => x.Code == "VS0230");
    }

    [Fact]
    public void A_non_literal_argument_is_never_judged()
    {
        // The line this check will not cross. There is no inference pass, so the type of `a + b` is
        // genuinely unknown here — and a warning that fires on correct code is worse than no warning.
        var d = Diagnose(RowDecl +
            "  fn pick() return int { return 1 }\n" +
            "  shard S { run once { let n = pick()\n    bring Row(\"ok\", n) } }");

        Assert.DoesNotContain(d, x => x.Code == "VS0230");
    }

    [Fact]
    public void An_emit_with_a_misspelled_field_is_VS0227()
    {
        // The quietest mistake in the language, now caught before it runs. The runtime half is
        // unchanged: `qty` is never set and the 9 still goes nowhere.
        var d = Diagnose(
            "  event @Order { item: string, qty: int }\n" +
            "  shard S { run once { emit @Order { item: \"nails\", quantity: 9 } } }");

        var hit = Assert.Single(d, x => x.Code == "VS0227");
        Assert.Contains("quantity", hit.Message);
        Assert.Contains("item, qty", hit.Message);   // says what it DOES take

        var output = Run(
            "  event @Order { item: string, qty: int }\n" +
            "  shard S { run once { emit @Order { item: \"nails\", quantity: 9 } } }\n" +
            "  shard H { hear @Order as o { " + P("\"item=[\" + o.item + \"] qty=[\" + o.qty + \"]\"") + " } }");

        Assert.Contains("item=[nails] qty=[]", output);
    }

    [Fact]
    public void An_emit_that_omits_a_field_is_NOT_reported()
    {
        // Deliberate, and load-bearing. `@Fetch` gained `headers` after programs were already emitting
        // it with three fields; reporting a missing field would have broken every one of them. An
        // absent field reads as empty, which is a defined answer — an unknown field name is not.
        var d = Diagnose(
            "  event @Order { item: string, qty: int }\n" +
            "  shard S { run once { emit @Order { item: \"nuts\" } } }");

        Assert.DoesNotContain(d, x => x.Code == "VS0227");
    }

    [Fact]
    public void A_call_with_the_wrong_number_of_arguments_is_VS0229()
    {
        // Nothing checked this in either direction. `add(1,2,3,4)` returned 3, `add(1)` returned 1 and
        // `add()` returned 0 — every one a plausible-looking number.
        var d = Diagnose(
            "  fn add(a: int, b: int) return int { return a + b }\n" +
            "  shard S { run once { " + P("add(1, 2, 3, 4)") + "\n    " + P("add(1)") + " } }");

        Assert.Equal(2, d.Count(x => x.Code == "VS0229"));
    }

    [Fact]
    public void A_call_into_another_bundle_is_checked_too()
    {
        // The qualified form resolves through the bundle index, so a stdlib signature is as checkable
        // as a local one — which is the half that matters for anyone building on the standard library.
        var d = Diagnose("  shard S { run once { *Vein.Rest.Auth.bearer(\"a\", \"b\", \"c\") } }");

        Assert.Contains(d, x => x.Code == "VS0229");
    }

    // ---- `base` as a field value: the declared default, in a payload or an attach ------------------------

    [Fact]
    public void Base_in_an_emit_payload_is_the_events_declared_default()
    {
        // The Workbench's `?` writes `base` for every optional field, so it has to mean in a payload what
        // it means at a bring: the default the declaration chose — not a copy of today's value.
        var output = Run(
            "  event @Order { item: string, qty: int = 3 }\n" +
            "  shard S { run once { emit @Order { item: \"nuts\", qty: base } } }\n" +
            "  shard H { hear @Order as o { " + P("o.item + \" x\" + o.qty") + " } }");

        Assert.Contains("nuts x3", output);
    }

    [Fact]
    public void Base_in_an_attach_is_the_shapes_declared_default()
    {
        var output = Run(
            "  shape $Price { amount: int, currency: string = \"EUR\" }\n" +
            "  shard S { run once { let e = spawn()\n    attach $Price to e { amount: 4, currency: base } } }\n" +
            "  shard R { each tick { target $Price as p { " + P("p.Price.amount + \" \" + p.Price.currency") + " } } }");

        Assert.Contains("4 EUR", output);
    }

    [Fact]
    public void Base_in_a_start_payload_is_the_default_too()
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by me {\n  start @Go { seed: base }\n  event @Go { seed: int = 7 }\n" +
            "  event @Response { status: int, body: string }\n" +
            "  shard K { hear @Go as g { emit @Response { status: 200, body: \"seed=\" + g.seed } } }\n}"));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        Assert.Contains("seed=7", new Interp().Render(r.Modules[0], "/").Body);
    }

    [Fact]
    public void Base_on_a_field_with_no_default_is_VS0231_and_a_typed_zero()
    {
        var d = Diagnose("  event @Order { item: string, qty: int }\n  shard S { run once { emit @Order { item: \"x\", qty: base } } }");
        Assert.Contains(d, x => x.Code == "VS0231" && x.Message.Contains("'qty'"));
    }

    [Fact]
    public void Base_outside_a_field_or_an_argument_still_means_nothing()
    {
        var d = Diagnose("  shape $P { a: int = 1 }\n  shard S { run once { let x = P { a: base } } }");
        Assert.Contains(d, x => x.Code == "VS0232");
    }

    // ---- re-attaching a shape: one component per identity, replaced whole --------------------------------

    private const string PriceDecl = "  shape $Price { amount: int, currency: string = \"EUR\" }\n  mark #Item\n";

    [Fact]
    public void A_reattach_replaces_the_component_and_omitted_fields_take_the_default()
    {
        var output = Run(PriceDecl +
            "  shard S { run once { let e = spawn()\n    attach $Price to e { amount: 5, currency: \"USD\" }\n    mark e #Item } }\n" +
            "  shard R { each tick { target $Price #Item as p { if p.Price.amount == 5 { attach $Price to p { amount: 9 } } } } }\n" +
            "  shard P { settled { target $Price #Item as p { " + P("p.Price.amount + \" \" + p.Price.currency") + " } } }");

        Assert.Contains("9 EUR", output);              // not "9 USD": the attach is the whole component
        Assert.Equal(1, output.Split("9 EUR").Length - 1);   // and one component, not a copy
    }

    [Fact]
    public void Reattaching_inside_a_target_that_matched_the_shape_is_a_note()
    {
        var d = Diagnose(PriceDecl + "  shard R { each tick { target $Price as p { attach $Price to p { amount: 9 } } } }");

        var note = Assert.Single(d, x => x.Code == "VS0348");
        Assert.Equal(Vein.Compiler.Diagnostics.Severity.Info, note.Severity);   // legal and often meant
        Assert.Contains("REPLACES it whole", note.Message);
        Assert.Contains("p.Price.field", note.Message);
    }

    [Fact]
    public void A_second_attach_in_the_same_block_is_a_note()
    {
        var d = Diagnose(PriceDecl +
            "  shard S { run once { let e = spawn()\n    attach $Price to e { amount: 1 }\n    attach $Price to e { amount: 2 } } }");
        Assert.Contains(d, x => x.Code == "VS0348" && x.Span.Line == 6);   // the SECOND attach
    }

    [Theory]
    [InlineData("let e = spawn()\n    attach $Price to e { amount: 1 }")]                                            // fresh
    [InlineData("let e = spawn()\n    if true { attach $Price to e { amount: 1 } }\n    attach $Price to e { amount: 2 }")] // only maybe
    [InlineData("let e = spawn()\n    attach $Price to e { amount: 1 }\n    unattach $Price from e\n    attach $Price to e { amount: 2 }")]
    public void Nothing_is_said_when_the_shape_is_not_certainly_there(string body)
    {
        Assert.DoesNotContain(Diagnose(PriceDecl + "  shard S { run once { " + body + " } }"), x => x.Code == "VS0348");
    }

    [Fact]
    public void Two_builders_with_one_name_are_VS0320_not_an_overload()
    {
        // The second used to replace the first silently. With the SAME parameter count nothing was
        // reported at all and every `bring` ran the second; with a different count the only symptom was
        // a VS0228 describing the wrong builder. VeinScript has no overloading, so the name is taken.
        var same = Diagnose(
            "  builder Line { text: string   line = \"first: \" + text }\n" +
            "  builder Line { text: string   line = \"second: \" + text }\n" +
            "  shard S { run once { bring Line(\"x\") } }");
        var other = Diagnose(
            "  builder Line { text: string   line = text }\n" +
            "  builder Line { a: string, b: string   line = a + b }\n" +
            "  shard S { run once { bring Line(\"x\", \"y\") } }");

        var d = Assert.Single(same, x => x.Code == "VS0320");
        Assert.Equal(Vein.Compiler.Diagnostics.Severity.Error, d.Severity);
        Assert.Equal(3, d.Span.Line);                       // reported at the SECOND declaration
        Assert.Contains("no overloading", d.Message);
        Assert.Contains("builder Short from Line", d.Message);
        Assert.Contains(other, x => x.Code == "VS0320");
    }

    [Fact]
    public void A_function_or_shape_declared_twice_is_VS0320_too()
    {
        var fns = Diagnose(
            "  fn twice(n: int) return int { return n * 2 }\n" +
            "  fn twice(s: string) return string { return s + s }\n");
        var shapes = Diagnose("  shape $P { a: int }\n  shape $P { b: int }\n");

        Assert.Contains(fns, x => x.Code == "VS0320" && x.Message.Contains("'twice()'") && x.Message.Contains("no overloading"));
        Assert.Contains(shapes, x => x.Code == "VS0320" && x.Message.Contains("'$P'"));
    }

    [Fact]
    public void A_bare_call_that_need_widened_is_checked_like_the_qualified_one()
    {
        // `need` makes bare `print` mean `*Vein.Console.Io.print`, and that path skipped both checks —
        // so the same mistake was a warning or nothing depending only on how the call was spelled.
        var count = Diagnose("  need \"Vein.Console\"\n  shard S { run once { do print(\"a\", \"b\", \"c\") } }");
        var kind = Diagnose("  need \"Vein.Console\"\n  shard S { run once { do print(42) } }");
        var fine = Diagnose("  need \"Vein.Console\"\n  shard S { run once { do print(\"ok\") } }");

        Assert.Contains(count, x => x.Code == "VS0229");
        Assert.Contains(kind, x => x.Code == "VS0230");
        Assert.DoesNotContain(fine, x => x.Code is "VS0229" or "VS0230");
    }

    [Fact]
    public void A_missing_int_field_arrives_empty_and_not_zero()
    {
        // Worth pinning separately, because "missing means 0" is the assumption everyone brings and it
        // is wrong. `< 1` is still true for it, which is why one guard catches both cases — but the
        // moment you PRINT it the difference shows.
        var output = Run(
            "  event @Order { item: string, qty: int }\n" +
            "  shard S { run once { emit @Order { item: \"nuts\" } } }\n" +
            "  shard H { hear @Order as o {\n" +
            "    " + P("\"qty=[\" + o.qty + \"]\"") + "\n" +
            "    if o.qty < 1 { " + P("\"and it compares as less than 1\"") + " } } }");

        Assert.Contains("qty=[]", output);
        Assert.DoesNotContain("qty=[0]", output);
        Assert.Contains("and it compares as less than 1", output);
    }

    // ---- `as row: $Shape` — an ascription for data that arrived from outside ----------------------

    [Fact]
    public void An_ascribed_collection_binding_is_accepted_and_checked()
    {
        // The case nothing else can reach: `fromJson` returns whatever the payload held, so a
        // collection binding has no inferable element type. The author states it — they are about to
        // `bring Row(…)` with the same shape — and the fields become known.
        var d = Diagnose(RowDecl +
            "  shard S { run once {\n" +
            "    let doc = fromJson(\"[]\")\n" +
            "    target doc as row: $Row { bring Row(row.title, row.rank) } } }");

        Assert.DoesNotContain(d, x => x.Severity == Vein.Compiler.Diagnostics.Severity.Error);
        Assert.DoesNotContain(d, x => x.Code == "VS0233");
    }

    [Fact]
    public void An_ascription_naming_a_shape_that_does_not_exist_is_VS0233()
    {
        // The ascription's whole value is that the fields get checked, so a misspelt shape quietly
        // switching that back off is the worst failure available to it.
        var d = Diagnose(RowDecl +
            "  shard S { run once {\n" +
            "    let doc = fromJson(\"[]\")\n" +
            "    target doc as row: $Rowe { bring Row(row.title, row.rank) } } }");

        Assert.Contains(d, x => x.Code == "VS0233");
    }

    [Fact]
    public void A_collection_loop_without_an_ascription_stays_legal()
    {
        // Optional, always. Exploring a payload whose shape you do not know yet has to keep working,
        // and that is the state every one of these loops was in before the ascription existed.
        var d = Diagnose(RowDecl +
            "  shard S { run once {\n" +
            "    let doc = fromJson(\"[]\")\n" +
            "    target doc as row { bring Row(row.title, row.rank) } } }");

        Assert.DoesNotContain(d, x => x.Severity == Vein.Compiler.Diagnostics.Severity.Error);
    }

    // ---- the sample ---------------------------------------------------------------------------------

    [Fact]
    public void The_diagnostics_sample_reports_and_carries_on()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        string path = Path.Combine(dir!.FullName, "samples", "diagnostics.vein");

        var r = new VeinCompilerService().Compile(new CompileRequest(
            Path.GetFileName(path), File.ReadAllText(path), SourcePath: path));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        var sw = new StringWriter();
        new Interp { Ticks = 1 }.Run(r.Modules[0], new StringReader(""), sw);
        string output = sw.ToString();

        Assert.Contains("WARN  | a row has an empty title", output);
        Assert.Contains("ERROR | Compute (once) at diagnostics.vein:80: Attempted to divide by zero.", output);
        Assert.Contains("recovered from Compute (once, diagnostics.vein line 80): using 0", output);
        Assert.DoesNotContain("THIS LINE NEVER PRINTS", output);
        // Still alive afterwards, and the rows the faulting run brought are intact.
        Assert.Contains("row: [write the docs] rank 3", output);
    }

    [Fact]
    public void The_guard_sample_reports_all_three_severities_and_rejects_the_broken_row()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        string path = Path.Combine(dir!.FullName, "samples", "diagnostics_guard.vein");

        var r = new VeinCompilerService().Compile(new CompileRequest(
            Path.GetFileName(path), File.ReadAllText(path), SourcePath: path));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        var sw = new StringWriter();
        new Interp { Ticks = 1 }.Run(r.Modules[0], new StringReader(""), sw);
        string output = sw.ToString();

        Assert.Contains("ERROR | row rejected", output);   // empty title, not brought
        Assert.Contains("WARN  | row \"ship it\" has rank [0]", output);
        Assert.Contains("note  | 4 rows arrived", output);

        // The typo row: `rnk: 9` never reached `rank`, so it warns with an EMPTY rank, not 0.
        Assert.Contains("WARN  | row \"paint it\" has rank []", output);

        // Three kept out of four — the rejected one built nothing.
        Assert.Equal(3, output.Split("kept: rank").Length - 1);
    }

    [Fact]
    public void The_defaults_sample_fills_in_what_each_call_left_out()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        string path = Path.Combine(dir!.FullName, "samples", "builder_defaults.vein");

        var r = new VeinCompilerService().Compile(new CompileRequest(
            Path.GetFileName(path), File.ReadAllText(path), SourcePath: path));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        var sw = new StringWriter();
        new Interp { Ticks = 1 }.Run(r.Modules[0], new StringReader(""), sw);
        string output = sw.ToString();

        // One argument, six defaults.
        Assert.Contains("(no description yet)", output);
        Assert.Contains("class=panel  accent=#4444aa  padded=true", output);

        // The `base` form: padded/col are given, and body/class/accent keep their declared values.
        Assert.Contains("class=panel  accent=#4444aa  padded=false  col=1", output);

        // What the Workbench's `?` writes: the empty value where there is no default, `base` where there is.
        Assert.Contains("title=[Untitled] note=[] slug=[untitled]", output);
    }
}
