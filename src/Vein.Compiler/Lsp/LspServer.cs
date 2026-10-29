using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Service;
using Vein.Compiler.Tooling;

namespace Vein.Compiler.Lsp;

/// `veinc lsp` — the Language Server Protocol over stdio, so VS Code (or any LSP editor) gets what the
/// Workbench has. It holds NO editor logic of its own: every answer is <see cref="EditorService"/>'s, the same
/// one the Workbench renders, so the two editors cannot drift. This class only speaks the protocol — framing,
/// positions, and the JSON shapes each method wants.
///
/// The transport is JSON-RPC with `Content-Length` framing, on System.Text.Json: the protocol subset is small,
/// and owning it costs less than a dependency would.
public sealed class LspServer
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly TextWriter? _log;
    private readonly int _debounceMs;
    private readonly object _write = new();
    private readonly object _gate = new();   // one compile at a time: the read loop and the diagnostics timer
    private readonly VeinCompilerService _service = new();
    private readonly Dictionary<string, OpenDoc> _docs = new(StringComparer.Ordinal);
    private string? _root;
    private bool _shutdown;

    private sealed class OpenDoc
    {
        public required string Uri { get; init; }
        public required string Text { get; set; }
        public EditorSnapshot? Snapshot { get; set; }
        public Timer? Pending { get; set; }
    }

    /// The token kinds `semanticTokens` reports, in the order their indexes refer to. Semantic colour adds what
    /// the TextMate grammar cannot know: an fn versus an SF, a schedule header, a `*Author.Bundle` path.
    public static readonly string[] TokenTypes = { "function", "method", "keyword", "namespace", "type" };

    /// The characters that open a list as they are typed — the same ones the Workbench opens on.
    public static readonly string[] TriggerCharacters = { "$", "#", "@", "&", "*", ".", "~", "<", " ", "!" };

    public LspServer(Stream input, Stream output, TextWriter? log = null, int debounceMs = 300)
    {
        _input = input; _output = output; _log = log; _debounceMs = debounceMs;
    }

    /// Serve until `exit`. 0 after a `shutdown` request, 1 for an exit without one (as the protocol asks), or
    /// when the client goes away.
    public int Run()
    {
        while (true)
        {
            JsonObject? message;
            try { message = Read(); }
            catch (Exception ex) { Log("read failed: " + ex.Message); return 1; }
            if (message is null) return _shutdown ? 0 : 1;

            var id = message["id"]?.DeepClone();
            string method = message["method"]?.GetValue<string>() ?? "";
            try
            {
                if (method == "exit") return _shutdown ? 0 : 1;
                var result = Handle(method, message["params"] as JsonObject, out bool known);
                if (id is null) continue;   // a notification: nothing goes back
                if (known) Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
                else Send(Error(id, -32601, $"{method} is not supported"));
            }
            catch (Exception ex)
            {
                Log($"{method} failed: {ex}");
                if (id is not null) Send(Error(id, -32603, ex.Message));
            }
        }
    }

    private static JsonObject Error(JsonNode id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    // ---- dispatch -------------------------------------------------------------------------------------

    private JsonNode? Handle(string method, JsonObject? p, out bool known)
    {
        known = true;
        switch (method)
        {
            case "initialize": return Initialize(p);
            case "initialized": return null;
            case "shutdown": _shutdown = true; return null;

            case "textDocument/didOpen":
            {
                var td = p!["textDocument"]!;
                string uri = td["uri"]!.GetValue<string>();
                var doc = new OpenDoc { Uri = uri, Text = td["text"]!.GetValue<string>() };
                lock (_gate) _docs[uri] = doc;
                Schedule(doc);
                return null;
            }
            case "textDocument/didChange":
            {
                if (Doc(p) is not { } doc) return null;
                // Full sync: the last change holds the whole text.
                if (p!["contentChanges"] is JsonArray { Count: > 0 } changes && changes[^1]?["text"] is { } text)
                    lock (_gate) doc.Text = text.GetValue<string>();
                Schedule(doc);
                return null;
            }
            case "textDocument/didClose":
            {
                if (Doc(p) is not { } doc) return null;
                lock (_gate) { doc.Pending?.Dispose(); _docs.Remove(doc.Uri); }
                Notify("textDocument/publishDiagnostics", new JsonObject { ["uri"] = doc.Uri, ["diagnostics"] = new JsonArray() });
                return null;
            }

            case "textDocument/completion": return With(p, Completion);
            case "textDocument/hover": return With(p, Hover);
            case "textDocument/signatureHelp": return With(p, Signature);
            case "textDocument/definition": return With(p, Definition);
            case "textDocument/references": return With(p, References);
            case "textDocument/documentSymbol": return With(p, DocumentSymbols);
            case "textDocument/codeAction": return With(p, CodeActions);
            case "textDocument/inlayHint": return With(p, InlayHints);
            case "textDocument/semanticTokens/full": return With(p, SemanticTokens);

            default:
                known = false;
                return null;
        }
    }

    private JsonNode Initialize(JsonObject? p)
    {
        string? rootUri = p?["rootUri"]?.GetValue<string>()
                          ?? (p?["workspaceFolders"] as JsonArray)?.FirstOrDefault()?["uri"]?.GetValue<string>();
        _root = rootUri is null ? p?["rootPath"]?.GetValue<string>() : PathOf(rootUri);
        Log("root: " + (_root ?? "(none)"));

        var triggers = new JsonArray(); foreach (var t in TriggerCharacters) triggers.Add(t);
        var types = new JsonArray(); foreach (var t in TokenTypes) types.Add(t);
        return new JsonObject
        {
            ["capabilities"] = new JsonObject
            {
                ["textDocumentSync"] = new JsonObject { ["openClose"] = true, ["change"] = 1 },
                ["completionProvider"] = new JsonObject { ["triggerCharacters"] = triggers, ["resolveProvider"] = false },
                ["hoverProvider"] = true,
                ["signatureHelpProvider"] = new JsonObject { ["triggerCharacters"] = new JsonArray("(", ",", "{") },
                ["definitionProvider"] = true,
                ["referencesProvider"] = true,
                ["documentSymbolProvider"] = true,
                ["codeActionProvider"] = true,
                ["inlayHintProvider"] = true,
                ["semanticTokensProvider"] = new JsonObject
                {
                    ["legend"] = new JsonObject { ["tokenTypes"] = types, ["tokenModifiers"] = new JsonArray() },
                    ["full"] = true,
                },
            },
            ["serverInfo"] = new JsonObject { ["name"] = "veinc lsp" },
        };
    }

    /// Run `answer` against the open document `p` names, at its position when it has one.
    private JsonNode? With(JsonObject? p, Func<OpenDoc, EditorSnapshot, int, JsonObject, JsonNode?> answer)
    {
        if (Doc(p) is not { } doc) return null;
        lock (_gate)
        {
            var snapshot = Snapshot(doc);
            int offset = p!["position"] is { } pos
                ? LspPositions.ToOffset(snapshot.Text, pos["line"]!.GetValue<int>(), pos["character"]!.GetValue<int>())
                : 0;
            return answer(doc, snapshot, offset, p);
        }
    }

    private OpenDoc? Doc(JsonObject? p)
    {
        string? uri = p?["textDocument"]?["uri"]?.GetValue<string>();
        lock (_gate) return uri is not null && _docs.TryGetValue(uri, out var d) ? d : null;
    }

    /// The document's text compiled — once per version. Called under `_gate`.
    private EditorSnapshot Snapshot(OpenDoc doc)
    {
        if (doc.Snapshot is { } s && ReferenceEquals(s.Text, doc.Text)) return s;
        string? path = PathOf(doc.Uri);
        string? dir = path is null ? _root : Path.GetDirectoryName(path);
        return doc.Snapshot = EditorService.Analyze(_service, doc.Text, path, dir, _root ?? dir);
    }

    // ---- diagnostics ---------------------------------------------------------------------------------

    /// Diagnostics a moment after the last keystroke, not on every one: a compile per character would make
    /// typing wait on the compiler.
    private void Schedule(OpenDoc doc)
    {
        if (_debounceMs <= 0) { Publish(doc); return; }
        lock (_gate)
        {
            doc.Pending?.Dispose();
            doc.Pending = new Timer(_ => Publish(doc), null, _debounceMs, Timeout.Infinite);
        }
    }

    private void Publish(OpenDoc doc)
    {
        JsonArray list;
        lock (_gate)
        {
            if (!_docs.ContainsKey(doc.Uri)) return;
            var snapshot = Snapshot(doc);
            string? file = PathOf(doc.Uri) is { } path ? Path.GetFileName(path) : null;
            list = new JsonArray();
            foreach (var d in snapshot.Diagnostics)
            {
                // A merged fragment's diagnostic belongs to that file, not to this one's line numbers.
                if (!string.IsNullOrEmpty(d.Span.File) && file is not null && !string.Equals(d.Span.File, file, StringComparison.OrdinalIgnoreCase)
                    && d.Span.File != "untitled.vein") continue;
                list.Add(ToLsp(d));
            }
        }
        Notify("textDocument/publishDiagnostics", new JsonObject { ["uri"] = doc.Uri, ["diagnostics"] = list });
    }

    private static JsonObject ToLsp(Diagnostic d) => new()
    {
        ["range"] = SpanRange(d.Span.Line, d.Span.Col, Math.Max(1, d.Span.Length)),
        ["severity"] = d.Severity switch { Severity.Error => 1, Severity.Warning => 2, _ => 3 },
        ["code"] = d.Code,
        ["source"] = "veinc",
        ["message"] = d.Message,
    };

    // ---- the methods ---------------------------------------------------------------------------------

    private JsonNode? Completion(OpenDoc doc, EditorSnapshot s, int offset, JsonObject p)
    {
        // Typed a trigger character, or asked (Ctrl+Space) — the same split the Workbench makes.
        char? typed = p["context"]?["triggerKind"]?.GetValue<int>() == 2
                      && p["context"]?["triggerCharacter"]?.GetValue<string>() is { Length: 1 } t ? t[0] : null;
        if (typed is { } c && !EditorService.MayComplete(s.Text, offset, c)) return new JsonArray();
        if (EditorService.Complete(s, offset, typed) is not { } list) return new JsonArray();

        var items = new JsonArray();
        int order = 0;
        foreach (var item in list.Items)
        {
            int start = SourceEdits.CompletionStart(s.Text, list.ReplaceFrom, item.Insert);
            var row = new JsonObject
            {
                ["label"] = item.Label,
                ["kind"] = CompletionKind(list.Kind),
                ["sortText"] = (order++).ToString("D5"),
                // What the typed text is matched against: the name the row starts with when the sigil is part
                // of what gets replaced (a `*path` takes the typed `@` with it), else what is typed.
                ["filterText"] = start < list.ReplaceFrom ? item.Label.Split(' ', 2)[0] : item.Insert,
                ["textEdit"] = new JsonObject { ["range"] = Range(s.Text, start, offset), ["newText"] = item.Insert },
            };
            // The first line of the tooltip beside the row; the author's `shared("…")` sentence (or the rest of
            // the tooltip) underneath, when the row is selected.
            if (item.Describe is { Length: > 0 } describe) row["detail"] = describe.Split('\n')[0];
            string? documentation = item.Doc ?? (item.Describe is { } d && d.Contains('\n') ? d : null);
            if (documentation is { Length: > 0 }) row["documentation"] = documentation;
            var extra = new JsonArray();
            if (item.NeedsDo && DoEdit(s.Text, start) is { } edit) extra.Add(edit);
            if (item.AddNeed is { } bundle && NeedEdit.For(s.Text, bundle) is var (needAt, needText))
                extra.Add(new JsonObject { ["range"] = Range(s.Text, needAt, needAt), ["newText"] = needText });
            if (extra.Count > 0) row["additionalTextEdits"] = extra;
            items.Add(row);
        }
        return items;
    }

    private static int CompletionKind(string kind) => kind switch
    {
        "shape" => 22, "mark" => 21, "event" => 23, "builder" => 4, "shard" => 7,
        "function" or "SF" or "stdlib" => 3, "member" => 5, "collection" => 2, "collection kind" => 14, _ => 1,
    };

    /// `do ` in front of an SF call that starts its statement (D15) — the Workbench's VeinCompletion.AddDo.
    private static JsonObject? DoEdit(string text, int nameStart) =>
        SourceEdits.DoBefore(text, nameStart) is { } call
            ? new JsonObject { ["range"] = Range(text, call, call), ["newText"] = "do " }
            : null;

    private JsonNode? Hover(OpenDoc doc, EditorSnapshot s, int offset, JsonObject p) =>
        EditorService.Hover(s, offset) is { } text
            ? new JsonObject { ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = "```vein\n" + text + "\n```" } }
            : null;

    private JsonNode? Signature(OpenDoc doc, EditorSnapshot s, int offset, JsonObject p)
    {
        if (EditorService.Signature(s, offset) is not { } sig) return null;
        var ps = new JsonArray(); foreach (var x in sig.Parameters) ps.Add(new JsonObject { ["label"] = x });
        return new JsonObject
        {
            ["signatures"] = new JsonArray(new JsonObject { ["label"] = sig.Label, ["parameters"] = ps }),
            ["activeSignature"] = 0,
            ["activeParameter"] = Math.Clamp(sig.Active, 0, Math.Max(0, sig.Parameters.Count - 1)),
        };
    }

    private JsonNode? Definition(OpenDoc doc, EditorSnapshot s, int offset, JsonObject p)
    {
        var (line, ch) = LspPositions.ToPosition(s.Text, offset);
        var found = EditorService.Definition(s, line + 1, ch + 1);
        Log("definition: " + (found?.Text ?? "nothing under the caret"));
        return found is null || found.Line == 0 ? null : Location(doc, found);
    }

    private JsonNode? References(OpenDoc doc, EditorSnapshot s, int offset, JsonObject p)
    {
        var (line, ch) = LspPositions.ToPosition(s.Text, offset);
        var list = new JsonArray();
        foreach (var hit in EditorService.References(s, line + 1, ch + 1)) list.Add(Location(doc, hit));
        return list;
    }

    private JsonNode? DocumentSymbols(OpenDoc doc, EditorSnapshot s, int offset, JsonObject p)
    {
        string? file = PathOf(doc.Uri) is { } path ? Path.GetFileName(path) : null;
        var list = new JsonArray();
        foreach (var d in EditorService.Symbols(s))
        {
            if (file is not null && !string.IsNullOrEmpty(d.Span.File) && !string.Equals(d.Span.File, file, StringComparison.OrdinalIgnoreCase)) continue;
            var range = SpanRange(d.Span.Line, d.Span.Col, Math.Max(1, d.Span.Length));
            list.Add(new JsonObject
            {
                ["name"] = Sigil(d.Kind) + d.Name,
                ["detail"] = d.Kind.ToString(),
                ["kind"] = SymbolKindOf(d.Kind),
                ["range"] = range,
                ["selectionRange"] = range.DeepClone(),
            });
        }
        return list;
    }

    private static string Sigil(Project.SymbolKind k) => k switch
    {
        Project.SymbolKind.Shape => "$", Project.SymbolKind.Mark => "#", Project.SymbolKind.Event => "@",
        Project.SymbolKind.Builder => "&", Project.SymbolKind.Shard => "!", _ => "",
    };

    private static int SymbolKindOf(Project.SymbolKind k) => k switch
    {
        Project.SymbolKind.Shape => 23, Project.SymbolKind.Mark => 14, Project.SymbolKind.Event => 24,
        Project.SymbolKind.Builder => 9, Project.SymbolKind.Shard or Project.SymbolKind.ShardView or Project.SymbolKind.Bridge => 5,
        Project.SymbolKind.Fn => 12, Project.SymbolKind.SF => 6, _ => 13,
    };

    private JsonNode? CodeActions(OpenDoc doc, EditorSnapshot s, int offset, JsonObject p)
    {
        var actions = new JsonArray();
        if (p["context"]?["diagnostics"] is not JsonArray asked) return actions;
        foreach (var lspDiag in asked.OfType<JsonObject>())
        {
            string? code = lspDiag["code"]?.ToString();
            int line = lspDiag["range"]?["start"]?["line"]?.GetValue<int>() ?? -1;
            int ch = lspDiag["range"]?["start"]?["character"]?.GetValue<int>() ?? -1;
            var ours = s.Diagnostics.FirstOrDefault(d => d.Code == code && d.Span.Line == line + 1 && d.Span.Col == ch + 1);
            if (ours is null) continue;
            foreach (var fix in EditorService.Fixes(s, ours))
            {
                string target = fix.File is null ? doc.Uri : new Uri(fix.File).AbsoluteUri;
                string targetText = fix.File is null ? s.Text : (File.Exists(fix.File) ? File.ReadAllText(fix.File) : "");
                actions.Add(new JsonObject
                {
                    ["title"] = fix.Title,
                    ["kind"] = "quickfix",
                    ["diagnostics"] = new JsonArray(lspDiag.DeepClone()),
                    ["edit"] = new JsonObject
                    {
                        ["changes"] = new JsonObject
                        {
                            [target] = new JsonArray(new JsonObject
                            {
                                ["range"] = Range(targetText, fix.Edit.Offset, fix.Edit.Offset + fix.Edit.Length),
                                ["newText"] = fix.Edit.Text,
                            }),
                        },
                    },
                });
            }
        }
        return actions;
    }

    private JsonNode? InlayHints(OpenDoc doc, EditorSnapshot s, int offset, JsonObject p)
    {
        var list = new JsonArray();
        foreach (var (at, label) in EditorService.Hints(s))
        {
            var (line, ch) = LspPositions.ToPosition(s.Text, at);
            list.Add(new JsonObject
            {
                ["position"] = new JsonObject { ["line"] = line, ["character"] = ch },
                ["label"] = label,
                ["kind"] = 1,
                ["paddingRight"] = true,
            });
        }
        return list;
    }

    private JsonNode? SemanticTokens(OpenDoc doc, EditorSnapshot s, int offset, JsonObject p)
    {
        var data = new JsonArray();
        int lastLine = 0, lastCol = 0;
        foreach (var paint in SemanticPaint.Analyze(s.Text).OrderBy(x => x.Line).ThenBy(x => x.Col))
        {
            int line = paint.Line - 1, col = paint.Col - 1;
            int type = paint.Role switch
            {
                SemanticPaint.Role.Fn => 0, SemanticPaint.Role.SF => 1, SemanticPaint.Role.Schedule => 2,
                SemanticPaint.Role.Shard => 4, _ => 3,
            };
            data.Add(line - lastLine);
            data.Add(line == lastLine ? col - lastCol : col);
            data.Add(paint.Length);
            data.Add(type);
            data.Add(0);
            lastLine = line; lastCol = col;
        }
        return new JsonObject { ["data"] = data };
    }

    // ---- shapes ----------------------------------------------------------------------------------------

    private static JsonObject Range(string text, int from, int to)
    {
        var (l1, c1) = LspPositions.ToPosition(text, from);
        var (l2, c2) = LspPositions.ToPosition(text, to);
        return new JsonObject
        {
            ["start"] = new JsonObject { ["line"] = l1, ["character"] = c1 },
            ["end"] = new JsonObject { ["line"] = l2, ["character"] = c2 },
        };
    }

    /// A compiler span (1-based line and column) as an LSP range on one line.
    private static JsonObject SpanRange(int line, int col, int length) => new()
    {
        ["start"] = new JsonObject { ["line"] = Math.Max(0, line - 1), ["character"] = Math.Max(0, col - 1) },
        ["end"] = new JsonObject { ["line"] = Math.Max(0, line - 1), ["character"] = Math.Max(0, col - 1 + length) },
    };

    private static JsonObject Location(OpenDoc doc, EditorLocation at) => new()
    {
        ["uri"] = at.File is null ? doc.Uri : new Uri(Path.GetFullPath(at.File)).AbsoluteUri,
        ["range"] = SpanRange(at.Line, at.Col, 0),
    };

    /// A `file:` URI as a local path; null for anything else (an untitled buffer).
    public static string? PathOf(string uri)
    {
        try { var u = new Uri(uri); return u.IsFile ? u.LocalPath : null; }
        catch { return null; }
    }

    // ---- framing -----------------------------------------------------------------------------------------

    private JsonObject? Read()
    {
        int length = -1;
        while (true)
        {
            string? header = ReadHeaderLine();
            if (header is null) return null;
            if (header.Length == 0) break;
            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(header["Content-Length:".Length..].Trim());
        }
        if (length < 0) return null;
        var body = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = _input.Read(body, read, length - read);
            if (n <= 0) return null;
            read += n;
        }
        Log("<- " + Encoding.UTF8.GetString(body));
        return JsonNode.Parse(body) as JsonObject;
    }

    private string? ReadHeaderLine()
    {
        var bytes = new List<byte>();
        while (true)
        {
            int b = _input.ReadByte();
            if (b < 0) return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
            if (b == '\n') return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
            bytes.Add((byte)b);
        }
    }

    private void Notify(string method, JsonObject @params) =>
        Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = @params });

    private void Send(JsonObject message)
    {
        string json = message.ToJsonString();
        var body = Encoding.UTF8.GetBytes(json);
        var head = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        lock (_write)
        {
            _output.Write(head);
            _output.Write(body);
            _output.Flush();
        }
        Log("-> " + (json.Length > 400 ? json[..400] + "…" : json));
    }

    private void Log(string line)
    {
        if (_log is null) return;
        lock (_log) { _log.WriteLine(line); _log.Flush(); }
    }
}
