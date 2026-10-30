using System.Text.RegularExpressions;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Lexing;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// Four declarations that compiled into a wrong answer, or into a wall of unrelated errors, with nothing
// pointing at the line that caused it (ROADMAP Tier 1, rows 6, 8, 9 and 11):
//
//   let top = 5                            at bundle level: DROPPED, so every `top` read empty — VS0394
//   fn f(x: int) return int { if … }       f(0) fell off the end and answered empty — VS0395
//   match d { when Nroth { … } }           a misspelt case never matched, and nothing said so — VS0242
//   var count = 0                          `count` is a keyword: VS0100, then a cascade — VS0396
//   fn f(x: int?)                          there are no nullable types: "Expected ')'" — VS0397
public class SilentDeclarationTests
{
    private static CompilationResult Compile(string source) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", source));

    /// Compile, demand success, run, and return what the program printed.
    private static string Run(string source, int ticks = 1)
    {
        var r = Compile(source);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var output = new StringWriter();
        new Interp { Ticks = ticks }.Run(r.Modules[0], new StringReader(""), output);
        return output.ToString().Trim();
    }

    private static string Lines(CompilationResult r) => string.Join("\n", r.Diagnostics.Select(d => d.ToString()));

    // ---- A2.1: a bundle-level `let` / `var` (VS0394) ---------------------------------------------------

    [Fact]
    public void A_bundle_level_let_is_an_error_at_its_declaration()
    {
        // Lower dropped it ("module-level state: not modeled yet"), so `top` below was a name nothing had
        // ever set: the program compiled clean and printed "top=".
        var r = Compile("""
            bundle T by me {
                let top = 5
                shard S { run once { emit *Vein.Console.Io.@Print { text: "top=" + top } } }
            }
            """);

        Assert.False(r.Success);
        var d = Assert.Single(r.Diagnostics, x => x.Code == "VS0394");
        Assert.Equal(Severity.Error, d.Severity);
        Assert.Equal(2, d.Span.Line);
        Assert.Contains("`top`", d.Message);
    }

    [Fact]
    public void The_message_says_where_state_goes_instead()
    {
        // A shard var for state, a tune var for a value a project changes, an fn for a shared constant.
        var d = Assert.Single(Compile("bundle T by me {\n    var hits = 0\n    shard S { run once { hits += 1 } }\n}").Diagnostics,
            x => x.Code == "VS0394");

        Assert.Contains("shard", d.Message);
        Assert.Contains("tune(", d.Message);
        Assert.Contains("fn hits() return", d.Message);
    }

    [Theory]
    [InlineData("var top = 5")]
    [InlineData("var top: int")]
    [InlineData("let top: string = \"x\"")]
    [InlineData("publicator Api {\n        let top = 5\n    }")]
    public void Every_bundle_level_binding_is_the_same_error(string declaration)
    {
        var r = Compile("bundle T by me {\n    " + declaration + "\n    shard S { run once { } }\n}");

        var d = Assert.Single(r.Diagnostics, x => x.Code == "VS0394");
        Assert.Equal(Severity.Error, d.Severity);
    }

    [Fact]
    public void A_shard_var_is_still_state_and_reads_its_starting_value()
    {
        // What the message sends the reader to has to work.
        Assert.Equal("top=5", Run("""
            bundle T by me {
                shard S {
                    var top = 5
                    run once { emit *Vein.Console.Io.@Print { text: "top=" + top } }
                }
            }
            """));
    }

    // ---- A2.2: an `fn … return T` that can reach its end (VS0395) ---------------------------------------

    private static IReadOnlyList<Diagnostic> FnDiags(string fn) =>
        Compile("bundle T by me {\n    shape $Dir { enum Way { North, South }  way: Way }\n    " + fn +
                "\n    shard S { run once { } }\n}").Diagnostics;

    [Fact]
    public void An_fn_that_can_fall_off_its_end_is_warned_at_its_declaration()
    {
        string src = """
            bundle T by me {
                fn f(x: int) return int { if x > 0 { return 1 } }
                shard S { run once { emit *Vein.Console.Io.@Print { text: "f0=" + f(0) + " f1=" + f(1) } } }
            }
            """;
        var r = Compile(src);

        var d = Assert.Single(r.Diagnostics, x => x.Code == "VS0395");
        Assert.Equal(Severity.Warning, d.Severity);
        Assert.Equal(2, d.Span.Line);
        Assert.Contains("`f`", d.Message);
        Assert.Contains("return int", d.Message);

        // Still a warning, not an error: this is what it warns about — f(0) answers empty.
        Assert.Equal("f0= f1=1", Run(src));
    }

    [Theory]
    [InlineData("fn f(x: int) return int { return 1 }")]
    [InlineData("fn f(x: int) return int { if x > 0 { return 1 } else { return 2 } }")]
    [InlineData("fn f(x: int) return int { if x > 0 { return 1 } else if x < 0 { return 2 } else { return 0 } }")]
    [InlineData("fn f(x: int) return int {\n        if x > 0 { return 1 }\n        return 0\n    }")]
    [InlineData("fn f(x: int) return int {\n        var n = x\n        while n > 0 { n -= 1 }\n        return n\n    }")]
    [InlineData("fn f(x: string) return int {\n        match x {\n            when a { return 1 }\n            else { return 0 }\n        }\n    }")]
    [InlineData("fn f(w: Way) return int {\n        match w {\n            when North { return 1 }\n            when South { return 2 }\n        }\n    }")]
    [InlineData("fn f(x: int) return int {\n        if x > 0 {\n            let y = x\n            return y\n        } else {\n            if x < 0 { return 1 } else { return 2 }\n        }\n    }")]
    public void An_fn_whose_every_path_returns_is_not_warned(string fn) =>
        Assert.DoesNotContain(FnDiags(fn), d => d.Code == "VS0395");

    [Theory]
    [InlineData("fn f(x: int) return int {\n        var n = x\n        while n > 0 { return n }\n    }")]
    [InlineData("fn f(x: int) return int {\n        repeat x { return 1 }\n    }")]
    [InlineData("fn f(x: int) return int {\n        target [1, 2] as v { return v }\n    }")]
    public void A_return_inside_a_loop_does_not_count_because_a_loop_may_not_run(string fn) =>
        Assert.Contains(FnDiags(fn), d => d.Code == "VS0395");

    [Theory]
    [InlineData("fn f(x: int) return int { if x > 0 { return 1 } else if x < 0 { return 2 } }")]
    [InlineData("fn f(x: int) return int { if x > 0 { return 1 } else { let y = 2 } }")]
    [InlineData("fn f(x: string) return int {\n        match x {\n            when a { return 1 }\n            when b { return 2 }\n        }\n    }")]
    [InlineData("fn f(w: Way) return int {\n        match w {\n            when North { return 1 }\n        }\n    }")]
    [InlineData("fn f(w: Way) return int {\n        match w {\n            when North { return 1 }\n            when South { let n = 2 }\n            else { return 0 }\n        }\n    }")]
    [InlineData("fn f(x: int) return string { }")]
    public void A_branch_that_does_not_return_is_warned(string fn) =>
        Assert.Contains(FnDiags(fn), d => d.Code == "VS0395");

    [Fact]
    public void A_shards_own_fn_is_checked_too()
    {
        var r = Compile("""
            bundle T by me {
                shard S {
                    fn pick(x: int) return string { if x > 0 { return "up" } }
                    run once { }
                }
            }
            """);

        var d = Assert.Single(r.Diagnostics, x => x.Code == "VS0395");
        Assert.Equal(3, d.Span.Line);
    }

    [Fact]
    public void An_fn_with_no_result_and_an_SF_are_not_warned()
    {
        // Neither promises a value. An SF emits instead of returning (VS0105) and is left alone.
        var r = Compile("""
            bundle T by me {
                fn note(x: int) { let y = x }
                SF say(x: int) { emit *Vein.Console.Io.@Print { text: "" + x } }
                shard S { run once { do say(1) } }
            }
            """);

        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "VS0395");
    }

    // ---- A2.3: a `when` arm naming no case of the matched enum (VS0242) -----------------------------------

    private const string Compass = """
        bundle T by me {
            shape $Dir { enum Way { North, South, East }  way: Way }
            event @Moved { way: Way }
            mark #Dir
            builder Needle { $Dir  mark #Dir }
            shard Boot { run once { bring Needle(Way.North) } }

        """;

    [Fact]
    public void A_misspelt_enum_arm_is_an_error_naming_the_case_meant()
    {
        // `when Nroth` compared the case name "North" with the text "Nroth": never equal, never run, and
        // the `match` simply did nothing for every needle.
        var r = Compile(Compass + """
                shard S {
                    settled {
                        target $Dir #Dir as d {
                            match d.Dir.way {
                                when Nroth { emit *Vein.Console.Io.@Print { text: "north" } }
                                when South { emit *Vein.Console.Io.@Print { text: "south" } }
                            }
                        }
                    }
                }
            }
            """);

        Assert.False(r.Success);
        var d = Assert.Single(r.Diagnostics, x => x.Code == "VS0242");
        Assert.Equal(Severity.Error, d.Severity);
        Assert.Equal(11, d.Span.Line);
        Assert.Contains("Nroth", d.Message);
        Assert.Contains("did you mean `North`", d.Message);
        Assert.Contains("North, South, East", d.Message);
    }

    [Fact]
    public void An_arm_on_an_enum_parameter_or_event_field_is_checked_the_same_way()
    {
        var r = Compile(Compass + """
                fn score(w: Way) return int {
                    match w {
                        when Est { return 3 }
                        else { return 0 }
                    }
                }
                shard S {
                    hear @Moved as m {
                        match m.way {
                            when Soth { emit *Vein.Console.Io.@Print { text: "south" } }
                        }
                    }
                }
            }
            """);

        Assert.Equal(new[] { "Est", "Soth" },
            r.Diagnostics.Where(x => x.Code == "VS0242").Select(x => Regex.Match(x.Message, "`when (\\w+)`").Groups[1].Value));
    }

    [Fact]
    public void An_arm_naming_every_case_still_matches()
    {
        Assert.Equal("north", Run(Compass + """
                shard S {
                    settled {
                        target $Dir #Dir as d {
                            match d.Dir.way {
                                when North { emit *Vein.Console.Io.@Print { text: "north" } }
                                when South { emit *Vein.Console.Io.@Print { text: "south" } }
                                else { emit *Vein.Console.Io.@Print { text: "NO WAY" } }
                            }
                        }
                    }
                }
            }
            """));
    }

    [Fact]
    public void A_match_on_text_with_bare_arms_is_not_an_enum_match()
    {
        // `match cmd { when quit … }` compares TEXT with a word, and apps/tasks is written this way. An enum
        // in the same bundle sharing one of the words does not make the subject that enum.
        var r = Compile(Compass + """
                shard S {
                    run once {
                        let cmd = "quit"
                        match cmd {
                            when North { emit *Vein.Console.Io.@Print { text: "a direction" } }
                            when quit { emit *Vein.Console.Io.@Print { text: "bye" } }
                        }
                    }
                }
            }
            """);

        Assert.True(r.Success, Lines(r));
        Assert.Empty(r.Diagnostics);
    }

    [Fact]
    public void A_mark_arm_is_still_checked_as_a_mark()
    {
        // The arms VS0218 already checked: a bundle that declares marks has its `when #M` names checked.
        var r = Compile(Compass + """
                shard S {
                    run once {
                        match here() {
                            when #Dri { emit *Vein.Console.Io.@Print { text: "dir" } }
                        }
                    }
                }
            }
            """);

        Assert.Contains(r.Diagnostics, d => d.Code == "VS0218" && d.Message.Contains("#Dri"));
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "VS0242");
    }

    // ---- A2.4: a reserved word where a name goes (VS0396), and `T?` (VS0397) -------------------------------

    [Fact]
    public void A_reserved_variable_name_is_one_diagnostic_at_each_use_and_no_cascade()
    {
        // ParserRecoveryTests' reproduction: `count` failed inside shard A as "Expected name, found
        // KwCount", recovery skipped the rest of the shard, and its `}` then closed the bundle early —
        // "Expected 'bundle' or 'app'". Neither error said that `count` is a reserved word.
        var r = Compile("""
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

        Assert.All(r.Diagnostics, d => Assert.Equal("VS0396", d.Code));
        Assert.Equal(new[] { (3, 13), (4, 20) }, r.Diagnostics.Select(d => (d.Span.Line, d.Span.Col)));
        Assert.Equal("`count` is reserved in VeinScript; name it differently, e.g. `total`.", r.Diagnostics[0].Message);
    }

    [Theory]
    [InlineData("from")]
    [InlineData("target")]
    [InlineData("count")]
    [InlineData("use")]
    [InlineData("base")]
    [InlineData("on")]
    [InlineData("sync")]
    [InlineData("by")]
    [InlineData("shared")]
    [InlineData("to")]
    [InlineData("need")]
    [InlineData("map")]
    [InlineData("transform")]
    [InlineData("match")]
    [InlineData("type")]
    public void The_words_Vein_Cards_hit_are_reported_as_reserved_wherever_a_name_goes(string word)
    {
        // VEIN_REPORT #11: each of these, used as a variable, a parameter or a field, was "a parse error with
        // a cascade of follow-on errors". Declared, read, assigned, compared and passed — one diagnostic each.
        var r = Compile($$"""
            bundle T by me {
                fn twice({{word}}: int) return int { return {{word}} * 2 }
                shard S {
                    var {{word}} = 1
                    run once {
                        {{word}} = {{word}} + 1
                        {{word}} += 1
                        if {{word}} > 0 { emit *Vein.Console.Io.@Print { text: "n=" + twice({{word}}) } }
                    }
                    hear *Vein.Console.Io.@Input as {{word}} { }
                }
            }
            """);

        Assert.True(r.Diagnostics.Count > 0, $"`{word}` as a name should be reported");
        Assert.All(r.Diagnostics, d =>
        {
            Assert.Equal("VS0396", d.Code);
            Assert.StartsWith($"`{word}` is reserved in VeinScript", d.Message);
        });
        Assert.Equal(9, r.Diagnostics.Count);
    }

    [Theory]
    [InlineData("hear *Vein.Console.Io.@Input as to { }", "to")]
    [InlineData("run once { target [1, 2] as count { } }", "count")]
    [InlineData("run once { repeat 3 as from { } }", "from")]
    [InlineData("each tick as tick { }", "tick")]
    [InlineData("fn sync() return int { return 1 }", "sync")]
    public void A_reserved_binding_or_function_name_is_reported_where_it_is_written(string member, string word)
    {
        var r = Compile("bundle T by me {\n    shard S {\n        " + member + "\n    }\n}");

        var d = Assert.Single(r.Diagnostics);
        Assert.Equal("VS0396", d.Code);
        Assert.StartsWith($"`{word}` is reserved", d.Message);
        Assert.Equal(3, d.Span.Line);
    }

    [Fact]
    public void Every_reserved_word_names_a_replacement_that_compiles()
    {
        // The message is the fix, so the fix it names has to be a legal name — checked for every keyword the
        // lexer has, so a keyword added later cannot ship with a suggestion that is itself reserved.
        foreach (string word in Lexer.KeywordNames)
        {
            var r = Compile($"bundle T by me {{\n    shard S {{ run once {{ let {word} = 1 }} }}\n}}");
            var d = Assert.Single(r.Diagnostics);
            Assert.Equal("VS0396", d.Code);

            var suggested = Regex.Match(d.Message, "e\\.g\\. `(\\w+)`");
            Assert.True(suggested.Success, $"`{word}`: no replacement named in \"{d.Message}\"");
            var fixedUp = Compile($"bundle T by me {{\n    shard S {{ run once {{ let {suggested.Groups[1].Value} = 1 }} }}\n}}");
            Assert.True(fixedUp.Success && fixedUp.Diagnostics.Count == 0, $"`{word}` → `{suggested.Groups[1].Value}`: {Lines(fixedUp)}");
        }
    }

    [Fact]
    public void A_reserved_word_called_like_a_function_gets_the_builtin_it_meant()
    {
        var d = Assert.Single(Compile("bundle T by me {\n    shard S { run once { let n = count([1, 2]) } }\n}").Diagnostics);

        Assert.Equal("VS0396", d.Code);
        Assert.Contains("len(", d.Message);
    }

    [Fact]
    public void Contextual_words_stay_legal_names()
    {
        // `carries` is a word only before #/$, `has` only after a `.~` collection, `run`/`every` only at the
        // head of a shard member, `ordered` only before `by`… — so each is still a name a program may use.
        Assert.Equal("carries has run every once frame ordered owns add remove load socket slot columns", Run("""
            bundle T by me {
                shard S {
                    run once {
                        let carries = "carries"   let has = "has"   let run = "run"   let every = "every"
                        let once = "once"   let frame = "frame"   let ordered = "ordered"   let owns = "owns"
                        let add = "add"   let remove = "remove"   let load = "load"   let socket = "socket"
                        let slot = "slot"   let columns = "columns"
                        emit *Vein.Console.Io.@Print { text: carries + " " + has + " " + run + " " + every + " " + once +
                            " " + frame + " " + ordered + " " + owns + " " + add + " " + remove + " " + load + " " +
                            socket + " " + slot + " " + columns }
                    }
                }
            }
            """));
    }

    [Fact]
    public void Keywords_stay_legal_as_field_and_member_names()
    {
        // A field is only ever read after a `.`, where a keyword cannot be confused with its meaning.
        Assert.Equal("3 a b", Run("""
            bundle T by me {
                shape $Hop { count: int, from: string, to: string }
                mark #Hop
                builder Hopper { $Hop  mark #Hop }
                shard Boot { run once { bring Hopper(3, "a", "b") } }
                shard S { settled { target $Hop #Hop as h {
                    emit *Vein.Console.Io.@Print { text: "" + h.Hop.count + " " + h.Hop.from + " " + h.Hop.to }
                } } }
            }
            """));
    }

    [Fact]
    public void An_else_on_its_own_line_is_still_the_error_rule_2b_documents()
    {
        // `else` is not a name anyone chose: RULES 2b names this message, and it must not turn into
        // "`else` is reserved".
        var r = Compile("bundle T by me {\n    shard S { run once {\n        if true { }\n        else { }\n    } }\n}");

        Assert.Contains(r.Diagnostics, d => d.Code == "VS0104" && d.Message.Contains("'else'"));
        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "VS0396");
    }

    [Theory]
    [InlineData("fn f(x: int?) return int { return 1 }", "int")]
    [InlineData("fn f(x: int) return string? { return \"\" }", "string")]
    [InlineData("shape $S { hp: int?, name: string }", "int")]
    [InlineData("shard S { var who: Entity? = 0 }", "Entity")]
    [InlineData("fn f(xs: list<int?>) return int { return 0 }", "int")]
    public void A_nullable_type_is_one_diagnostic_saying_there_are_none(string declaration, string type)
    {
        var r = Compile("bundle T by me {\n    " + declaration + "\n}");

        var d = Assert.Single(r.Diagnostics);
        Assert.Equal("VS0397", d.Code);
        Assert.Equal(Severity.Error, d.Severity);
        Assert.Contains("no nullable types", d.Message);
        Assert.Contains($"`{type}`", d.Message);
    }

    [Fact]
    public void A_reserved_word_written_as_a_type_says_what_to_write()
    {
        // `map` is a keyword with no parser case; `map<K, V>` was "Expected type, found KwMap", and the shard
        // after it was lost to the cascade.
        var r = Compile("bundle T by me {\n    shard S {\n        var m: map<string, int>\n        run once { }\n    }\n}");

        var d = Assert.Single(r.Diagnostics);
        Assert.Equal("VS0396", d.Code);
        Assert.Contains("~<map>", d.Message);
        Assert.Contains("fromJson", d.Message);
    }
}
