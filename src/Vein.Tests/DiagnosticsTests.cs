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
        var output = Run("  shard S { run once { *Vein.Diagnostics.Report.warn(\"look at this\") } }");

        Assert.Contains("warning", output);
        Assert.Contains("look at this", output);
    }

    [Fact]
    public void The_three_severities_are_named_not_numbered_on_the_console()
    {
        var output = Run(
            "  shard S { run once {\n" +
            "    *Vein.Diagnostics.Report.info(\"eye\")\n" +
            "    *Vein.Diagnostics.Report.warn(\"ear\")\n" +
            "    *Vein.Diagnostics.Report.error(\"nose\") } }");

        Assert.Contains("(info: eye)", output);
        Assert.Contains("(warning: ear)", output);
        Assert.Contains("(error: nose)", output);
    }

    [Fact]
    public void A_collector_takes_over_from_the_console_rather_than_doubling_it()
    {
        // The console is a FALLBACK, not a default. A program that collects diagnostics has said where
        // it wants them, and printing as well would be the library overriding that.
        var output = Run(
            "  shard S { run once { *Vein.Diagnostics.Report.warn(\"once only\") } }\n" +
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
            "  shard S { run once { *Vein.Diagnostics.Report.warn(\"first\") } }\n" +
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
    public void A_bring_with_too_few_arguments_is_not_checked_and_the_field_is_empty()
    {
        // Documents a HOLE, not a feature. The row is built, the missing param is empty, and nothing
        // anywhere says so. If this test ever fails because a check was added, that is good news —
        // update it rather than restoring the silence.
        var d = Diagnose(RowDecl + "  shard S { run once { bring Row(\"only-title\") } }");
        Assert.DoesNotContain(d, x => x.Severity != Vein.Compiler.Diagnostics.Severity.Info);

        var output = Run(RowDecl +
            "  shard S { run once { bring Row(\"only-title\") } }\n" +
            "  shard R { settled { target $Row #Row as r { " + P("\"rank=[\" + r.Row.rank + \"]\"") + " } } }");

        Assert.Contains("rank=[]", output);
    }

    [Fact]
    public void A_bring_with_swapped_types_is_not_checked_either()
    {
        // Parameter types are documentation: IrExpr.ResolvedType is never assigned, so there is no
        // semantics pass to disagree with them. The values land exactly as written.
        var output = Run(RowDecl +
            "  shard S { run once { bring Row(42, \"not-a-number\") } }\n" +
            "  shard R { settled { target $Row #Row as r {\n" +
            "    " + P("\"title=[\" + r.Row.title + \"] rank=[\" + r.Row.rank + \"]\"") + " } } }");

        Assert.Contains("title=[42] rank=[not-a-number]", output);
    }

    [Fact]
    public void An_emit_with_a_misspelled_field_silently_drops_the_value()
    {
        // The quietest mistake available in the language: `qty` is never set, and the 9 goes nowhere.
        // No parse error, no compile warning, no runtime event.
        var output = Run(
            "  event @Order { item: string, qty: int }\n" +
            "  shard S { run once { emit @Order { item: \"nails\", quantity: 9 } } }\n" +
            "  shard H { hear @Order as o { " + P("\"item=[\" + o.item + \"] qty=[\" + o.qty + \"]\"") + " } }");

        Assert.Contains("item=[nails] qty=[]", output);
        Assert.DoesNotContain("9", output);
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
        Assert.Contains("ERROR | Compute (once): Attempted to divide by zero.", output);
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
}
