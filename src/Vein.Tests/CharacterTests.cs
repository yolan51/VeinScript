using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// A character is a ONE-CHARACTER STRING — `s[0]` yields one and the ordinary operators work on it. No
// `char` type, no `'a'` literal, no shape and no builder, because a shape is for data an identity
// carries and a character carried by nothing is just a value.
//
// The bug these start with is the reason any of this needed doing: comparison went through AsDouble,
// which is 0 for a non-numeric string, so an is-a-letter test was true for everything.
public class CharacterTests
{
    private static string Run(string body)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", "bundle T by you {\n" + body + "\n}"));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        var output = new StringWriter();
        new Interp { Ticks = 1 }.Run(r.Modules[0], new StringReader(""), output);
        return output.ToString();
    }

    private static string Once(string statements) =>
        Run("    shard S {\n        run once {\n" + statements + "\n        }\n    }");

    private static string Say(string expr) =>
        $"            emit *Vein.Console.Io.@Print {{ text: \"\" + ({expr}) }}";

    [Fact]
    public void A_range_test_rejects_what_is_not_in_the_range()
    {
        // THE REGRESSION. `c >= "a" and c <= "z"` was `0 >= 0 and 0 <= 0` — true for "!", for "5", for
        // everything — and it failed silently, which is what made it worth finding.
        Assert.Contains("true", Once(Say("\"m\" >= \"a\" and \"m\" <= \"z\"")));
        Assert.Contains("false", Once(Say("\"!\" >= \"a\" and \"!\" <= \"z\"")));
        Assert.Contains("false", Once(Say("\"5\" >= \"a\" and \"5\" <= \"z\"")));
    }

    [Fact]
    public void Strings_order_ordinally()
    {
        Assert.Contains("true", Once(Say("\"a\" < \"b\"")));
        Assert.Contains("false", Once(Say("\"b\" < \"a\"")));

        // Capitals sort before lowercase — ordinal, never culture-sensitive. `EntityStore.OrderKey`
        // already sorts this way so the C# backend agrees; an operator that disagreed with the sort
        // would be a second answer to the same question.
        Assert.Contains("true", Once(Say("\"Z\" < \"a\"")));
    }

    [Fact]
    public void Numbers_still_compare_as_numbers()
    {
        // The fix must not turn arithmetic into text ordering. `10 < 9` is false; `"10" < "9"` is true,
        // because "1" precedes "9" — both are right, for different operands.
        Assert.Contains("false", Once(Say("10 < 9")));
        Assert.Contains("true", Once(Say("\"10\" < \"9\"")));
    }

    [Fact]
    public void A_numeric_string_against_a_number_compares_as_the_number()
    {
        // This used to pin the opposite, as a known wart: only BOTH-strings had switched to ordinal, so
        // a mixed comparison still ran through AsDouble — which answers 0 for anything that is not a
        // number. `"5" < 3` was `0 < 3`, TRUE, and `"5" > 3` was false. Every ordering that mixed text
        // and a number silently agreed the text was zero.
        //
        // It was left that way because making it parse would have been a new loose coercion reaching
        // every existing program, and it wanted deciding on its own. `int(x)` is what decided it: the
        // language can now say what a numeric string means, so the comparison saying something else
        // has no defence left.
        Assert.Contains("true", Once(Say("\"5\" > 3")));
        Assert.Contains("false", Once(Say("\"5\" < 3")));
        Assert.Contains("true", Once(Say("\"4.5\" < 5")));
    }

    [Fact]
    public void Text_that_is_not_a_number_falls_back_to_comparing_text()
    {
        // Not to zero. `"abc"` against `3` compares "abc" with "3" ordinally — deterministic, and the
        // same place `==` lands when it cannot compare numerically.
        Assert.Contains("true", Once(Say("\"abc\" > 3")));    // 'a' (97) after '3' (51)
        Assert.Contains("false", Once(Say("\"abc\" < 3")));
    }

    [Fact]
    public void Both_strings_still_order_ordinally()
    {
        // The half that must NOT change. Character comparison depends on it (RULES 28) and
        // EntityStore.OrderKey sorts the same way, so an operator that disagreed would be a second
        // answer to the same question.
        Assert.Contains("true", Once(Say("\"10\" < \"9\"")));
        Assert.Contains("true", Once(Say("\"a\" < \"b\"")));
    }

    [Fact]
    public void A_character_comes_out_of_a_string_by_index()
    {
        Assert.Contains("#", Once(Say("\"# heading\"[0]")));
        Assert.Contains("true", Once(Say("\"# heading\"[0] == \"#\"")));
    }

    [Fact]
    public void Chars_walks_a_string_one_character_at_a_time()
    {
        Assert.Contains("3", Once(Say("len(chars(\"abc\"))")));

        // The point of returning a list: `target` walks it like anything else.
        Assert.Contains("h|e|y|", Once(
            "            var acc: string\n" +
            "            acc = \"\"\n" +
            "            target chars(\"hey\") as c { acc = acc + c + \"|\" }\n" +
            "            emit *Vein.Console.Io.@Print { text: acc }"));
    }

    [Fact]
    public void Code_and_chr_round_trip()
    {
        Assert.Contains("97", Once(Say("code(\"a\")")));
        Assert.Contains("b", Once(Say("chr(98)")));
        Assert.Contains("a", Once(Say("chr(code(\"a\"))")));

        // The next letter — arithmetic ordering alone cannot do.
        Assert.Contains("b", Once(Say("chr(code(\"a\") + 1)")));
    }

    [Fact]
    public void Code_of_an_empty_string_is_zero_rather_than_a_crash()
    {
        // Half-written input is normal; a runtime is not a place to crash a user's console app.
        Assert.Contains("0", Once(Say("code(\"\")")));
    }

    [Fact]
    public void Case_folding_is_how_you_compare_case_insensitively()
    {
        // Comparison is ordinal, so "A" and "a" are different characters. `lower(x) == lower(y)` is one
        // call rather than a second comparison operator that quietly means something else.
        Assert.Contains("false", Once(Say("\"Zeta\" == \"zeta\"")));
        Assert.Contains("true", Once(Say("lower(\"Zeta\") == lower(\"zeta\")")));
        Assert.Contains("HI", Once(Say("upper(\"hi\")")));
    }

    [Fact]
    public void A_classifier_is_an_ordinary_function()
    {
        // The argument against a `char` keyword: with ordering fixed, every classifier anyone needs is
        // one comparison written in the language itself.
        string output = Run("""
                fn isDigit(c: string) -> bool { return c >= "0" and c <= "9" }
                shard S {
                    run once {
                        if isDigit("7") { emit *Vein.Console.Io.@Print { text: "7 yes" } }
                        if not isDigit("x") { emit *Vein.Console.Io.@Print { text: "x no" } }
                    }
                }
            """);

        Assert.Contains("7 yes", output);
        Assert.Contains("x no", output);
    }
}
