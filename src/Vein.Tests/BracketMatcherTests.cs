using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// VeinScript nests deeply — bundle, shard, hear, target, if — so "which `}` closes what" is a real
// question. The part that makes this more than counting is that a brace inside a string or a comment is
// not a brace, and this codebase's own samples emit HTML, so `"<div>{"` is not a hypothetical.
public class BracketMatcherTests
{
    [Fact]
    public void The_caret_on_an_opening_brace_finds_its_partner()
    {
        //           0123456789
        const string s = "a { b } c";
        Assert.Equal((2, 6), BracketMatcher.Match(s, 2));
    }

    [Fact]
    public void The_caret_on_a_closing_brace_finds_its_partner()
    {
        const string s = "a { b } c";
        Assert.Equal((2, 6), BracketMatcher.Match(s, 6));
    }

    [Fact]
    public void A_caret_just_past_a_brace_still_counts_as_on_it()
    {
        // Where the caret lands after you type `}` is one past it, and that is the moment you most want
        // to see what it closed.
        const string s = "a { b } c";
        Assert.Equal((2, 6), BracketMatcher.Match(s, 7));
    }

    [Fact]
    public void Nesting_matches_the_right_depth()
    {
        //                 0         1
        //                 0123456789012345
        const string s = "{ a { b } c }";
        Assert.Equal((0, 12), BracketMatcher.Match(s, 0));
        Assert.Equal((4, 8), BracketMatcher.Match(s, 4));
    }

    [Fact]
    public void A_brace_in_a_string_is_not_a_brace()
    {
        // The failure this prevents: emit a `{` inside HTML and every brace after it points one level
        // wrong for the rest of the file. samples/web_site.vein does exactly this.
        const string s = "emit @R { body: \"<i>{</i>\" }";

        var pair = BracketMatcher.Match(s, s.IndexOf('{'));

        Assert.NotNull(pair);
        Assert.Equal(s.Length - 1, pair!.Value.Close);   // the real closer, not the one in the literal
    }

    [Fact]
    public void A_brace_in_a_comment_is_not_a_brace()
    {
        string s = "a {\n    // } not this one\n}";

        var pair = BracketMatcher.Match(s, 2);

        Assert.NotNull(pair);
        Assert.Equal(s.Length - 1, pair!.Value.Close);
    }

    [Fact]
    public void A_caret_on_a_brace_inside_a_string_matches_nothing()
    {
        const string s = "let t = \"{\"";
        Assert.Null(BracketMatcher.Match(s, s.IndexOf('{')));
    }

    [Fact]
    public void An_unclosed_brace_reports_nothing_rather_than_guessing()
    {
        // Half-written code is the normal state while typing; boxing a brace with a partner it does not
        // have would be worse than boxing nothing.
        Assert.Null(BracketMatcher.Match("shard S {", 8));
    }

    [Fact]
    public void The_caret_away_from_any_brace_matches_nothing()
    {
        Assert.Null(BracketMatcher.Match("let a = 1", 4));
    }

    [Fact]
    public void An_escaped_quote_does_not_end_the_string()
    {
        // `"a\"{"` is still inside the literal at the brace.
        const string s = "let t = \"a\\\"{\"";
        Assert.Null(BracketMatcher.Match(s, s.IndexOf('{')));
    }
}
