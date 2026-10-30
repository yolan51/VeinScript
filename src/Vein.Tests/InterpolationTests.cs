using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Vein.Hosting;
using Xunit;

namespace Vein.Tests;

// `"HP: \(hp)"` — string interpolation (SYNTAX-DECISIONS D29). The lexer turns it into the tokens of
// `("HP: " + (hp) + "")`, so these tests are mostly about the edges of that rewrite: the first piece forcing
// text, expressions holding their own parentheses and strings, the escape, and one error for one mistake.
public class InterpolationTests
{
    private static (string Output, IReadOnlyList<string> Codes) Run(string body, string decls = "")
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by you {\n    need \"Vein.Console\"\n" + decls + "\n    shard S {\n        run once {\n" + body + "\n        }\n    }\n}"));
        var codes = r.Diagnostics.Select(d => d.Code).ToList();
        if (!r.Success) return ("", codes);

        var output = new StringWriter();
        new Interp { Ticks = 1 }.Run(r.Modules[0], new StringReader(""), output);
        return (output.ToString().Trim().ReplaceLineEndings("\n"), codes);
    }

    private static string Say(string body, string decls = "")
    {
        var (output, codes) = Run(body, decls);
        Assert.True(codes.Count == 0, string.Join(", ", codes));
        return output;
    }

    [Fact]
    public void A_value_is_written_into_the_text()
    {
        Assert.Equal("HP: 3 of 10", Say("let hp = 3\nlet max = 10\ndo print(\"HP: \\(hp) of \\(max)\")"));
    }

    [Fact]
    public void Two_numbers_side_by_side_join_as_text_not_add()
    {
        // The first piece is always a string, even "", so `+` joins rather than adds.
        Assert.Equal("12", Say("let a = 1\nlet b = 2\ndo print(\"\\(a)\\(b)\")"));
        Assert.Equal("3", Say("let a = 1\nlet b = 2\ndo print(\"\\(a + b)\")"));
    }

    [Fact]
    public void A_value_prints_exactly_as_joining_it_with_plus_does()
    {
        Assert.Equal("1.5|1.5\n2|2\ntrue|true",
            Say("do print(\"\\(1.5)|\" + 1.5)\ndo print(\"\\(2)|\" + 2)\ndo print(\"\\(true)|\" + true)"));
    }

    [Fact]
    public void An_expression_may_hold_parentheses_calls_and_strings_of_its_own()
    {
        Assert.Equal("[ann!] has 3 letters (9)", Say(
            "let name = \"ann\"\ndo print(\"[\\(name + \"!\")] has \\(len(name)) letters (\\((1 + 2) * 3))\")"));
    }

    [Fact]
    public void A_string_inside_an_interpolation_may_interpolate_too()
    {
        Assert.Equal("<b>x=4</b>", Say("let x = 4\ndo print(\"<b>\\(\"x=\\(x)\")</b>\")"));
    }

    [Fact]
    public void A_field_of_an_identity_and_of_an_event_can_be_shown()
    {
        string decls = "    shape $Life { hp: int }\n    event @Hit { by: int }\n" +
                       "    shard Show { hear @Hit as h { target $Life as l { do print(\"hit by \\(h.by), \\(l.Life.hp) left\") } } }";
        string body = "let e = spawn()\nattach $Life to e { hp: 7 }\nemit @Hit { by: 2 }";
        Assert.Equal("hit by 2, 7 left", Say(body, decls));
    }

    [Fact]
    public void A_backslash_before_a_parenthesis_is_written_twice()
    {
        Assert.Equal("\\(x) stays text", Say("do print(\"\\\\(x) stays text\")"));
    }

    [Fact]
    public void A_string_without_interpolation_lexes_as_it_always_did()
    {
        var tokens = new Vein.Compiler.Lexing.Lexer("let s = \"a\\tb \\\"q\\\"\"", "t.vein", new Vein.Compiler.Diagnostics.DiagnosticBag()).Tokenize();
        var s = Assert.Single(tokens, t => t.Kind == Vein.Compiler.Lexing.TokenKind.String);
        Assert.Equal("a\tb \"q\"", s.Value);
        Assert.DoesNotContain(tokens, t => t.Kind == Vein.Compiler.Lexing.TokenKind.LParen);
    }

    [Theory]
    [InlineData("do print(\"empty \\() here\")", "VS0398")]
    [InlineData("do print(\"open \\(1 + 2 here\")", "VS0399")]
    [InlineData("do print(\"open \\(len(\"ab\") + 1 here\")", "VS0399")]
    [InlineData("do print(\"no end \\(1 // here\")", "VS0399")]
    public void A_broken_interpolation_is_one_error_not_a_cascade(string body, string code)
    {
        var (_, codes) = Run(body);
        Assert.Equal(new[] { code }, codes);
    }

    [Fact]
    public void An_editor_colours_the_pieces_as_text_and_the_expression_as_code()
    {
        const string line = "do print(\"HP: \\(hp) of \\(42)\")";
        var tokens = VeinSyntax.Classify(line);
        string Text(VeinToken t) => line.Substring(t.Column, t.Length);

        Assert.Contains(tokens, t => t.Kind == VeinTokenKind.String && Text(t) == "\"HP: ");
        Assert.Contains(tokens, t => t.Kind == VeinTokenKind.String && Text(t) == " of ");
        Assert.Contains(tokens, t => t.Kind == VeinTokenKind.String && Text(t) == "\"");
        Assert.Contains(tokens, t => t.Kind == VeinTokenKind.Number && Text(t) == "42");
        Assert.DoesNotContain(tokens, t => t.Kind == VeinTokenKind.String && Text(t).Contains("hp"));
    }
}
