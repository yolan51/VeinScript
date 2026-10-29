using Vein.Compiler.Diagnostics;
using Vein.Compiler.Project;
using Vein.Compiler.Service;
using Vein.Compiler.Tooling;

namespace Vein.Hosting;

/// One completion, ready to apply (VeinEditorSession.Completions): what it reads, what it types and from
/// where, the list's kind, its tooltip and `shared("…")` line, and what else a pick writes.
public sealed record VeinCompletionRow(string Label, string Insert, int Start, string Kind, string? Detail, string? Doc,
    IReadOnlyList<(int At, string Text)> Extras);

/// The editor's help for one open `.vein` file — completion, `?`, signature help, hover, go to definition,
/// references, the outline and quick fixes — for an engine's own script editor (Godot's). The answers are
/// EditorService's and QuestionFill's, the same ones the Workbench and `veinc lsp` give, so a third editor
/// cannot answer differently; this only runs each of them where the engine can find what the file needs.
///
/// WHY A HOST NEEDS THIS. The editor's answers look `need`ed bundles up lazily, from the project folder.
/// Inside an engine nothing above that folder holds the stdlib — it is unpacked to StdlibCache — and the
/// library packages sit outside the project too (VeinLibraries). Every call here runs with both in reach.
///
/// Offsets are 0-based into the text; lines and columns are 1-based, as diagnostics are.
public sealed class VeinEditorSession
{
    private readonly VeinCompilerService _service = new();

    /// The file the text belongs to, on disk: its folder is where the project's bundles are found.
    public string Path { get; }

    /// The folder project-wide questions (references) search — the project's root.
    public string? Root { get; }

    public VeinEditorSession(string path, string? root = null)
    {
        Path = System.IO.Path.GetFullPath(path);
        Root = root ?? System.IO.Path.GetDirectoryName(Path);
    }

    /// The last analysis, or null before the first Update.
    public EditorSnapshot? Snapshot { get; private set; }

    public string Text => Snapshot?.Text ?? "";

    public IReadOnlyList<Diagnostic> Diagnostics => Snapshot?.Diagnostics ?? Array.Empty<Diagnostic>();

    /// Analyse `text` (the editor's, unsaved edits included). Cheap to call again with the same text.
    public EditorSnapshot Update(string text)
    {
        if (Snapshot is { } s && s.Text == text) return s;
        using var _ = Reach();
        return Snapshot = EditorService.Analyze(_service, text, Path, System.IO.Path.GetDirectoryName(Path), Root);
    }

    /// Whether typing `typed` may open a list — cheap, asked before any analysis.
    public static bool MayComplete(string text, int offset, char typed) => EditorService.MayComplete(text, offset, typed);

    /// The list at `offset` of `text`: `typed` is the character just typed (the caret after it), or null when
    /// it is asked for (Ctrl+Space).
    public EditorCompletionList? Complete(string text, int offset, char? typed)
    {
        var s = Update(text);
        using var _ = Reach();
        return EditorService.Complete(s, offset, typed);
    }

    /// The list at `caret`, as an editor applies it: each row replaces `[Start, caret)` with `Insert`, and
    /// when it is picked, its `Extras` go in too — `do ` before an SF call (D15), the `need` line a bundle
    /// not yet needed asks for. Every extra is at or before `Start`, at an offset of the text before the pick.
    /// Empty when nothing is worth offering (a `typed` key that opens no list included).
    public IReadOnlyList<VeinCompletionRow> Completions(string text, int caret, char? typed)
    {
        if (typed is { } c && !MayComplete(text, caret, c)) return Array.Empty<VeinCompletionRow>();
        if (Complete(text, caret, typed) is not { } list) return Array.Empty<VeinCompletionRow>();
        var rows = new List<VeinCompletionRow>();
        foreach (var item in list.Items)
        {
            int start = SourceEdits.CompletionStart(text, list.ReplaceFrom, item.Insert);
            var extras = new List<(int At, string Text)>();
            if (item.NeedsDo && SourceEdits.DoBefore(text, start) is { } call) extras.Add((call, "do "));
            if (item.AddNeed is { } bundle && NeedEdit.For(text, bundle) is var (at, line)) extras.Add((at, line));
            rows.Add(new VeinCompletionRow(item.Label, item.Insert, start, list.Kind, item.Describe, item.Doc, extras));
        }
        return rows;
    }

    public string? Hover(string text, int offset)
    {
        var s = Update(text);
        using var _ = Reach();
        return EditorService.Hover(s, offset);
    }

    public EditorSignature? Signature(string text, int offset)
    {
        var s = Update(text);
        using var _ = Reach();
        return EditorService.Signature(s, offset);
    }

    public EditorLocation? Definition(string text, int line, int col)
    {
        var s = Update(text);
        using var _ = Reach();
        (line, col) = Anchor(s, line, col);
        return EditorService.Definition(s, line, col);
    }

    public IReadOnlyList<EditorLocation> References(string text, int line, int col)
    {
        var s = Update(text);
        using var _ = Reach();
        (line, col) = Anchor(s, line, col);
        return EditorService.References(s, line, col);
    }

    /// Where on the line the site under the pointer is recorded. A statement's site spans from its keyword —
    /// `emit @Sold` is recorded at `emit` — so a Ctrl+click on `Sold` itself found nothing; the word under
    /// the pointer, named by a site on the same line, is that site.
    private static (int Line, int Col) Anchor(EditorSnapshot s, int line, int col)
    {
        if (s.Definitions.At(line, col) is not null) return (line, col);
        string[] lines = s.Text.Split('\n');
        if (line < 1 || line > lines.Length) return (line, col);
        int offset = lines.Take(line - 1).Sum(l => l.Length + 1) + Math.Clamp(col - 1, 0, lines[line - 1].Length);
        var (word, _) = EditorService.WordAt(s.Text, offset);
        if (word is null) return (line, col);
        var site = s.Definitions.Sites.FirstOrDefault(x => x.Span.Line == line && x.Name == word);
        return site is null ? (line, col) : (line, site.Span.Col);
    }

    /// The file's declarations, for an outline.
    public IReadOnlyList<SymbolSite> Symbols(string text)
    {
        var s = Update(text);
        using var _ = Reach();
        return EditorService.Symbols(s);
    }

    /// The certain fixes for one of the file's diagnostics.
    public IReadOnlyList<QuickFix> Fixes(string text, Diagnostic diagnostic)
    {
        var s = Update(text);
        using var _ = Reach();
        return EditorService.Fixes(s, diagnostic);
    }

    /// What the `?` just typed at offset `q` of `text` does — the whole payload of an event, the arguments of
    /// a builder, the fields of a shape — or null when that `?` is only a character (inside a string, after
    /// a word). Every outcome replaces or removes the `?`: it is a key, never syntax. Resolved against the
    /// text WITHOUT it, as the Workbench does, so a `?` where the parser would stop costs nothing around it.
    public FillOutcome? Question(string text, int q)
    {
        if (q < 0 || q >= text.Length || text[q] != '?') return null;
        using var _ = Reach();
        string without = text.Remove(q, 1);
        string dir = System.IO.Path.GetDirectoryName(Path)!;
        var ast = _service.Compile(new CompileRequest(System.IO.Path.GetFileName(Path), without, ProjectDir: dir, SourcePath: Path)).Ast;
        int line = 1 + text.AsSpan(0, q).Count('\n');
        return QuestionFill.Plan(text, q, Signatures.For(ast, dir, line));
    }

    /// The stdlib and the library packages, in reach for one call.
    private static IDisposable Reach() => new Both(BundleSearch.WithFallback(StdlibCache.Root), BundleSearch.WithLibraries(VeinLibraries.Roots));

    private sealed class Both(IDisposable first, IDisposable second) : IDisposable
    {
        public void Dispose() { second.Dispose(); first.Dispose(); }
    }
}
