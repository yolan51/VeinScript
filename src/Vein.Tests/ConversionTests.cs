using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// `int(x)`, `float(x)`, `string(x)`, `bool(x)` and `isNumber(x)`.
//
// Named after the types, which reads as a cast and cost no new syntax: `int` and friends are not
// keywords — the parser only ever meets them in type position — so `int(x)` already parsed as a call
// and simply resolved to nothing. It compiled clean, ran, and printed nothing at all, which is the
// worst of the three possible outcomes.
public class ConversionTests
{
    private static string Say(string expr)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by you {\n    shard S {\n        run once {\n" +
            $"            emit *Vein.Console.Io.@Print {{ text: \"\" + ({expr}) }}\n" +
            "        }\n    }\n}"));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        var output = new StringWriter();
        new Interp { Ticks = 1 }.Run(r.Modules[0], new StringReader(""), output);
        return output.ToString().Trim();
    }

    // ---- int --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("int(\"42\")", "42")]
    [InlineData("int(\"  7  \")", "7")]          // a typed line arrives with its whitespace
    [InlineData("int(\"-3\")", "-3")]
    [InlineData("int(42)", "42")]
    [InlineData("int(true)", "1")]
    public void Int_reads_a_whole_number(string expr, string expected) => Assert.Equal(expected, Say(expr));

    [Fact]
    public void Int_truncates_toward_zero_rather_than_rounding()
    {
        // `int(x)` is asked for most often to index or to count, and 4.9 items is four.
        Assert.Equal("4", Say("int(\"4.9\")"));
        Assert.Equal("4", Say("int(4.9)"));
        Assert.Equal("-4", Say("int(-4.9)"));
    }

    [Theory]
    [InlineData("int(\"abc\")")]
    [InlineData("int(\"\")")]
    [InlineData("int(\"12abc\")")]
    public void Unparseable_text_is_zero_rather_than_a_crash(string expr)
    {
        // Total, like `substring` clamping and `indexOf` answering -1. A conversion that could throw
        // would need guarding at every use, and a language with no `catch` has nowhere to put it.
        Assert.Equal("0", Say(expr));
    }

    // ---- float ------------------------------------------------------------------------------------

    [Fact]
    public void Float_parses_a_decimal_point_whatever_the_machine_thinks()
    {
        // InvariantCulture on purpose: parsed against the local culture, "1.5" would read as 15 on a
        // machine whose separator is a comma — so a program would read its own saved files differently
        // depending on where it ran.
        Assert.Equal("1.5", Say("float(\"1.5\")"));
        Assert.Equal("0", Say("float(\"1,5\")"));
    }

    [Fact]
    public void Float_of_something_unreadable_is_zero()
    {
        Assert.Equal("0", Say("float(\"abc\")"));
        Assert.Equal("0", Say("float(\"\")"));
    }

    // ---- string -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("string(7)", "7")]
    [InlineData("string(1.5)", "1.5")]
    [InlineData("string(true)", "true")]
    [InlineData("string(\"already\")", "already")]
    public void String_gives_the_text_a_value_prints_as(string expr, string expected) =>
        Assert.Equal(expected, Say(expr));

    [Fact]
    public void String_matches_what_concatenation_already_produced()
    {
        // `"" + x` was the only way to do this before, so the two must not disagree — otherwise a
        // program's output would change depending on which one someone reached for.
        Assert.Equal(Say("\"\" + 1.5"), Say("string(1.5)"));
        Assert.Equal(Say("\"\" + false"), Say("string(false)"));
    }

    [Fact]
    public void Int_saturates_rather_than_wrapping_past_the_end_of_the_range()
    {
        // `(long)d` for a value outside long's range answers long.MinValue, so this used to print
        // -9223372036854775808: a number too big to hold came back as a large NEGATIVE one, which then
        // compared and summed as though it were real. Saturating is at least on the right side of zero.
        Assert.Equal("9223372036854775807", Say("int(\"999999999999999999999\")"));
        Assert.Equal("-9223372036854775808", Say("int(\"-999999999999999999999\")"));
    }

    [Fact]
    public void String_renders_a_list_rather_than_its_dotnet_type_name()
    {
        // `"x = " + someList` used to print `System.Collections.Generic.List` and a mangled type
        // argument — .NET showing through a language that has no .NET in it. `join` is still how you
        // render a list on purpose; this is what it looks like when you did not ask.
        Assert.Equal("[a, b]", Say("string(chars(\"ab\"))"));
        Assert.Equal("[]", Say("string(chars(\"\"))"));
    }

    // ---- bool and isNumber ------------------------------------------------------------------------

    [Theory]
    [InlineData("bool(1)", "true")]
    [InlineData("bool(0)", "false")]
    [InlineData("bool(true)", "true")]
    [InlineData("bool(\"x\")", "true")]
    [InlineData("bool(\"\")", "false")]          // empty text is falsy, exactly as `if ""` already was
    public void Bool_follows_truthiness_for_everything_that_is_not_a_written_answer(string expr, string expected)
        => Assert.Equal(expected, Say(expr));

    [Theory]
    [InlineData("bool(\"false\")", "false")]
    [InlineData("bool(\"FALSE\")", "false")]
    [InlineData("bool(\"no\")", "false")]
    [InlineData("bool(\"off\")", "false")]
    [InlineData("bool(\" false \")", "false")]   // as it arrives from a typed line
    [InlineData("bool(\"true\")", "true")]
    [InlineData("bool(\"yes\")", "true")]
    [InlineData("bool(\"on\")", "true")]
    [InlineData("bool(\"0\")", "false")]         // a number in text answers as that number would
    [InlineData("bool(\"0.0\")", "false")]
    [InlineData("bool(\"3\")", "true")]
    public void Bool_reads_a_written_answer_rather_than_asking_whether_there_is_text(string expr, string expected)
    {
        // THIS IS THE ONE CONVERSION THAT IS NOT TRUTHINESS. `bool(x)` used to call `Truthy`, under which
        // every non-empty string is true — so `bool("false")` was TRUE, and a program that read "false"
        // from the console or a saved file got the opposite of what was written, silently.
        //
        // `if s` still means "is there text here"; that rule is right for a condition. It is wrong for a
        // conversion, because the thing being converted is nearly always something a person wrote down.
        Assert.Equal(expected, Say(expr));
    }

    [Fact]
    public void Bool_falls_back_to_truthiness_for_a_string_that_is_not_an_answer()
    {
        // The parse recognises words and numbers. Anything else keeps the old behaviour, so `bool` and
        // `if` still agree on every string that was never a written yes or no.
        Assert.Equal("true", Say("bool(\"cat\")"));
        Assert.Equal("true", Say("bool(\"falsey\")"));
        Assert.Equal("true", Say("bool(\"0 apples\")"));
    }

    [Theory]
    [InlineData("isNumber(\"42\")", "true")]
    [InlineData("isNumber(\"4.9\")", "true")]
    [InlineData("isNumber(\" 7 \")", "true")]
    [InlineData("isNumber(\"abc\")", "false")]
    [InlineData("isNumber(\"\")", "false")]
    [InlineData("isNumber(\"12abc\")", "false")]
    [InlineData("isNumber(42)", "true")]
    public void IsNumber_is_how_you_tell_zero_from_nonsense(string expr, string expected)
    {
        // THE REASON IT EXISTS. `int("abc")` is 0 and so is `int("0")` — a total function buys its
        // safety by making failure indistinguishable from a real answer. Without this the pair would be
        // the `fromJson`-returns-null trap wearing a different name.
        Assert.Equal(expected, Say(expr));
    }

    [Fact]
    public void IsNumber_says_no_to_a_bool()
    {
        // `bool` converts to 0 or 1, but the question here is "is this text a number" — answering yes
        // for `true` would let a menu accept it as a choice.
        Assert.Equal("false", Say("isNumber(true)"));
    }

    // ---- the reason this was asked for ------------------------------------------------------------

    [Fact]
    public void A_typed_menu_choice_can_now_be_a_number()
    {
        // Console input arrives as text, and there was no way to turn it into a number — which is why
        // the RPG menu had to compare `choice == "3"` rather than doing arithmetic with it.
        string output = Say("int(trim(\"  3  \")) * 10");

        Assert.Equal("30", output);
    }

    [Fact]
    public void A_guarded_parse_reads_as_one_line()
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", """
            bundle T by you {
                shard S {
                    run once {
                        let typed = "  12  "
                        if isNumber(typed) {
                            emit *Vein.Console.Io.@Print { text: "got " + (int(typed) + 1) }
                        } else {
                            emit *Vein.Console.Io.@Print { text: "not a number" }
                        }
                    }
                }
            }
            """));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        var output = new StringWriter();
        new Interp { Ticks = 1 }.Run(r.Modules[0], new StringReader(""), output);

        Assert.Contains("got 13", output.ToString());
    }
}
