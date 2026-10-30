using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// Three things that compiled clean and then gave a plausible wrong answer (ROADMAP Tier 1, rows 2, 3 and 10,
// and Vein Cards' VEIN_REPORT #13): a misspelled field read back EMPTY, a `let` changed like a `var`, and a type
// name nobody declared was taken for an untyped value. Each is now a diagnostic where it is written.
public class SilentStaticChecksTests
{
    private static CompilationResult Compile(string source) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", source));

    private static List<Diagnostic> Coded(CompilationResult r, string code) =>
        r.Diagnostics.Where(d => d.Code == code).ToList();

    private static string All(CompilationResult r) => string.Join("\n", r.Diagnostics.Select(d => d.ToString()));

    private static string Run(CompilationResult r, int ticks = 1)
    {
        Assert.True(r.Success, All(r));
        var output = new StringWriter();
        new Interp { Ticks = ticks }.Run(r.Modules[0], new StringReader(""), output);
        return output.ToString().Replace("\r", "").Trim();
    }

    private const string Print = "SF print(text: string) { emit *Vein.Console.Io.@Print { text: text } }";

    /// A bundle with `$Task { title, rank }`, one task spawned, and `body` as the shard's other members.
    private static string WithTask(string body) => $$"""
        bundle T by you {
            shape $Task { title: string, rank: int }
            shard S {
                run once { let e = spawn()  attach $Task to e { title: "hello", rank: 1 } }
                {{body}}
            }
            {{Print}}
        }
        """;

    // ---- A1.1: a field the shape or event does not have --------------------------------------------

    [Fact]
    public void A_misspelled_shape_field_read_through_a_target_is_an_error()
    {
        var r = Compile($$"""
            bundle T by you {
                shape $Task { title: string }
                shard S {
                    run once { let e = spawn()  attach $Task to e { title: "hello" } }
                    each tick { target $Task as row { do print(row.Task.titel) } }
                }
                {{Print}}
            }
            """);

        var d = Assert.Single(Coded(r, "VS0391"));
        Assert.Equal(Severity.Error, d.Severity);
        Assert.Contains("'titel'", d.Message);
        Assert.Contains("title", d.Message);          // what the shape DOES have, so the fix is one look away
        Assert.Equal(5, d.Span.Line);
    }

    [Fact]
    public void A_misspelled_event_field_read_in_a_hear_is_an_error()
    {
        var r = Compile($$"""
            bundle T by you {
                event @E { name: string }
                shard S {
                    run once { emit @E { name: "n" } }
                    hear @E as ev { do print(ev.feild) }
                }
                {{Print}}
            }
            """);

        var d = Assert.Single(Coded(r, "VS0391"));
        Assert.Equal(Severity.Error, d.Severity);
        Assert.Contains("'feild'", d.Message);
        Assert.Contains("@E", d.Message);
    }

    [Fact]
    public void A_misspelled_field_on_an_ascribed_binding_is_an_error()
    {
        // RULES 27 promised exactly this — "the ascription's whole value is that fields get checked" — and
        // Resolve typed the binding and reported nothing.
        var r = Compile($$"""
            bundle T by you {
                shape $Task { title: string, rank: int }
                shard S {
                    run once {
                        let rows = fromJson("[{\"title\": \"x\", \"rank\": 1}]")
                        target rows as row: $Task { do print(row.titel) }
                    }
                }
                {{Print}}
            }
            """);

        var d = Assert.Single(Coded(r, "VS0391"));
        Assert.Equal(Severity.Error, d.Severity);
        Assert.Contains("'titel'", d.Message);
    }

    [Fact]
    public void Writing_a_field_the_shape_does_not_declare_is_an_error_too()
    {
        // `x.Task.titel = …` stored a value under a name nothing else knows — not `toJson`, not a weave, not
        // the C# backend — and `+=` on one is a write as well as a read.
        var r = Compile(WithTask("""
            each tick { target $Task as row { row.Task.titel = "x"  row.Task.rnak += 1 } }
            """));

        var ds = Coded(r, "VS0391");
        Assert.Equal(2, ds.Count);
        Assert.All(ds, d => Assert.Equal(Severity.Error, d.Severity));
        Assert.Contains(ds, d => d.Message.Contains("'titel'") && d.Message.Contains("write"));
        Assert.Contains(ds, d => d.Message.Contains("'rnak'") && d.Message.Contains("'rank'"));
    }

    [Fact]
    public void Every_events_envelope_is_readable_without_being_declared()
    {
        // LANGUAGE.md §3.7: id, from, origin, source, bundle, cause and trail are on every event, declared or
        // not — and `from` carries more than the two fields the IR's provenance type names.
        var r = Compile($$"""
            bundle T by you {
                event @E { name: string }
                shard S {
                    run once { emit @E { name: "n" } }
                    hear @E as ev {
                        do print(ev.name + ev.id + ev.cause + ev.bundle + ev.from.name + ev.from.kind + ev.from.identity)
                        do print("" + ev.origin + ev.source + len(ev.trail) + len(ev.from.marks))
                    }
                }
                {{Print}}
            }
            """);

        Assert.Empty(Coded(r, "VS0391"));
        Assert.Contains("n", Run(r));
    }

    [Fact]
    public void A_field_read_straight_off_an_identity_is_an_error_that_names_the_shape()
    {
        // `row` is an identity; its data is on its shapes. The interpreter read `row.title` as EMPTY while the
        // C# backend read $Task's title — the two runtimes disagreed about a line missing `.Task`.
        var r = Compile(WithTask("""
            each tick { target $Task as row { do print(row.title) } }
            """));

        var d = Assert.Single(Coded(r, "VS0393"));
        Assert.Equal(Severity.Error, d.Severity);
        Assert.Contains("row.Task.title", d.Message);
    }

    [Fact]
    public void A_record_read_through_its_own_shape_name_is_an_error()
    {
        // The opposite of a query binding (RULES 27): an ascribed row IS the record, so `row.Task.title` reads
        // a key called "Task" that no $Task has. It used to be typed as the component and read as empty.
        var r = Compile($$"""
            bundle T by you {
                shape $Task { title: string, rank: int }
                shard S {
                    run once {
                        target fromJson("[{\"title\": \"x\", \"rank\": 1}]") as row: $Task { do print(row.Task.title) }
                    }
                }
                {{Print}}
            }
            """);

        var d = Assert.Single(Coded(r, "VS0391"));
        Assert.Contains("row.title", d.Message);
    }

    [Fact]
    public void A_needed_events_payload_is_checked_like_a_local_one()
    {
        // `each tick as t` is typed as `*Vein.Time.Clock.@Ticked` — frame and delta.
        var r = Compile(WithTask("""
            each tick as t { do print("" + t.delt) }
            """));

        var d = Assert.Single(Coded(r, "VS0391"));
        Assert.Contains("@Ticked", d.Message);
        Assert.Contains("'delta'", d.Message);
    }

    [Fact]
    public void A_declared_type_is_the_authors_word_so_a_field_it_lacks_is_an_error()
    {
        // A parameter, a result or a list element typed as a shape says what the value is, as an ascription does.
        var r = Compile($$"""
            bundle T by you {
                shape $Task { title: string }
                fn show(t: Task) return string { return t.titel }
                fn first(ts: list<Task>) return string { return ts[0].titel }
                {{Print}}
            }
            """);

        Assert.Equal(2, Coded(r, "VS0391").Count(d => d.Severity == Severity.Error));
    }

    [Fact]
    public void A_var_whose_type_was_only_inferred_is_not_judged()
    {
        // `var g = w.Task` may hold anything after its next assignment, so a field read through it is not
        // called unknown. A `let` cannot change (VS0390), and a declared type is the author's own word.
        var r = Compile(WithTask("""
            each tick {
                target $Task as w {
                    var g = w.Task
                    do print(g.titel)
                    let h = w.Task
                    do print(h.titel)
                }
            }
            """));

        var d = Assert.Single(Coded(r, "VS0391"));
        Assert.Equal(10, d.Span.Line);                // the `let`'s read, not the `var`'s
    }

    [Fact]
    public void A_request_can_carry_fields_of_its_own_so_only_a_near_miss_is_said()
    {
        // `?name=x` reads as `r.name` (stdlib/Web.vein): the boot event's payload is open. `r.pth` is one slip
        // from `path`, which is a typo far more often than a query parameter.
        var r = Compile($$"""
            bundle T by you {
                need "Vein.Web"
                shard S {
                    hear @Request as r { do print(r.path + r.name + r.pth) }
                }
                {{Print}}
            }
            """);

        var d = Assert.Single(Coded(r, "VS0391"));
        Assert.Equal(Severity.Warning, d.Severity);
        Assert.Contains("'pth'", d.Message);
        Assert.Contains("'path'", d.Message);
        Assert.True(r.Success, All(r));
    }

    [Fact]
    public void A_start_event_is_open_too()
    {
        // `--set k=v` puts any field on the boot payload, whatever `start` named.
        var r = Compile($$"""
            bundle T by you {
                event @Boot { seed: int = 1 }
                start @Boot { seed: 2 }
                shard S { hear @Boot as b { do print("" + b.seed + b.player) } }
                {{Print}}
            }
            """);

        Assert.Empty(Coded(r, "VS0391"));
    }

    [Fact]
    public void Reading_declared_fields_compiles_and_runs_as_before()
    {
        var r = Compile(WithTask("""
            each tick { target $Task as row { do print(row.Task.title + " " + row.Task.rank) } }
            """));

        Assert.Equal("hello 1", Run(r));
    }

    // ---- A1.2: a `let` is not a `var` --------------------------------------------------------------

    [Fact]
    public void Reassigning_a_let_is_an_error()
    {
        var r = Compile($$"""
            bundle T by you {
                shard S {
                    run once {
                        let k = 1
                        k = 2
                        do print("" + k)
                    }
                }
                {{Print}}
            }
            """);

        var d = Assert.Single(Coded(r, "VS0390"));
        Assert.Equal(Severity.Error, d.Severity);
        Assert.Contains("`k`", d.Message);
        Assert.Contains("var", d.Message);            // the fix: declare it `var`
        Assert.Equal(5, d.Span.Line);
    }

    [Theory]
    [InlineData("+=")]
    [InlineData("-=")]
    [InlineData("*=")]
    [InlineData("/=")]
    public void A_compound_assignment_to_a_let_is_an_error(string op)
    {
        var r = Compile($$"""
            bundle T by you {
                shard S { run once { let k = 6  k {{op}} 2 } }
            }
            """);

        var d = Assert.Single(Coded(r, "VS0390"));
        Assert.Contains($"k {op}", d.Message);
    }

    [Fact]
    public void A_var_stays_assignable()
    {
        var r = Compile($$"""
            bundle T by you {
                shard S { run once { var k = 1  k = 2  k += 3  do print("" + k) } }
                {{Print}}
            }
            """);

        Assert.Empty(Coded(r, "VS0390"));
        Assert.Equal("5", Run(r));
    }

    [Fact]
    public void Changing_what_a_let_holds_is_not_reassigning_it()
    {
        // `let` fixes the NAME. A list is the same list everywhere it is held (RULES 28h), so it could never
        // promise the contents were fixed; an element or a record's field is the value changing, not the name.
        var r = Compile($$"""
            bundle T by you {
                shard S {
                    run once {
                        let xs = [1, 2]
                        xs[0] = 5
                        let d = fromJson("{}")
                        d.k = "a"
                        d["j"] = "b"
                        do print(join(xs, ",") + d.k + d.j)
                    }
                }
                {{Print}}
            }
            """);

        Assert.Empty(Coded(r, "VS0390"));
        Assert.Equal("5,2ab", Run(r));
    }

    [Fact]
    public void Growing_a_let_list_rebinds_it_and_is_refused()
    {
        // `xs += 2` makes a NEW list and points the name at it (RULES 28h: growing copies).
        var r = Compile("""
            bundle T by you {
                shard S { run once { let xs = [1]  xs += 2 } }
            }
            """);

        Assert.Single(Coded(r, "VS0390"));
    }

    [Fact]
    public void A_shards_let_cannot_be_reassigned_by_its_handlers()
    {
        var r = Compile($$"""
            bundle T by you {
                event @Go { }
                shard S {
                    let max = 5
                    run once { emit @Go { } }
                    hear @Go as g { max = 6 }
                    fn bump() return int { max += 1  return max }
                }
            }
            """);

        var ds = Coded(r, "VS0390");
        Assert.Equal(2, ds.Count);
        Assert.All(ds, d => Assert.Contains("S's `let`", d.Message));
    }

    [Fact]
    public void Names_resolve_local_first_as_the_interpreter_assigns_them()
    {
        // d7fddc0: an assignment writes the body's own name before the shard's. So a handler's own `var cats`
        // shadows the shard's `let cats` (fine), and its own `let cats` is a `let` even where the shard's
        // `cats` is a `var` (refused).
        var r = Compile($$"""
            bundle T by you {
                event @A { }
                event @B { }
                shard S {
                    let cats = "the shard's"
                    var dogs = "the shard's"
                    run once { emit @A { }  emit @B { } }
                    hear @A as a { var cats = "mine"  cats = cats + "!"  do print(cats) }
                    hear @B as b { let dogs = "mine"  dogs = "changed" }
                }
                {{Print}}
            }
            """);

        var d = Assert.Single(Coded(r, "VS0390"));
        Assert.Contains("`dogs` is a `let`", d.Message);
        Assert.Equal(9, d.Span.Line);
    }

    [Fact]
    public void A_let_inside_a_block_is_still_that_name_after_the_block()
    {
        // The interpreter's locals are flat — an `IrBlock` shares its parent's scope — so after the `if` ran,
        // `k = 2` writes that `let`.
        var r = Compile("""
            bundle T by you {
                shard S { run once { if true { let k = 1 }  k = 2 } }
            }
            """);

        Assert.Single(Coded(r, "VS0390"));
    }

    [Fact]
    public void A_parameter_and_a_binding_can_be_assigned()
    {
        var r = Compile($$"""
            bundle T by you {
                fn bump(x: int) return int { x = x + 1  return x }
                shard S { run once { repeat 2 as i { i = 5 }  do print("" + bump(1)) } }
                {{Print}}
            }
            """);

        Assert.Empty(Coded(r, "VS0390"));
        Assert.Equal("2", Run(r));
    }

    // ---- A1.3: a type name nothing declares ---------------------------------------------------------

    [Fact]
    public void A_misspelled_type_name_is_an_error()
    {
        var r = Compile("""
            bundle T by you {
                fn f(x: strng) return string { return x }
            }
            """);

        var d = Assert.Single(Coded(r, "VS0392"));
        Assert.Equal(Severity.Error, d.Severity);
        Assert.Contains("'strng'", d.Message);
        Assert.Contains("'string'", d.Message);       // did you mean
    }

    [Theory]
    [InlineData("fn f(x: flaot) return int { return 1 }", "float")]
    [InlineData("fn f(x: int) return itn { return x }", "int")]
    [InlineData("fn f() return int { let n: Strng = \"a\"  return 1 }", "string")]
    [InlineData("shape $P { x: Entty }", "Entity")]
    [InlineData("event @E { at: Mrak }", "Mark")]
    [InlineData("builder B { n: boool  line = \"\" }", "bool")]
    [InlineData("fn f(xs: list<strng>) return int { return 1 }", "string")]
    [InlineData("type V { x: flot }", "float")]
    [InlineData("shard S { var hp: itn }", "int")]
    [InlineData("shape $Task { title: string }  fn f(t: Tsak) return int { return 1 }", "Task")]
    public void A_slip_of_a_type_name_is_an_error_wherever_it_is_written(string member, string meant)
    {
        var r = Compile($"bundle T by you {{\n    {member}\n}}");

        var d = Assert.Single(Coded(r, "VS0392"));
        Assert.Equal(Severity.Error, d.Severity);
        Assert.Contains($"'{meant}'", d.Message);
    }

    [Fact]
    public void Every_type_the_language_has_compiles_clean()
    {
        var r = Compile("""
            bundle T by you {
                need "Vein.Math"
                shape $Unit {
                    enum Mood { Calm, Angry }
                    mood: Mood
                    hp: int
                }
                type Pair { a: float, b: float }
                event @Hit { by: Entity, amount: percent }
                fn all(i: int, f: float, b: bool, s: string, p: percent, e: Entity, m: Mark, xs: list<int>, u: Unit, pr: Pair, h: Hit, md: Mood, v: Vec2, doc: json, d: dictionary) return void { }
            }
            """);

        Assert.Empty(Coded(r, "VS0392"));
    }

    [Fact]
    public void A_value_read_from_JSON_is_typed_json_and_a_parsed_object_dictionary()
    {
        // The names Vein Cards needed and did not have, so it wrote them and nothing objected: a value whose
        // kind the data decides, and a parsed object (RULES 24b's own word). Nothing about either is checked
        // — which is what they mean.
        var r = Compile($$"""
            bundle T by you {
                fn get(d: dictionary, k: string) return json { return d[k] }
                shard S { run once { do print("" + get(fromJson("{\"a\": 3}"), "a") + get(fromJson("{\"b\": [1]}"), "b")) } }
                {{Print}}
            }
            """);

        Assert.Empty(Coded(r, "VS0392"));
        Assert.Equal("3[1]", Run(r));
    }

    [Theory]
    [InlineData("any")]
    [InlineData("Vector3")]
    public void A_name_that_is_no_type_at_all_is_a_warning_that_points_to_json(string name)
    {
        // Not a slip of a real type, so not a typo to refuse: it may be a shape another bundle of an app
        // declares, or a value the author had no name for. It compiles, and the warning says what to write —
        // `any` is two letters from `int` and is still not taken for it.
        var r = Compile($"bundle T by you {{\n    fn f(d: {name}) return int {{ return 1 }}\n}}");

        var d = Assert.Single(Coded(r, "VS0392"));
        Assert.Equal(Severity.Warning, d.Severity);
        Assert.Contains("`json`", d.Message);
        Assert.DoesNotContain("Did you mean", d.Message);
        Assert.True(r.Success, All(r));
    }
}
