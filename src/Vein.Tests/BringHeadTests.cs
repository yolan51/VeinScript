using System.Text.RegularExpressions;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// What the editor recognises as "a `bring` waiting for its arguments", which is what decides whether
// typing `?` expands anything.
//
// It was `\w+`, matching neither the `&` sigil nor a qualified path — so `bring &Unit ?` did nothing
// at all while `bring Unit ?` worked. Silent, and backwards: writing the sigil is the MORE explicit
// form, and it was the one the tooling ignored.
public class BringHeadTests
{
    private static string? Name(string before)
    {
        var m = Regex.Match(before, EventCatalog.BringHead + @"\s*$");
        return m.Success ? m.Groups[1].Value : null;
    }

    [Theory]
    [InlineData("bring Unit ")]
    [InlineData("bring &Unit ")]
    [InlineData("bring 8 Unit ")]
    [InlineData("bring 8 &Unit ")]
    [InlineData("bring *alice.Combat.Api.&Unit ")]
    [InlineData("bring *alice.Combat.Api.Unit ")]
    [InlineData("bring *Vein.Web.Html.&Unit ")]
    public void Every_form_bring_accepts_yields_the_bare_name(string before)
    {
        // The bare name is the right capture: `Builders` stores a `use`d bundle's builders under their
        // bare names, because that is how `bring Button(…)` resolves in the first place.
        Assert.Equal("Unit", Name(before));
    }

    [Theory]
    [InlineData("bring Unit")]          // no trailing space — the caret is right after the name
    [InlineData("bring &Unit")]
    [InlineData("            bring &Unit ")]   // indented, as it always is in a shard
    public void Spacing_and_indentation_do_not_matter(string before) => Assert.Equal("Unit", Name(before));

    [Fact]
    public void The_name_is_taken_from_the_end_so_earlier_text_does_not_confuse_it()
    {
        Assert.Equal("Panel", Name("        bring Unit(1, 2)\n        bring &Panel "));
    }

    [Theory]
    [InlineData("emit @Hit ")]          // a different statement entirely
    [InlineData("bring ")]              // nothing named yet
    [InlineData("let x = bringer ")]    // `bring` as a prefix of another word
    public void Something_that_is_not_a_bring_matches_nothing(string before) => Assert.Null(Name(before));

    [Fact]
    public void A_count_is_not_mistaken_for_the_name()
    {
        // `bring 8 Unit` brings eight of them; the digits are not a builder.
        Assert.Equal("Unit", Name("bring 8 Unit "));
        Assert.Equal("Unit", Name("bring 12 &Unit "));
    }

    [Fact]
    public void The_qualifier_is_consumed_whole_rather_than_leaving_its_last_segment()
    {
        // `[\w.]*` is greedy and would happily eat the final dot; the pattern has to backtrack so that
        // `Api` is part of the path and `Unit` is the name. Getting this wrong would look like it
        // worked — it would just fill in the wrong builder's fields.
        Assert.Equal("Unit", Name("bring *alice.Combat.Api.&Unit "));
        Assert.NotEqual("Api", Name("bring *alice.Combat.Api.&Unit "));
    }
}
