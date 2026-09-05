using Vein.Cloud;
using Xunit;

namespace Vein.Tests;

// The assistant runs from a grammar digest maintained separately from this compiler, so the two can
// drift — and the failure mode is source that reads plausibly, goes into the editor, and does not
// compile. The Workbench holds the only authority on that question, so it asks before offering.
//
// [Collection] because `VeinCloudClient.Hook` is one static field shared by three test classes.
[Collection("Cloud")]
public class AssistantTests : IDisposable
{
    private readonly List<CloudRequest> _sent = new();

    public AssistantTests() => VeinCloudClient.Hook = null;
    public void Dispose() => VeinCloudClient.Hook = null;

    private void Reply(string markdown)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new { reply = markdown });
        VeinCloudClient.Hook = req => { _sent.Add(req); return new CloudResponse(200, json, null); };
    }

    private static CloudSession Signed => new("tok", "u1", "a@b.test", "Alice", "user");

    // ---- the request ------------------------------------------------------------------------------

    [Fact]
    public async Task The_question_and_the_history_tail_go_out_with_the_bearer()
    {
        Reply("Sure.");

        var history = Enumerable.Range(0, 14)
            .Select(i => new ChatTurn(i % 2 == 0 ? "user" : "assistant", $"turn {i}"))
            .ToList();

        await AssistantApi.AskAsync(Signed, "how do I hear an event?", history);

        Assert.EndsWith("/workbenchChat", _sent[0].Url);
        Assert.Equal("tok", _sent[0].Bearer);

        using var body = System.Text.Json.JsonDocument.Parse(_sent[0].Body!);

        // Only the last ten are read by the service, so only ten are sent.
        Assert.Equal(10, body.RootElement.GetProperty("history").GetArrayLength());
        Assert.Equal("turn 4", body.RootElement.GetProperty("history")[0].GetProperty("content").GetString());
        Assert.Equal("how do I hear an event?", body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task An_empty_or_oversized_question_is_refused_before_a_round_trip()
    {
        Reply("never reached");

        await Assert.ThrowsAsync<CloudException>(() => AssistantApi.AskAsync(Signed, "   "));
        await Assert.ThrowsAsync<CloudException>(() =>
            AssistantApi.AskAsync(Signed, new string('x', AssistantApi.MaxMessage + 1)));

        Assert.Empty(_sent);
    }

    // ---- fenced blocks ----------------------------------------------------------------------------

    [Fact]
    public void Only_vein_tagged_fences_are_taken()
    {
        // An untagged fence in an answer about VeinScript is as likely to be a shell line or a JSON
        // payload, and compiling those would produce confident nonsense about code never meant to.
        var blocks = AssistantApi.ExtractVeinBlocks("""
            Try this:

            ```vein
            shard S { run once { } }
            ```

            and run it with

            ```bash
            veinc run s.vein
            ```
            """);

        Assert.Single(blocks);
        Assert.Contains("shard S", blocks[0]);
    }

    [Fact]
    public void A_truncated_reply_still_yields_its_block()
    {
        var blocks = AssistantApi.ExtractVeinBlocks("Here:\n\n```vein\nshard S { run once { } }\n");

        Assert.Single(blocks);
        Assert.Contains("shard S", blocks[0]);
    }

    [Fact]
    public void No_code_means_no_blocks_rather_than_an_empty_one()
    {
        Assert.Empty(AssistantApi.ExtractVeinBlocks("A shard hears an event and reacts to it."));
        Assert.Empty(AssistantApi.ExtractVeinBlocks(""));
    }

    // ---- the check --------------------------------------------------------------------------------

    [Fact]
    public async Task A_bare_fragment_is_wrapped_so_the_check_answers_the_real_question()
    {
        // `shard S { }` alone is a guaranteed VS0101. Wrapping it is what makes the answer mean "would
        // this work inside my bundle?" rather than "is this a whole file?".
        Reply("""
            Here you go:

            ```vein
            shard Greeter {
                run once { emit *Vein.Console.Io.@Print { text: "hello" } }
            }
            ```
            """);

        var answer = await AssistantApi.AskAsync(Signed, "print hello");

        Assert.Single(answer.Blocks);
        Assert.True(answer.Blocks[0].Wrapped);
        Assert.True(answer.Blocks[0].Compiles, answer.Blocks[0].Summary());
        Assert.True(answer.HasUsableCode);

        // And what is offered for insertion is what the assistant wrote, not the wrapper.
        Assert.StartsWith("shard Greeter", answer.Blocks[0].Source);
    }

    [Fact]
    public async Task A_whole_bundle_is_checked_as_written()
    {
        Reply("""
            ```vein
            bundle Greet by you {
                shard S { run once { emit *Vein.Console.Io.@Print { text: "hi" } } }
            }
            ```
            """);

        var answer = await AssistantApi.AskAsync(Signed, "a bundle please");

        Assert.False(answer.Blocks[0].Wrapped);
        Assert.True(answer.Blocks[0].Compiles, answer.Blocks[0].Summary());
    }

    [Fact]
    public async Task A_suggestion_in_the_wrong_grammar_is_shown_as_not_compiling()
    {
        // This is the guide's own example output, and none of it is this language: `works on`,
        // `reads/writes`, an unsigilled event name, `query near`, and `end` as a block terminator.
        // Silently dropping it would be wrong; pasting it into the editor would be worse. It is
        // offered with the reason it will not build.
        Reply("""
            ```vein
            shard Flee {
              works on #Self
              reads/writes Position
              hears EnemyNear
              each tick {
                let target = query near #Enemy 6
                when target.count > 0 {
                  Position.x = Position.x - 1
                end
              end
            }
            ```
            """);

        var answer = await AssistantApi.AskAsync(Signed, "make things flee");

        Assert.Single(answer.Blocks);
        Assert.False(answer.Blocks[0].Compiles);
        Assert.False(answer.HasUsableCode);
        Assert.NotEmpty(answer.Blocks[0].Summary());

        // Still returned, so the panel can show it with its errors rather than pretending the
        // assistant said nothing.
        Assert.Contains("shard Flee", answer.Blocks[0].Source);
    }

    [Fact]
    public async Task Prose_with_no_code_is_carried_through_untouched()
    {
        Reply("A `hear` block runs when the event it names is emitted.");

        var answer = await AssistantApi.AskAsync(Signed, "what is hear?");

        Assert.Empty(answer.Blocks);
        Assert.False(answer.HasUsableCode);
        Assert.Contains("hear", answer.Reply);
    }

    [Fact]
    public void Trimming_keeps_the_tail()
    {
        var transcript = Enumerable.Range(0, 30).Select(i => new ChatTurn("user", $"t{i}")).ToList();

        var kept = AssistantApi.Trim(transcript);

        Assert.Equal(20, kept.Count);
        Assert.Equal("t10", kept[0].Content);
        Assert.Equal("t29", kept[^1].Content);
    }
}
