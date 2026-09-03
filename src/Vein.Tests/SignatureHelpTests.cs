using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// `bring Panel(?, ?, ?, ?)` says nothing about which `?` is which — an include flattens someone else's
// shape into the list, so the names are not even in this file, and positional binding means the slot
// carries no name. Signature help is worth more here than for an ordinary call.
//
// Caret positions are marked with `|` in these tests and stripped before the call.
public class SignatureHelpTests
{
    private static CallContext? At(string marked)
    {
        int caret = marked.IndexOf('|');
        Assert.True(caret >= 0, "mark the caret with |");
        return SignatureHelp.At(marked.Remove(caret, 1), caret);
    }

    [Fact]
    public void Inside_a_bring_names_the_builder()
    {
        var call = At("bring Panel(|");
        Assert.NotNull(call);
        Assert.Equal("Panel", call!.Name);
        Assert.False(call.IsEvent);
        Assert.Equal(0, call.ActiveSlot);
    }

    [Fact]
    public void The_slot_follows_the_commas()
    {
        Assert.Equal(0, At("bring Panel(1|")!.ActiveSlot);
        Assert.Equal(1, At("bring Panel(1, |")!.ActiveSlot);
        Assert.Equal(2, At("bring Panel(1, 2, |")!.ActiveSlot);
    }

    [Fact]
    public void A_nested_call_does_not_advance_the_outer_slot()
    {
        // `bring Panel(len(a, b), |` is in the OUTER slot 1, not slot 3.
        Assert.Equal(1, At("bring Panel(len(a, b), |")!.ActiveSlot);
    }

    [Fact]
    public void Inside_an_emit_payload_names_the_event()
    {
        var call = At("emit @Damaged { |");
        Assert.NotNull(call);
        Assert.Equal("Damaged", call!.Name);
        Assert.True(call.IsEvent);
    }

    [Fact]
    public void A_qualified_event_reduces_to_its_last_segment()
    {
        // Exactly how Interp routes: the *Author.Bundle.Publicator qualifier is dropped.
        var call = At("emit *Vein.Console.Io.@Print { |");
        Assert.NotNull(call);
        Assert.Equal("Print", call!.Name);
        Assert.True(call.IsEvent);
    }

    [Fact]
    public void A_plain_block_is_not_a_call()
    {
        // The failure this avoids: every `{` in the language would otherwise offer signature help for
        // whatever identifier happened to precede it.
        Assert.Null(At("shard S { |"));
        Assert.Null(At("run once { |"));
        Assert.Null(At("if x == 1 { |"));
    }

    [Fact]
    public void A_closed_call_is_not_still_open()
    {
        Assert.Null(At("bring Panel(1, 2)\n|"));
    }

    [Fact]
    public void A_bracket_inside_a_string_does_not_open_a_call()
    {
        // The web samples are full of `"<div>("`. Reading one as an open argument list would offer help
        // for the rest of the file.
        Assert.Null(At("emit @R { body: \"a ( b\" }\n|"));
    }

    [Fact]
    public void The_description_marks_the_active_slot()
    {
        var fields = new[]
        {
            new EventField("title", "string", true, null),
            new EventField("width", "int", false, "80")
        };

        string first = SignatureHelp.Describe(new CallContext("Panel", false, 0), fields)!;
        string second = SignatureHelp.Describe(new CallContext("Panel", false, 1), fields)!;

        Assert.Contains("[title: string]", first);
        Assert.Contains("[width: int = 80]", second);
        Assert.DoesNotContain("[title", second);
    }

    [Fact]
    public void A_builder_with_no_fields_says_so_rather_than_showing_empty_brackets()
    {
        Assert.Equal("&Empty — no fields",
            SignatureHelp.Describe(new CallContext("Empty", false, 0), Array.Empty<EventField>()));
    }
}
