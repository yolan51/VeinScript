using System.Text;
using System.Text.Json.Nodes;
using Vein.Compiler.Lsp;
using Xunit;

namespace Vein.Tests;

// `veinc lsp` driven in-process with framed JSON, the way VS Code drives it. What is pinned: the framing,
// the handshake, diagnostics arriving for an opened document, and that each method answers at the right
// place — completion, hover, definition, inlay hints, semantic tokens — plus the UTF-16 position helper an
// off-by-one in would move every answer one character over.
public class LspTests
{
    private const string Uri = "untitled:test.vein";

    private const string Source =
        "bundle Game by you {\n" +                                                  // 0
        "    shape $Item { name: string, weight: float }\n" +                        // 1
        "    shape $Hero { name: string, ~<map> slots: string | owns $Item }\n" +    // 2
        "    mark #Hero\n" +                                                         // 3
        "    builder Hero { $Hero mark #Hero }\n" +                                  // 4
        "    event @Show { }\n" +                                                    // 5
        "    shard S {\n" +                                                          // 6
        "        hear @Show as e {\n" +                                              // 7
        "            target $Hero as h { h.Hero.~slots.clear() }\n" +                // 8
        "        }\n" +                                                              // 9
        "    }\n" +                                                                  // 10
        "}\n";

    private static string Frame(JsonObject m)
    {
        string json = m.ToJsonString();
        return $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n{json}";
    }

    private static JsonObject Request(int id, string method, JsonObject? p = null) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p ?? new JsonObject() };

    private static JsonObject Note(string method, JsonObject? p = null) =>
        new() { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = p ?? new JsonObject() };

    private static JsonObject At(int line, int ch) => new()
    {
        ["textDocument"] = new JsonObject { ["uri"] = Uri },
        ["position"] = new JsonObject { ["line"] = line, ["character"] = ch },
    };

    /// Runs a session: initialize, open `text`, then `requests`, shutdown, exit. Returns every message sent back.
    private static (int Exit, List<JsonObject> Out) Session(string text, params JsonObject[] requests)
    {
        var input = new StringBuilder();
        input.Append(Frame(Request(1, "initialize", new JsonObject { ["rootUri"] = null })));
        input.Append(Frame(Note("initialized")));
        input.Append(Frame(Note("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri, ["languageId"] = "vein", ["version"] = 1, ["text"] = text },
        })));
        foreach (var r in requests) input.Append(Frame(r));
        input.Append(Frame(Request(999, "shutdown")));
        input.Append(Frame(Note("exit")));

        var output = new MemoryStream();
        int exit = new LspServer(new MemoryStream(Encoding.UTF8.GetBytes(input.ToString())), output, debounceMs: 0).Run();
        return (exit, Parse(output.ToArray()));
    }

    private static List<JsonObject> Parse(byte[] bytes)
    {
        var list = new List<JsonObject>();
        string all = Encoding.UTF8.GetString(bytes);
        int i = 0;
        while (i < all.Length)
        {
            int headEnd = all.IndexOf("\r\n\r\n", i, StringComparison.Ordinal);
            if (headEnd < 0) break;
            int length = int.Parse(all[(all.IndexOf(':', i) + 1)..headEnd].Trim());
            byte[] body = Encoding.UTF8.GetBytes(all[(headEnd + 4)..]);
            string json = Encoding.UTF8.GetString(body, 0, length);
            list.Add((JsonObject)JsonNode.Parse(json)!);
            i = headEnd + 4 + json.Length;
        }
        return list;
    }

    private static JsonNode? Result(List<JsonObject> outp, int id) =>
        outp.First(m => m["id"]?.GetValue<int>() == id)["result"];

    [Fact]
    public void The_handshake_advertises_what_the_workbench_does_and_exits_cleanly()
    {
        var (exit, outp) = Session(Source);
        Assert.Equal(0, exit);
        var caps = Result(outp, 1)!["capabilities"]!;
        Assert.Equal(1, caps["textDocumentSync"]!["change"]!.GetValue<int>());
        Assert.Contains("~", caps["completionProvider"]!["triggerCharacters"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.True(caps["inlayHintProvider"]!.GetValue<bool>());
        Assert.NotNull(caps["semanticTokensProvider"]);
    }

    [Fact]
    public void An_opened_document_gets_its_diagnostics()
    {
        string broken = Source.Replace("h.Hero.~slots.clear()", "h.Hero.~slots.Add(1)");
        var (_, outp) = Session(broken);
        var published = outp.Last(m => m["method"]?.GetValue<string>() == "textDocument/publishDiagnostics");
        var diags = published["params"]!["diagnostics"]!.AsArray();
        Assert.Contains(diags, d => d!["code"]!.GetValue<string>() == "VS0375");
        Assert.Equal(8, diags.First(d => d!["code"]!.GetValue<string>() == "VS0375")!["range"]!["start"]!["line"]!.GetValue<int>());
    }

    [Fact]
    public void Completion_after_a_tilde_lists_the_collections_with_a_text_edit()
    {
        string text = Source.Replace("h.Hero.~slots.clear()", "h.Hero.~");
        int ch = "            target $Hero as h { h.Hero.~".Length;
        var req = Request(2, "textDocument/completion", At(8, ch));
        req["params"]!["context"] = new JsonObject { ["triggerKind"] = 2, ["triggerCharacter"] = "~" };
        var (_, outp) = Session(text, req);
        var items = Result(outp, 2)!.AsArray();
        var slots = Assert.Single(items);
        Assert.Equal("slots", slots!["textEdit"]!["newText"]!.GetValue<string>());
        Assert.Equal(ch, slots["textEdit"]!["range"]!["start"]!["character"]!.GetValue<int>());
    }

    [Fact]
    public void Hover_definition_hints_and_tokens_answer_at_the_right_place()
    {
        int onSlots = "            target $Hero as h { h.Hero.~sl".Length;
        var (_, outp) = Session(Source,
            Request(3, "textDocument/hover", At(8, onSlots)),
            Request(4, "textDocument/definition", At(8, 13)),
            Request(5, "textDocument/inlayHint", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = Uri } }),
            Request(6, "textDocument/semanticTokens/full", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = Uri } }),
            Request(7, "textDocument/documentSymbol", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = Uri } }));

        Assert.Contains("~<map> slots: string | owns $Item", Result(outp, 3)!["contents"]!["value"]!.GetValue<string>());

        var def = Result(outp, 4)!;
        Assert.Equal(Uri, def["uri"]!.GetValue<string>());
        Assert.Equal(2, def["range"]!["start"]!["line"]!.GetValue<int>());

        var hint = Assert.Single(Result(outp, 5)!.AsArray());
        Assert.Equal("‹map›", hint!["label"]!.GetValue<string>());
        Assert.Equal(8, hint["position"]!["line"]!.GetValue<int>());

        Assert.Equal(0, Result(outp, 6)!["data"]!.AsArray().Count % 5);
        Assert.Contains(Result(outp, 7)!.AsArray(), s => s!["name"]!.GetValue<string>() == "$Hero");
    }

    [Fact]
    public void An_unknown_request_is_an_error_not_silence()
    {
        var (_, outp) = Session(Source, Request(8, "textDocument/rename", At(0, 0)));
        Assert.Equal(-32601, outp.First(m => m["id"]?.GetValue<int>() == 8)["error"]!["code"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("ab\ncd", 1, 1, 4)]
    [InlineData("ab\r\ncd", 1, 0, 4)]
    [InlineData("ab\ncd", 0, 99, 2)]       // past a line's end clamps to it
    [InlineData("é→x\ny", 0, 2, 2)]        // UTF-16 units: a string index
    public void Positions_and_offsets_round_trip(string text, int line, int ch, int offset)
    {
        Assert.Equal(offset, LspPositions.ToOffset(text, line, ch));
        if (ch < 99) Assert.Equal((line, ch), LspPositions.ToPosition(text, offset));
    }
}
