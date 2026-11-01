using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Vein.Hosting;
using Xunit;

namespace Vein.Tests;

// Wrong answers the interpreter gave WITHOUT A WORD, found by reading every program we own as one corpus
// (docs/CORPUS.md): most of them by Vein Cards, a card game whose rules engine runs on the interpreter.
//
//   * an event field named like the envelope (`id`, `from`, `origin`) read the runtime's value, not the emit's;
//   * walking a parsed object bound .NET pairs, whose `key` and `value` read empty;
//   * `slice` on a text answered `[]`;
//   * the safety caps — recursion at 256, a `while` at 100,000 turns, 10,000 events in a drain — stopped
//     the program and said nothing;
//   * a fault deep in nested calls was reported at the outermost `hear`.
//
// Each test below states the behaviour that replaced one of them. Where the C# backend can run the same
// program, a test runs both runtimes and holds them to the same output.
public class SilentRuntimeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-silent-" + Guid.NewGuid().ToString("N"));

    public SilentRuntimeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static IrModule Compile(string source)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", source));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        return r.Modules[0];
    }

    /// Run on the interpreter: what it printed, and what went to the error stream.
    private static (string Out, string Err, Interp Interp) Run(string source, int ticks = 1)
    {
        var module = Compile(source);
        var output = new StringWriter();
        var errors = new StringWriter();
        var interp = new Interp { Ticks = ticks, ErrorOutput = errors };
        interp.Run(module, new StringReader(""), output);
        return (output.ToString().Replace("\r", ""), errors.ToString().Replace("\r", ""), interp);
    }

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public static IEnumerable<object[]> Runtimes => new[] { new object[] { VeinRuntime.Interpreter }, new object[] { VeinRuntime.Compiled } };

    /// The same file, hosted on the runtime asked for — the interpreter, or the C# backend compiled.
    private List<string> Host(string name, string source, VeinRuntime mode, int frames)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, source);
        var game = VeinGame.Open(path, mode);
        var lines = new List<string>();
        game.Printed += (line, isError) => lines.Add(isError ? "ERR " + line : line);
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
        Assert.Equal(mode == VeinRuntime.Compiled ? "compiled" : "interpreter", game.Runtime);
        for (int i = 0; i < frames; i++) game.Step(1.0 / 60);
        return lines;
    }

    // ---- B.1 an event field named like the envelope --------------------------------------------------------

    [Fact]
    public void An_event_field_named_id_reads_what_the_emit_wrote()
    {
        // `event @HostMatch { id: string }` read back "ev50", the runtime's own id, where the emit had said
        // "M1" — and nothing warned (Vein Cards #18). A declared field is the event's own: it keeps its value.
        var (output, _, _) = Run("""
            bundle T by you {
                event @Pick { id: int, name: string }
                event @Plain { name: string }
                shard S {
                    run once {
                        emit @Pick { id: 42, name: "ace" }
                        emit @Plain { name: "two" }
                    }
                    hear @Pick as p { emit *Vein.Console.Io.@Print { text: "pick " + p.id + " " + p.name } }
                    hear @Plain as p { emit *Vein.Console.Io.@Print { text: "plain " + startsWith(p.id, "ev") } }
                }
            }
            """);

        Assert.Contains("pick 42 ace", output);
        Assert.Contains("plain true", output);     // an event that declares no `id` still reads the runtime's
    }

    [Fact]
    public void The_runtime_id_still_links_the_chain_when_an_event_declares_its_own()
    {
        // The chain is the runtime's, kept beside the payload: an event caused by @Pick names @Pick's runtime
        // id as its `cause`, never the 42 the program wrote into @Pick's own `id`.
        var (output, _, _) = Run("""
            bundle T by you {
                event @Pick { id: int }
                event @Picked { }
                shard S {
                    run once { emit @Pick { id: 42 } }
                    hear @Pick as p { emit @Picked { } }
                    hear @Picked as c {
                        emit *Vein.Console.Io.@Print { text: "cause " + startsWith(c.cause, "ev") + " " + (c.cause == "42") }
                        emit *Vein.Console.Io.@Print { text: "trail " + len(c.trail) + " " + (c.trail[0] == c.cause) }
                    }
                }
            }
            """);

        Assert.Contains("cause true false", output);
        Assert.Contains("trail 1 true", output);
    }

    [Fact]
    public void An_event_field_named_from_reads_what_the_emit_wrote_and_the_audience_still_tests_the_sender()
    {
        // samples/RpgDemoCopyPaste declares `@Attack { from: Entity, to: Entity }`: `a.from` read the SENDER's
        // provenance instead of the attacker, `attacker == a.from` never held, and the hero never landed a
        // blow. The barrier is the other half — it must keep testing who really sent the event.
        var (output, _, _) = Run("""
            bundle T by you {
                shape $Stats { name: string }
                event @Attack { from: Entity, to: Entity }
                shard Attacker {
                    run once {
                        let hero = spawn()
                        attach $Stats to hero { name: "Hero" }
                        emit @Attack { from: hero, to: hero }
                    }
                }
                shard Hears {
                    hear @Attack as a { emit *Vein.Console.Io.@Print { text: "same " + (a.from == a.to) } }
                }
                shard OnlyAttacker {
                    hear @Attack as a audience !Attacker { emit *Vein.Console.Io.@Print { text: "admitted from Attacker" } }
                }
                shard OnlyOthers {
                    hear @Attack as a audience not !Attacker { emit *Vein.Console.Io.@Print { text: "WRONG: admitted" } }
                }
            }
            """);

        Assert.Contains("same true", output);
        Assert.Contains("admitted from Attacker", output);
        Assert.DoesNotContain("WRONG", output);
    }

    [Fact]
    public void An_event_field_named_origin_keeps_its_value_inside_a_target()
    {
        // VeinScriptBundle's net.Mesh declares `@MeshDelivered { origin: string, … }` and documents
        // `d.origin` as the node a message came from. Emitted inside a `target`, it read the entity id.
        var (output, _, _) = Run("""
            bundle T by you {
                shape $Node { name: string }
                event @Delivered { origin: string, text: string }
                event @Plain { text: string }
                shard S {
                    run once {
                        let n = spawn()
                        attach $Node to n { name: "S2" }
                    }
                    each tick {
                        target $Node as n {
                            emit @Delivered { origin: "S1", text: "hello" }
                            emit @Plain { text: "hi" }
                        }
                    }
                    hear @Delivered as d { emit *Vein.Console.Io.@Print { text: "from " + d.origin + ": " + d.text } }
                    hear @Plain as p { emit *Vein.Console.Io.@Print { text: "entity " + (p.origin > 0) } }
                }
            }
            """);

        Assert.Contains("from S1: hello", output);
        Assert.Contains("entity true", output);   // an event with no `origin` of its own still gets the entity
    }

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void A_declared_id_and_from_read_alike_on_both_runtimes(VeinRuntime mode)
    {
        // Compiled payloads always kept a declared field; the interpreter now agrees — and the backend keeps a
        // declared `from` too, where it wrote the provenance into the program's Entity field (CS0029).
        var lines = Host("own.vein", """
            bundle Own by me {
                event @Pick { id: int, from: Entity, to: Entity }
                shard S {
                    run once {
                        let card = spawn()
                        emit @Pick { id: 42, from: card, to: card }
                    }
                    hear @Pick as p { emit *Vein.Console.Io.@Print { text: "pick " + p.id + " " + (p.from == p.to) } }
                }
            }
            """, mode, frames: 1);

        Assert.Contains("pick 42 true", lines);
    }

    [Fact]
    public void The_rpg_sample_lands_its_attacks()
    {
        var output = RunSample(Path.Combine("RpgDemoCopyPaste", "RpgDemoCopyPaste.vein"), ticks: 40);

        Assert.Contains("Hero hits Goblin for 6.", output);
        Assert.Contains("You slew the Dragon!", output);
    }

    private static string RunSample(string relative, int ticks)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        string path = Path.Combine(dir!.FullName, "samples", relative);

        var r = new VeinCompilerService().Compile(new CompileRequest(Path.GetFileName(path), File.ReadAllText(path), SourcePath: path));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var output = new StringWriter();
        new Interp { Ticks = ticks, ErrorOutput = new StringWriter() }.Run(r.Modules[0], new StringReader(""), output);
        return output.ToString();
    }

    // ---- B.2 walking a parsed object -------------------------------------------------------------------------

    [Fact]
    public void Walking_a_parsed_object_binds_each_key_and_value_in_the_order_the_keys_came()
    {
        // `kv.key` and `kv.value` read EMPTY — the walk bound .NET pairs — and only `string(kv)` showed
        // "[a, 1]", so Vein Cards found a record's keys by scanning the text toJson wrote (VEIN_REPORT #4).
        var (output, _, _) = Run("""
            bundle T by you {
                shard S {
                    run once {
                        let d = fromJson("{\"b\": 1, \"a\": \"two\", \"c\": [3, 4]}")
                        target d as kv { emit *Vein.Console.Io.@Print { text: Index + " " + kv.key + "=" + kv.value } }
                    }
                }
            }
            """);

        Assert.Equal(new[] { "0 b=1", "1 a=two", "2 c=[3, 4]" }, Lines(output));
    }

    [Fact]
    public void A_key_set_again_keeps_its_place_and_a_new_key_goes_last_as_in_a_map()
    {
        // The order a `~<map>` keeps (VeinMap.With): first put, a re-put stays where it was.
        var (output, _, _) = Run("""
            bundle T by you {
                shape $Board { ~<map> scores: string | int }
                shard S {
                    run once {
                        let d = fromJson("{\"b\": 1, \"a\": 2}")
                        d["a"] = 9
                        d["c"] = 3
                        var line = ""
                        target d as kv { line += kv.key + "=" + kv.value + " " }
                        emit *Vein.Console.Io.@Print { text: "record " + line }
                        let e = spawn()
                        attach $Board to e { }
                        e.Board.~scores.put("b", 1)
                        e.Board.~scores.put("a", 2)
                    }
                    each tick {
                        target $Board as b {
                            b.Board.~scores.put("a", 9)
                            b.Board.~scores.put("c", 3)
                        }
                    }
                    settled {
                        var line = ""
                        target $Board.~scores as b, kv { line += kv.key + "=" + kv.value + " " }
                        emit *Vein.Console.Io.@Print { text: "map    " + line }
                    }
                }
            }
            """);

        Assert.Contains("record b=1 a=9 c=3 ", output);
        Assert.Contains("map    b=1 a=9 c=3 ", output);
    }

    [Fact]
    public void An_entry_and_a_record_read_as_text_rather_than_as_dotnet()
    {
        var (output, _, _) = Run("""
            bundle T by you {
                shard S {
                    run once {
                        let d = fromJson("{\"a\": 1, \"n\": {\"x\": true}}")
                        target d as kv { emit *Vein.Console.Io.@Print { text: "entry " + kv } }
                        emit *Vein.Console.Io.@Print { text: "record " + d }
                    }
                }
            }
            """);

        Assert.Contains("entry {key: a, value: 1}", output);
        Assert.Contains("record {a: 1, n: {x: true}}", output);
        Assert.DoesNotContain("System.Collections", output);
    }

    [Fact]
    public void Two_different_records_are_no_longer_equal()
    {
        // `==` compares anything that is not a number as text, and every record's text was its .NET type
        // name — so ANY two records were equal.
        var (output, _, _) = Run("""
            bundle T by you {
                shard S {
                    run once {
                        let a = fromJson("{\"k\": 1}")
                        let b = fromJson("{\"k\": 2}")
                        let c = fromJson("{\"k\": 1}")
                        emit *Vein.Console.Io.@Print { text: "a==b " + (a == b) + ", a==c " + (a == c) }
                    }
                }
            }
            """);

        Assert.Contains("a==b false, a==c true", output);
    }

    [Fact]
    public void A_record_that_holds_itself_still_prints()
    {
        // Shared state is often a record holding itself (`db.match.db`): written out, it must end.
        var (output, _, _) = Run("""
            bundle T by you {
                shard S {
                    run once {
                        let db = fromJson("{\"n\": 1}")
                        db["self"] = db
                        emit *Vein.Console.Io.@Print { text: "db " + db }
                    }
                }
            }
            """);

        Assert.Contains("db {n: 1, self: {…}}", output);
    }

    // ---- B.3 slice on a text ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("slice(\"hello\", 1, 3)", "ell")]
    [InlineData("slice(\"hello\", 2)", "llo")]
    [InlineData("slice(\"hello\", 3, 99)", "lo")]        // past the end: shorter, never an error
    [InlineData("slice(\"hi\", 5)", "")]                 // a start past the end: nothing
    [InlineData("slice(\"hello\", -2, 2)", "he")]        // a start before the beginning: the beginning
    [InlineData("slice(\"hello\", 1, -1)", "")]          // a negative count takes nothing
    [InlineData("slice(chars(\"abc\"), 1)", "[b, c]")]   // a list is sliced as it always was
    public void Slice_of_a_text_is_the_text_between_by_the_lists_own_rules(string expr, string expected)
    {
        // It answered `[]` — a string is not a list of values — and said nothing (Vein Cards #16).
        var (output, _, _) = Run($$"""
            bundle T by you {
                shard S { run once { emit *Vein.Console.Io.@Print { text: "<" + ({{expr}}) + ">" } } }
            }
            """);

        Assert.Equal("<" + expected + ">", output.Trim());
    }

    // ---- B.4 the safety caps ---------------------------------------------------------------------------------

    /// A collector that prints every diagnostic's fields, one line each.
    private const string Collector = """
                shard Collector {
                    hear *Vein.Diagnostics.Report.@DiagnosticRaised as d {
                        emit *Vein.Console.Io.@Print { text: "heard " + d.severity + " | " + d.shard + " | " + d.phase + " | " + d.file + ":" + d.line + " | " + d.message }
                    }
                }
        """;

    [Fact]
    public void Recursion_past_the_cap_is_a_fault_that_stops_the_unit_and_says_where()
    {
        // It answered null and the block carried on with it: `down(0)` "returned" an empty value and the
        // print below ran, reporting a number nobody computed.
        var (output, _, _) = Run($$"""
            bundle T by you {
                need "Vein.Diagnostics"
                fn down(n: int) return int {
                    return down(n + 1)
                }
                shard S {
                    run once {
                        let x = down(0)
                        emit *Vein.Console.Io.@Print { text: "after " + x }
                    }
                }
            {{Collector}}
            }
            """);

        var heard = Assert.Single(Lines(output), l => l.StartsWith("heard", StringComparison.Ordinal));
        Assert.StartsWith("heard 2 | S | once | t.vein:4 | S (once) at t.vein:4 in fn down ×256 ← once: calls nested 256 deep", heard);
        Assert.Contains("needs a case that stops", heard);
        Assert.DoesNotContain("after", output);
    }

    [Fact]
    public void A_while_past_the_cap_is_a_fault_at_the_while()
    {
        var (output, _, _) = Run($$"""
            bundle T by you {
                need "Vein.Diagnostics"
                shard S {
                    run once {
                        var n = 0
                        while n >= 0 { n += 1 }
                        emit *Vein.Console.Io.@Print { text: "after " + n }
                    }
                }
            {{Collector}}
            }
            """);

        var heard = Assert.Single(Lines(output), l => l.StartsWith("heard", StringComparison.Ordinal));
        Assert.StartsWith("heard 2 | S | once | t.vein:6 | S (once) at t.vein:6: a `while` loop ran 100,000 times", heard);
        Assert.DoesNotContain("after", output);
    }

    [Fact]
    public void A_cap_nobody_hears_is_printed_as_an_error_and_fails_the_run()
    {
        var (output, errors, interp) = Run("""
            bundle T by you {
                fn down(n: int) return int { return down(n + 1) }
                shard S { run once { let x = down(0) } }
                shard Alive { run once { emit *Vein.Console.Io.@Print { text: "alive" } } }
            }
            """);

        Assert.Contains("(error: S (once) at t.vein:2 in fn down ×256 ← once: calls nested 256 deep", errors);
        Assert.Equal(1, interp.ExitCode);
        Assert.Contains("alive", output);   // one unit stopped; the program did not
    }

    [Fact]
    public void A_cap_outside_any_unit_is_said_and_the_program_starts_as_before()
    {
        // A shard var whose starting value recurses: there is no unit to stop, so the var is empty as it was
        // — and now the program is told.
        var (output, _, _) = Run($$"""
            bundle T by you {
                need "Vein.Diagnostics"
                fn down(n: int) return int { return down(n + 1) }
                shard S {
                    var x = down(0)
                    run once { emit *Vein.Console.Io.@Print { text: "x is [" + x + "]" } }
                }
            {{Collector}}
            }
            """);

        Assert.Contains("x is []", output);
        Assert.Contains(Lines(output), l => l.StartsWith("heard 2 |", StringComparison.Ordinal) && l.Contains("calls nested 256 deep", StringComparison.Ordinal));
    }

    private const string PingPong = """
            bundle Loop by me {
                need "Vein.Diagnostics"
                event @Ping { }
                shard Starter { run once { emit @Ping { } } }
                shard Pinger {
                    hear @Ping as p {
                        emit @Ping { }
                    }
                }
                shard Collector {
                    hear *Vein.Diagnostics.Report.@DiagnosticRaised as d {
                        emit *Vein.Console.Io.@Print { text: "heard " + d.severity + " | " + d.shard + " | " + d.phase + " | " + d.file + ":" + d.line + " | " + startsWith(d.message, "10,000 events in one drain — @Ping is still being emitted") }
                    }
                }
            }
            """;

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void A_drain_past_the_cap_is_heard_naming_the_event_and_where_it_is_emitted(VeinRuntime mode)
    {
        // An event that causes itself: the drain stopped at 10,000 events and said nothing. The diagnostic is
        // handled at once — not behind the runaway's own events — so a collector that prints it prints.
        var lines = Host("loop.vein", PingPong, mode, frames: 1);

        Assert.Contains("heard 2 | Pinger | hear @Ping | loop.vein:7 | true", lines);
    }

    [Fact]
    public void A_drain_past_the_cap_nobody_hears_is_printed_and_fails_the_run()
    {
        var (_, errors, interp) = Run("""
            bundle T by you {
                event @Ping { }
                shard Starter { run once { emit @Ping { } } }
                shard Pinger { hear @Ping as p { emit @Ping { } } }
            }
            """);

        Assert.Contains("(error: 10,000 events in one drain — @Ping is still being emitted", errors);
        Assert.Contains("— Pinger (hear @Ping) at t.vein:4)", errors);
        Assert.Equal(1, interp.ExitCode);
    }

    [Fact]
    public void With_a_budget_a_cap_is_still_out_of_gas_for_the_host()
    {
        // A host that must agree with other machines about whether work finished keeps OutOfGas: no handler
        // may turn the cap into a diagnostic and carry on as if the work were done.
        var module = Compile("""
            bundle T by you {
                fn down(n: int) return int { return down(n + 1) }
                shard S { run once { let x = down(0) } }
            }
            """);
        var interp = new Interp { Budget = 1_000_000_000, ErrorOutput = new StringWriter() };

        var gas = Assert.Throws<OutOfGas>(() => interp.Run(module, new StringReader(""), new StringWriter()));
        Assert.Contains("calls nested 256 deep", gas.Message);
    }

    // ---- B.5 where a fault happened ---------------------------------------------------------------------------

    [Fact]
    public void A_fault_inside_nested_calls_names_the_line_that_failed_and_the_calls_between()
    {
        // Vein Cards #17: an error in a long run of nested SF calls was reported at Engine.vein:1771, the
        // file-loaded handler, and finding the real line took a print at each step.
        var (output, _, _) = Run($$"""
            bundle T by you {
                need "Vein.Diagnostics"
                event @Loaded { }
                fn share(total: int, seats: int) return int {
                    return total / 1 + total % seats
                }
                SF deal(seats: int) {
                    let cut = share(52, seats)
                    emit *Vein.Console.Io.@Print { text: "dealt " + cut }
                }
                shard Engine {
                    run once { emit @Loaded { } }
                    hear @Loaded as l {
                        do deal(0)
                        emit *Vein.Console.Io.@Print { text: "after the deal" }
                    }
                }
            {{Collector}}
            }
            """);

        var heard = Assert.Single(Lines(output), l => l.StartsWith("heard", StringComparison.Ordinal));
        // shard and phase still name the unit, so a collector's `if d.shard == …` means what it did.
        Assert.Equal("heard 2 | Engine | hear @Loaded | t.vein:5 | " +
                     "Engine (hear @Loaded) at t.vein:5 in fn share ← SF deal ← hear @Loaded: Attempted to divide by zero.", heard);
        Assert.DoesNotContain("dealt", output);
        Assert.DoesNotContain("after the deal", output);
    }

    [Fact]
    public void A_fault_after_a_call_has_returned_is_the_calling_statements()
    {
        // The call's own statements moved the place; its return puts the caller's back.
        var (output, _, _) = Run($$"""
            bundle T by you {
                need "Vein.Diagnostics"
                fn twice(n: int) return int {
                    return n * 2
                }
                shard S {
                    run once {
                        let zero = 2 - 2
                        let x = twice(3) + 7 % zero
                    }
                }
            {{Collector}}
            }
            """);

        var heard = Assert.Single(Lines(output), l => l.StartsWith("heard", StringComparison.Ordinal));
        Assert.Equal("heard 2 | S | once | t.vein:9 | S (once) at t.vein:9: Attempted to divide by zero.", heard);
    }

    [Fact]
    public void A_recursion_chain_that_alternates_keeps_its_two_ends()
    {
        var (output, _, _) = Run($$"""
            bundle T by you {
                need "Vein.Diagnostics"
                fn ping(n: int) return int { return pong(n + 1) }
                fn pong(n: int) return int { return ping(n + 1) }
                shard S { run once { let x = ping(0) } }
            {{Collector}}
            }
            """);

        var heard = Assert.Single(Lines(output), l => l.StartsWith("heard", StringComparison.Ordinal));
        Assert.Contains("in fn pong ← fn ping ← fn pong ← fn ping ← … ← fn ping ← fn pong ← fn ping ← once: calls nested 256 deep", heard);
    }

    // A unit that faults in its body, in a `while` condition on its fourth turn, and after a call. Line
    // numbers are this source's: the `%` on 8, the `while` on 14, the call on 19.
    private const string Places = """
        bundle Places by me {
            need "Vein.Diagnostics"
            fn twice(n: int) return int { return n * 2 }
            shard Direct {
                run once {
                    let zero = 2 - 2
                    emit *Vein.Console.Io.@Print { text: "direct runs" }
                    let x = 5 % zero
                }
            }
            shard Loop {
                run once {
                    var n = 0
                    while 10 % (3 - n) >= 0 { n += 1 }
                }
            }
            shard Call {
                run once { let zero = 2 - 2
                    let y = twice(4) + 1 % zero
                }
            }
            shard Collector {
                hear *Vein.Diagnostics.Report.@DiagnosticRaised as d {
                    emit *Vein.Console.Io.@Print { text: "heard " + d.shard + " | " + d.phase + " | " + d.file + ":" + d.line + " | " + d.message }
                }
            }
        }
        """;

    [Theory]
    [MemberData(nameof(Runtimes))]
    public void A_fault_in_a_unit_names_its_statement_on_both_runtimes(VeinRuntime mode)
    {
        var lines = Host("places.vein", Places, mode, frames: 1);

        Assert.Contains("direct runs", lines);
        Assert.Contains("heard Direct | once | places.vein:8 | Direct (once) at places.vein:8: Attempted to divide by zero.", lines);
        Assert.Contains("heard Loop | once | places.vein:14 | Loop (once) at places.vein:14: Attempted to divide by zero.", lines);
        Assert.Contains("heard Call | once | places.vein:19 | Call (once) at places.vein:19: Attempted to divide by zero.", lines);
    }

    [Fact]
    public void A_diagnostic_the_program_raises_still_names_its_unit()
    {
        // `do warn(…)` is not a fault: its place stays the unit's, which is what Report's SFs promise.
        var (_, errors, _) = Run("""
            bundle T by you {
                need "Vein.Diagnostics"
                shard S { run once {
                    do *Vein.Diagnostics.Report.warn("look") } }
            }
            """);

        Assert.Contains("(warning: look — S (once) at t.vein:3)", errors);
    }

    // ---- B.6 a shard var set in one `hear` and written in the next ----------------------------------------------

    [Fact]
    public void A_shard_var_set_in_one_hear_is_what_the_next_hear_of_the_same_event_writes_into()
    {
        // Vein Cards #15, as reported: the second handler's write was said to land on the placeholder. It
        // lands on the record the first handler assigned — handlers of one event run in declaration order,
        // each seeing the shard's state as the one before left it.
        //
        // The EVENT does not change with it (docs/PAYLOAD-FOLDS.md): each hearer reads the payload as it was emitted,
        // on a copy of its own, so the record the first handler kept is its copy, and the third hearer's `r3.db` is
        // still what Boot sent. It used to print `event {"name":"real","x":1}` — a hearer reaching into what the
        // next one hears.
        var (output, _, _) = Run("""
            bundle T by you {
                event @Ready { db: string }
                shard Boot {
                    run once {
                        let real = fromJson("{\"name\": \"real\"}")
                        emit @Ready { db: real }
                    }
                }
                shard Engine {
                    var db = fromJson("{}")
                    hear @Ready as r { db = r.db }
                    hear @Ready as r2 { db["x"] = 1 }
                    hear @Ready as r3 { emit *Vein.Console.Io.@Print { text: "engine " + toJson(db) + " | event " + toJson(r3.db) } }
                }
            }
            """);

        Assert.Contains("engine {\"name\":\"real\",\"x\":1} | event {\"name\":\"real\"}", output);
    }
}
