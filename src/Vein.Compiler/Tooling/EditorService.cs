using Vein.Compiler.Diagnostics;
using Vein.Compiler.Parsing;
using Vein.Compiler.Project;
using Vein.Compiler.Service;

namespace Vein.Compiler.Tooling;

/// One compile of an editor's text and everything an editor asks of it, built on demand: the definitions,
/// members, collections, signatures and builders. Plain text and offsets — no UI type — so the Workbench and
/// `veinc lsp` read the same answers.
public sealed class EditorSnapshot
{
    public EditorSnapshot(string text, string? path, string? projectDir, string? rootFolder, CompilationResult result)
    {
        Text = text; Path = path; ProjectDir = projectDir; RootFolder = rootFolder; Result = result;
    }

    public string Text { get; }
    /// The file on disk this text is (null for an untitled buffer).
    public string? Path { get; }
    /// The folder bundles are searched from — the file's own folder, or the open folder for an untitled one.
    public string? ProjectDir { get; }
    /// The open folder, for project-wide questions (references).
    public string? RootFolder { get; }
    public CompilationResult Result { get; }
    public CompilationUnit? Ast => Result.Ast;
    public IReadOnlyList<Diagnostic> Diagnostics => Result.Diagnostics;

    private DefinitionIndex? _definitions;
    private MemberIndex.Model? _members;
    private Signatures? _signatures;
    private IReadOnlyList<BuilderEntry>? _builders;

    public DefinitionIndex Definitions => _definitions ??= Ast is null ? DefinitionIndex.Empty : DefinitionIndex.Analyze(Ast);
    public MemberIndex.Model? Members => _members ??= Ast is null ? null : MemberIndex.Build(Ast);
    public CollectionIndex Collections => Members?.Collections ?? CollectionIndex.Build(null);
    public Signatures? Signatures => _signatures ??= Ast is null ? null : Signatures.For(Ast, ProjectDir);
    public IReadOnlyList<BuilderEntry> Builders => _builders ??= Safe(() => Ast is null ? new List<BuilderEntry>() : EventCatalog.Builders(Ast, ProjectDir)) ?? new List<BuilderEntry>();

    private static T? Safe<T>(Func<T> f) where T : class { try { return f(); } catch { return null; } }
}

/// A completion row: what it reads, what it types, the tooltip, the author's `shared("…")` line, and the
/// edits that ride along with a pick — `do ` in front of an SF (D15), a `need` line for a bundle not yet
/// needed (RULES 17b: a bare name at a use site resolves only through one).
public sealed record EditorCompletion(string Label, string Insert, string? Describe = null, string? Doc = null)
{
    public bool NeedsDo { get; init; }
    /// The bundle (`Author.Bundle`) a `need` line must name for this pick to resolve, or null.
    public string? AddNeed { get; init; }
}

/// A list of rows, the kind it is (`shape`, `member`, `collection`…), and where the word it replaces starts.
public sealed record EditorCompletionList(IReadOnlyList<EditorCompletion> Items, string Kind, int ReplaceFrom);

/// A call's signature as one line, its parameters as they appear in that line, and the one being typed.
public sealed record EditorSignature(string Label, IReadOnlyList<string> Parameters, int Active);

/// Where something is: a file (null for the text being edited) and a 1-based line and column.
public sealed record EditorLocation(string? File, int Line, int Col, string Text);

/// The editor's questions, answered from an <see cref="EditorSnapshot"/>. Each one is what the Workbench
/// already did inside its window, moved here unchanged so a second editor cannot answer differently.
public static class EditorService
{
    public static EditorSnapshot Analyze(VeinCompilerService service, string text, string? path, string? projectDir, string? rootFolder = null)
    {
        using var _ = BundleSearch.Scope(projectDir);
        string name = path is null ? "untitled.vein" : System.IO.Path.GetFileName(path);
        var result = service.Compile(new CompileRequest(name, text, ProjectDir: projectDir, SourcePath: path));
        return new EditorSnapshot(text, path, projectDir, rootFolder, result);
    }

    // ---- completion ----------------------------------------------------------------------------------

    /// The list to show at `offset`. `typed` is the character just typed (the caret is after it), or null
    /// when the list is asked for (Ctrl+Space): then it reads back over the half-typed word to whatever
    /// started it. Null when nothing is worth offering.
    public static EditorCompletionList? Complete(EditorSnapshot s, int offset, char? typed)
    {
        using var _ = BundleSearch.Scope(s.ProjectDir);
        string text = s.Text;
        offset = Math.Clamp(offset, 0, text.Length);

        if (typed is { } t)
        {
            switch (t)
            {
                case '.': return Members(s, offset);
                case '*': return Star(s, offset);
                case '&': return Builders(s, offset);
                case '~': return Collections(s, offset);
                case '<': return CollectionIndex.KindAt(text, offset) is { } k ? Kinds(k) : null;
                case '$': return Scope(s, offset, SymbolKind.Shape, "shape");
                case '#': return Scope(s, offset, SymbolKind.Mark, "mark");
                case '@': return Scope(s, offset, SymbolKind.Event, "event");
                // `!Shard` is taken only by an `audience` list (D19) — anywhere else it is a VS0006 typo.
                case '!': return LineAt(text, offset).Contains("audience", StringComparison.Ordinal) ? Shards(s, offset) : null;
                case ' ':
                    if (JustTyped(text, offset, "do")) return Sfs(s, offset);   // `do ` — which SF? (D15)
                    if (JustTyped(text, offset, "emit") || JustTyped(text, offset, "hear")) return EventsAfterKeyword(s, offset);
                    if (JustTyped(text, offset, "mute") || JustTyped(text, offset, "unmute")) return Shards(s, offset);   // D18
                    return null;
                default: return null;
            }
        }

        int start = offset;
        while (start > 0 && IsIdent(text[start - 1])) start--;
        if (start == 0) return Functions(s, start, offset);
        return text[start - 1] switch
        {
            '$' => Scope(s, start, SymbolKind.Shape, "shape"),
            '#' => Scope(s, start, SymbolKind.Mark, "mark"),
            '@' => Scope(s, start, SymbolKind.Event, "event"),
            '&' => Builders(s, start),
            '*' => Star(s, start),
            '.' => Members(s, start),
            '~' => Collections(s, offset),
            '<' when CollectionIndex.KindAt(text, offset) is { } k => Kinds(k),
            _ => Functions(s, start, offset),
        };
    }

    /// Whether typing `typed` (the caret now after it) can open a list — cheap, so an editor asks before it
    /// compiles anything for <see cref="Complete"/>.
    public static bool MayComplete(string text, int offset, char typed) => typed switch
    {
        '.' or '*' or '&' or '~' or '$' or '#' or '@' => true,
        '<' => CollectionIndex.KindAt(text, offset) is not null,
        '!' => LineAt(text, offset).Contains("audience", StringComparison.Ordinal),
        ' ' => JustTyped(text, offset, "do") || JustTyped(text, offset, "emit") || JustTyped(text, offset, "hear")
               || JustTyped(text, offset, "mute") || JustTyped(text, offset, "unmute"),
        _ => false,
    };

    /// The list behind `$`, `#` and `@` — everything of that kind reachable from here, each row naming the
    /// bundle it came from. A shape or mark at a use site (`target`, `mark`, `attach`, a `when` arm) MUST be
    /// bare (RULES 17b), so there the bare name is typed and picking one from an un-needed bundle also writes
    /// the `need`; everywhere else the qualified `*path` is typed, which says which of ten bundles it is.
    private static EditorCompletionList? Scope(EditorSnapshot s, int from, SymbolKind kind, string label)
    {
        if (s.Ast is null) return null;
        var entries = ScopeIndex.For(s.Ast, kind, s.ProjectDir);
        if (entries.Count == 0) return null;
        bool bareOnly = SourceContext.At(s.Text, from) == SourceContext.SigilSite.UseSite && kind is SymbolKind.Shape or SymbolKind.Mark;
        var items = entries.Select(e => new EditorCompletion(e.Label, bareOnly || e.Local ? e.Name : e.Qualified, e.Describe(label), e.Doc)
        {
            AddNeed = bareOnly && !e.Needed && !e.Local ? NeedKey(e.Origin) : null,
        }).ToList();
        return new EditorCompletionList(items, label, from);
    }

    /// `Author.Bundle[.Publicator]` → the `Author.Bundle` a `need` names.
    private static string? NeedKey(string? origin) =>
        origin?.Split('.') is { Length: >= 2 } parts ? parts[0] + "." + parts[1] : null;

    /// `&Builder` — the local builders with their parameter lists, then every shared builder on the search
    /// path, by its qualified path (which replaces the typed `&`).
    private static EditorCompletionList? Builders(EditorSnapshot s, int from)
    {
        var items = s.Builders
            .Select(b => new EditorCompletion(b.Fields.Count == 0 ? b.Name : b.Name + "   " + string.Join(", ", b.Fields.Select(f => f.Name + ": " + f.Type)), b.Name))
            .OrderBy(x => x.Insert, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var known = items.Select(x => x.Insert).ToHashSet(StringComparer.Ordinal);
        if (s.Ast is not null)
            foreach (var e in ScopeIndex.For(s.Ast, SymbolKind.Builder, s.ProjectDir))
                if (known.Add(e.Name)) items.Add(new EditorCompletion(e.Label, e.Qualified));
        return items.Count == 0 ? null : new EditorCompletionList(items, "builder", from);
    }

    /// `*` — the stdlib's and bundles' cross-bundle symbols as full qualified paths, filtered by the
    /// project's `vein.discovery` policy. An SF picked here gets its `do` too (D15).
    private static EditorCompletionList? Star(EditorSnapshot s, int from)
    {
        string? dir = s.Path is not null ? System.IO.Path.GetDirectoryName(s.Path) : s.RootFolder;
        var symbols = DiscoveryPolicy.Load(dir).Filter(StdlibIndex.Symbols(dir)).ToList();
        string PathOf(QualifiedSymbol q) => string.Join(".", q.PathSegments) + "." + q.Sigil + q.Name;
        var sfs = symbols.Where(q => q.Kind == SymbolKind.SF).Select(PathOf).ToHashSet(StringComparer.Ordinal);
        var items = symbols.Select(PathOf).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)
            .Select(n => new EditorCompletion(n, n) { NeedsDo = sfs.Contains(n) }).ToList();
        return items.Count == 0 ? null : new EditorCompletionList(items, "stdlib", from);
    }

    /// `do ` — every SF callable there: this file's own, then each bundle's by its qualified path.
    private static EditorCompletionList? Sfs(EditorSnapshot s, int from)
    {
        var items = new List<EditorCompletion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in s.Definitions.Definitions.Where(d => d.Kind == SymbolKind.SF).OrderBy(d => d.Name, StringComparer.Ordinal))
            if (seen.Add(d.Name)) items.Add(new EditorCompletion(d.Name + "   SF, this file", d.Name));
        string? dir = s.Path is not null ? System.IO.Path.GetDirectoryName(s.Path) : s.RootFolder;
        try
        {
            foreach (var q in DiscoveryPolicy.Load(dir).Filter(StdlibIndex.Symbols(dir)).Where(q => q.Kind == SymbolKind.SF)
                         .OrderBy(q => q.QualifiedName, StringComparer.Ordinal))
                if (seen.Add(q.QualifiedName)) items.Add(new EditorCompletion(q.QualifiedName, q.QualifiedName, null, q.Doc));
        }
        catch { /* no search path: this file's SFs are still worth offering */ }
        return items.Count == 0 ? null : new EditorCompletionList(items, "SF", from);
    }

    /// `mute ` / `unmute ` / an `audience` `!` — this bundle's shards, ShardViews and bridges. Only this
    /// bundle's: `mute` cannot reach another bundle's shard (D18).
    private static EditorCompletionList? Shards(EditorSnapshot s, int from)
    {
        var items = s.Definitions.Definitions
            .Where(d => d.Kind is SymbolKind.Shard or SymbolKind.ShardView or SymbolKind.Bridge)
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .Select(d => (d.Name + "   " + ShardKindWord(d.Kind), d.Name)).Distinct()
            .Select(x => new EditorCompletion(x.Item1, x.Name)).ToList();
        return items.Count == 0 ? null : new EditorCompletionList(items, "shard", from);
    }

    public static string ShardKindWord(SymbolKind k) =>
        k switch { SymbolKind.ShardView => "ShardView", SymbolKind.Bridge => "bridge", _ => "shard" };

    /// `emit ` / `hear ` — the events usable there, this file's own first, then every shared one.
    private static EditorCompletionList? EventsAfterKeyword(EditorSnapshot s, int from)
    {
        if (s.Ast is null) return null;
        var items = ScopeIndex.For(s.Ast, SymbolKind.Event, s.ProjectDir)
            .Select(e => new EditorCompletion(e.Label, e.Local || e.Needed ? e.SigilName : e.Qualified, e.Describe("event"), e.Doc)).ToList();
        return items.Count == 0 ? null : new EditorCompletionList(items, "event", from);
    }

    /// A plain word — the functions callable here, fns and SFs, this file's first. An SF row writes its `do`.
    private static EditorCompletionList? Functions(EditorSnapshot s, int wordStart, int offset)
    {
        if (s.Ast is null) return null;
        var items = new List<EditorCompletion>();
        foreach (var (kind, tag) in new[] { (SymbolKind.Fn, "fn"), (SymbolKind.SF, "SF") })
            foreach (var e in ScopeIndex.For(s.Ast, kind, s.ProjectDir))
            {
                string insert = e.Local || e.Needed ? e.Name : e.Qualified;
                items.Add(new EditorCompletion(tag + "  " + e.Label, insert,
                    tag + " " + e.Name + (e.Fields ?? "") + (e.Origin is null ? "" : "   " + e.Origin) + (e.Doc is { Length: > 0 } ? "\n" + e.Doc : ""), e.Doc)
                    { NeedsDo = kind == SymbolKind.SF });
            }
        return items.Count == 0 ? null : new EditorCompletionList(items, "function", wordStart);
    }

    /// After a `.` — a collection's words (`h.Hero.~slots.`), else the members after the chain: a hear
    /// binding's payload, a target binding's shapes, a shape's fields (a collection as `~name`).
    private static EditorCompletionList? Members(EditorSnapshot s, int from)
    {
        if (s.Members is not { } model) return null;
        if (CollectionIndex.At(s.Text, from) is { } coll && model.Collections.Spec(coll.Shape, coll.Field) is { } spec)
            return new EditorCompletionList(CollectionIndex.Words(spec)
                .Select(w => new EditorCompletion(w.Label, w.Insert, CollectionIndex.Describe(coll.Field, spec))).ToList(), "collection", from);
        int dot = from - 1;
        if (dot < 0 || s.Text[dot] != '.') return null;
        var tokens = Chain(s.Text, dot).Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return null;
        var items = model.Items(tokens).Select(i => new EditorCompletion(i.Label, i.Insert)).ToList();
        return items.Count == 0 ? null : new EditorCompletionList(items, "member", from);
    }

    /// `h.Hero.~` — the shape's collections only, each with its kind (D24).
    private static EditorCompletionList? Collections(EditorSnapshot s, int offset)
    {
        if (CollectionIndex.Naming(s.Text, offset) is not { } naming) return null;
        var items = s.Collections.Of(naming.Shape)
            .Select(c => new EditorCompletion($"{c.Field}   ‹{CollectionIndex.Tag(c.Spec)}› {CollectionIndex.Types(c.Spec)}", c.Field,
                                              CollectionIndex.Describe(c.Field, c.Spec)))
            .ToList();
        return items.Count == 0 ? null : new EditorCompletionList(items, "collection", naming.NameStart);
    }

    /// `~<` in a declaration — the four kinds, each saying what it keeps (D24).
    private static EditorCompletionList Kinds(int start) =>
        new(CollectionIndex.Kinds.Select(k => new EditorCompletion(k.Label, k.Insert, k.Describe, k.Describe)).ToList(), "collection kind", start);

    /// The receiver text immediately before a `.` (identifiers, dots, and a leading `::`).
    private static string Chain(string text, int dotOffset)
    {
        int i = dotOffset - 1;
        while (i >= 0 && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '.' or ':')) i--;
        return text.Substring(i + 1, dotOffset - (i + 1));
    }

    /// True when `offset` sits just after `<word> ` at the start of a statement — first on its line, or
    /// after a `{` — so `emit ` opens the event list and `demit ` does not.
    public static bool JustTyped(string text, int offset, string word)
    {
        string before = LineAt(text, offset, upTo: true);
        if (!before.EndsWith(word + " ", StringComparison.Ordinal)) return false;
        string rest = before[..^(word.Length + 1)].TrimEnd();
        return rest.Length == 0 || rest.EndsWith('{');
    }

    private static string LineAt(string text, int offset, bool upTo = false)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        int s = offset;
        while (s > 0 && text[s - 1] != '\n') s--;
        if (upTo) return text[s..offset];
        int e = offset;
        while (e < text.Length && text[e] != '\n') e++;
        return text[s..e];
    }

    // ---- hover ---------------------------------------------------------------------------------------

    /// What the word at `offset` is: a format as code, a collection as declared, a signature from any
    /// bundle, a field's type, or the declared symbol — the sigil decides which (a shape and a mark may share
    /// a name, RULES 14e). Null when there is nothing to say.
    public static string? Hover(EditorSnapshot s, int offset)
    {
        using var _ = BundleSearch.Scope(s.ProjectDir);
        string text = s.Text;
        var (word, start) = WordAt(text, offset);
        if (word is null || s.Ast is null || s.Members is not { } model) return null;

        // D23: the name in `loom<MeshList>`, or after `from` in a format — the format itself, as code.
        if (FormatHover(s, start, word) is { } format) return format;

        // D24: `.~slots` — the collection as declared, since the `~` alone does not say which kind.
        if (CollectionIndex.At(text, offset) is { } read && model.Collections.Spec(read.Shape, read.Field) is { } spec)
            return $"{CollectionIndex.Describe(read.Field, spec)}\n— on ${read.Shape}";

        // 0) Anything about a signature — a field in an `emit`/`attach` body, a slot in a `bring`, or an
        //    @Event / &Builder / $Shape itself — from ANY bundle, through the compiler's own lookup.
        if (s.Signatures?.DescribeAt(text, offset) is { } sig) return sig;

        // 1) A field name inside an emit / struct / shape / event body → its declared type.
        var (kind, name) = EnclosingBraceHead(model, text, start);
        if (name is not null)
        {
            if (kind == "event" && model.Events.TryGetValue(name, out var ef) && ef.FirstOrDefault(x => x.Name == word) is { Name: not null } f1)
                return $"{name}.{word} : {f1.Type}";
            if (kind == "shape" && model.Shapes.TryGetValue(name, out var sf) && sf.FirstOrDefault(x => x.Name == word) is { Name: not null } f2)
                return $"{name}.{word} : {f2.Type}";
        }

        // 2) The word itself is a declared symbol. THE SIGIL DECIDES, when there is one.
        char sigil = start > 0 ? text[start - 1] : '\0';
        switch (sigil)
        {
            case '$': return model.Shapes.ContainsKey(word) ? Shape(word) : $"shape ${word}";
            case '@': return model.Events.ContainsKey(word) ? Event(word) : $"event @{word}";
            case '#': return Mark(word);
            case '&': return $"builder &{word}";
        }

        // No sigil: a bare name in an expression. Order is a fallback, not a guess about kind.
        if (model.Shapes.ContainsKey(word)) return Shape(word);
        if (model.Events.ContainsKey(word)) return Event(word);
        if (FuncIndex.Find(s.Ast, word, s.ProjectDir) is { Fn: not null } hit) return FuncIndex.Signature(hit.Fn, hit.Owner);
        if (IsMark(s.Ast, word)) return Mark(word);
        // `mute Physics` — the only place a bare shard name is written outside its declaration.
        if (s.Definitions.Definitions.FirstOrDefault(d => d.Name == word && d.Kind is SymbolKind.Shard or SymbolKind.ShardView or SymbolKind.Bridge) is { } sh)
            return $"{ShardKindWord(sh.Kind)} {word}";
        return null;

        string Shape(string n) => $"shape ${n} {{ " + string.Join(", ", model.Shapes[n].Select(f => $"{f.Name}: {f.Type}")) + " }";
        string Event(string n) => $"event @{n} {{ " + string.Join(", ", model.Events[n].Select(f => $"{f.Name}: {f.Type}")) + " }";
        // A mark carries no fields, so the useful extra fact is whether a shape shares its name.
        string Mark(string n) => IsMark(s.Ast!, n)
            ? model.Shapes.ContainsKey(n) ? $"mark #{n}   (a shape ${n} shares this name)" : $"mark #{n}"
            : $"mark #{n}   (not declared in this file)";
    }

    /// `loom<MeshList>` / `format X from MeshList`: the format named, as code — this file's own, a needed
    /// bundle's (the index), or a built-in's one-line description. Null anywhere else.
    private static string? FormatHover(EditorSnapshot s, int start, string word)
    {
        string before = s.Text[..start].TrimEnd();
        int dot = before.Length;
        while (dot > 0 && (char.IsLetterOrDigit(before[dot - 1]) || before[dot - 1] is '.' or '*' or '_')) dot--;   // `*Vein.Weaving.`
        string head = before[..dot].TrimEnd();
        if (!head.EndsWith("loom<", StringComparison.Ordinal) && !head.EndsWith(" from", StringComparison.Ordinal)) return null;
        if (head.EndsWith(" from", StringComparison.Ordinal) && !System.Text.RegularExpressions.Regex.IsMatch(head, @"format\s+\w+\s+from$")) return null;

        if (FormatCatalog.DescribeBuiltIn(word) is { } builtIn) return builtIn;
        IEnumerable<Decl> All(IEnumerable<Decl> ms) => ms.SelectMany(m => m is PublicatorDecl p ? All(p.Members) : new[] { m });
        if (s.Ast!.Bundles.SelectMany(b => All(b.Members)).OfType<FormatDecl>().FirstOrDefault(f => f.Name == word) is { } local)
            return FormatCatalog.Code(local).TrimEnd();
        try
        {
            if (BundleIndex.For(s.ProjectDir).Formats.FirstOrDefault(kv => kv.Value.Name == word) is { Value: { } shared } hit)
                return $"{FormatCatalog.Code(shared).TrimEnd()}\n— from {hit.Key[..hit.Key.LastIndexOf('.')]}";
        }
        catch { }
        return null;
    }

    /// Is `name` declared as a mark anywhere in the unit? Marks have no members, so `MemberIndex` does not
    /// carry them and the AST is the only place to ask.
    private static bool IsMark(CompilationUnit unit, string name)
    {
        static IEnumerable<MarkDecl> Declared(IEnumerable<Decl> members)
        {
            foreach (var m in members)
            {
                if (m is MarkDecl mark) yield return mark;
                else if (m is PublicatorDecl pub) foreach (var nested in Declared(pub.Members)) yield return nested;
            }
        }
        return unit.Bundles.SelectMany(b => Declared(b.Members)).Any(m => m.Name == name);
    }

    public static (string? Word, int Start) WordAt(string text, int offset)
    {
        if (offset < 0 || offset > text.Length) return (null, 0);
        int i = offset;
        if (i >= text.Length || !IsIdent(text[i])) { if (i > 0 && IsIdent(text[i - 1])) i--; else return (null, 0); }
        int st = i; while (st > 0 && IsIdent(text[st - 1])) st--;
        int e = i; while (e < text.Length && IsIdent(text[e])) e++;
        return (text[st..e], st);
    }

    /// The kind/name of the type whose `{ … }` body encloses `pos` (`emit @E {`, `Id {`, `shape $H {`).
    private static (string, string?) EnclosingBraceHead(MemberIndex.Model model, string text, int pos)
    {
        int depth = 0, i = pos - 1;
        for (; i >= 0; i--)
        {
            if (text[i] == '}') depth++;
            else if (text[i] == '{') { if (depth == 0) break; depth--; }
        }
        if (i < 0) return ("", null);
        int j = i - 1; while (j >= 0 && char.IsWhiteSpace(text[j])) j--;
        int end = j + 1;
        while (j >= 0 && (IsIdent(text[j]) || text[j] is '$' or '@' or '#')) j--;
        string tok = text[(j + 1)..end];
        if (tok.Length == 0) return ("", null);
        if (tok[0] == '@') return ("event", tok[1..]);
        if (tok[0] == '$') return ("shape", tok[1..]);
        if (model.Events.ContainsKey(tok)) return ("event", tok);
        if (model.Shapes.ContainsKey(tok)) return ("shape", tok);
        return ("", null);
    }

    private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';

    // ---- signature help -----------------------------------------------------------------------------

    /// The call the caret is inside, and which argument is being typed: an fn / SF call (`clamp(`), or a
    /// `bring Unit(` / `emit @Hit {` — whose parameters are flattened from shapes usually in another file,
    /// so they are the signature most worth seeing. Null for a name nothing resolves: describing a different
    /// function that happens to share it would be worse than saying nothing.
    public static EditorSignature? Signature(EditorSnapshot s, int offset)
    {
        using var _ = BundleSearch.Scope(s.ProjectDir);
        if (SignatureHelp.FunctionAt(s.Text, offset) is { } call)
        {
            var (fn, owner) = call.Path is null
                ? FuncIndex.Find(s.Ast, call.Name, s.ProjectDir)
                : FuncIndex.FindQualified(call.Path, call.Name, s.ProjectDir);
            if (fn is null) return null;
            // Find's owner for a needed bundle's function is its full key, name included.
            if (call.Path is null && owner is not null && owner.EndsWith("." + fn.Name, StringComparison.Ordinal))
                owner = owner[..^(fn.Name.Length + 1)];
            return new EditorSignature(FuncIndex.Signature(fn, owner),
                fn.Params.Select(p => p.Name + ": " + (p.Type?.Name ?? "?")).ToList(), call.ActiveSlot);
        }
        if (SignatureHelp.At(s.Text, offset) is { } bc && s.Signatures?.Find(new SignatureRef(bc.Kind, bc.Path, bc.Name)).Hit is { } sig)
        {
            var ps = sig.Fields.Select(f => f.Name + ": " + f.Type).ToList();
            return sig.Kind switch
            {
                SignatureKind.Builder => new EditorSignature($"bring {sig.Name}({string.Join(", ", ps)})", ps, bc.ActiveSlot),
                SignatureKind.Event => new EditorSignature($"emit @{sig.Name} {{ {string.Join(", ", ps)} }}", ps, bc.ActiveSlot),
                _ => new EditorSignature($"${sig.Name} {{ {string.Join(", ", ps)} }}", ps, bc.ActiveSlot),
            };
        }
        return null;
    }

    // ---- go to definition / references ---------------------------------------------------------------

    /// The symbol at a 1-based line and column, as the definition index records it.
    public static SymbolSite? SymbolAt(EditorSnapshot s, int line, int col) => s.Definitions.At(line, col);

    /// The `need` declared on a line, or null.
    public static NeedDecl? NeedOn(EditorSnapshot s, int line) =>
        s.Ast?.Bundles.SelectMany(b => b.Members).OfType<NeedDecl>().FirstOrDefault(n => n.Span.Line == line);

    /// F12: where the symbol at (line, col) is declared — a `need` line opens its bundle; the local index
    /// wins over a shared declaration of the same name (RULES 18); then the bundle that shares it. The
    /// location's Text is the status line. Null with nothing under the caret.
    public static EditorLocation? Definition(EditorSnapshot s, int line, int col)
    {
        using var _ = BundleSearch.Scope(s.ProjectDir);
        if (NeedOn(s, line) is { } need)
        {
            string? file = null;
            try { BundleIndex.For(s.ProjectDir).Owners.TryGetValue(need.Key, out file); } catch { }
            return file is null
                ? new EditorLocation(null, 0, 0, $"need \"{need.Key}\" — no bundle of that name is on the search path.")
                : new EditorLocation(file, 1, 1, $"need \"{need.Key}\" — {System.IO.Path.GetFileName(file)}");
        }
        if (SymbolAt(s, line, col) is not { } site) return null;

        if (s.Definitions.Define(site.Name, site.Kind) is { } def)
            return new EditorLocation(null, def.Span.Line, def.Span.Col, $"{site.Kind} {site.Name} — declared in {def.Owner}, line {def.Span.Line}");

        if (site.Kind is SymbolKind.Shape or SymbolKind.Mark or SymbolKind.Event or SymbolKind.Builder && s.Ast is not null)
        {
            ScopeIndex.ScopeEntry? found = null;
            try { found = ScopeIndex.For(s.Ast, site.Kind, s.ProjectDir).FirstOrDefault(en => !en.Local && en.Name == site.Name); } catch { }
            if (found is { At: { } at })
                return new EditorLocation(at.File, at.Line, at.Col, $"{site.Kind} {site.Name} — declared in {found.Origin}, {System.IO.Path.GetFileName(at.File)} line {at.Line}");
        }
        return new EditorLocation(null, 0, 0, $"{site.Kind} {site.Name} is not declared in this file, and no bundle here shares it.");
    }

    /// Shift+F12: every site naming the symbol at (line, col), across the project. The bundle being edited
    /// comes from the text (fragments merged); every other file from disk. File null = the text itself.
    public static IReadOnlyList<EditorLocation> References(EditorSnapshot s, int line, int col)
    {
        if (SymbolAt(s, line, col) is not { } site) return Array.Empty<EditorLocation>();
        string name = site.Name; var kind = site.Kind;
        var hits = new List<EditorLocation>();
        var seen = new HashSet<(string, int, int)>();

        void Add(string? file, SymbolSite x)
        {
            if (!seen.Add((file ?? "", x.Span.Line, x.Span.Col))) return;
            string where = file is null ? "" : System.IO.Path.GetFileName(file) + "  ";
            hits.Add(new EditorLocation(file, x.Span.Line, x.Span.Col, $"{where}{x.Span.Line}:{x.Span.Col}  {(x.IsDefinition ? "declared" : "used")} in {x.Owner}"));
        }

        string? currentMain = s.Path is null ? null
            : BundleLoader.IsFragment(s.Path) ? BundleLoader.LocateMainFile(s.Path) ?? s.Path : s.Path;
        var ownSources = currentMain is null ? Array.Empty<string>() : BundleLoader.SourceFiles(currentMain);
        foreach (var x in s.Definitions.All(name, kind))
        {
            string? path = BundleLoader.PathOf(x.Span, ownSources);
            Add(path is not null && string.Equals(path, s.Path, StringComparison.OrdinalIgnoreCase) ? null : path, x);
        }

        // A shard is named only from inside its own bundle (`mute`, D18) — another file's `shard Boot` is
        // a different shard.
        if (s.RootFolder is null || kind is SymbolKind.Shard or SymbolKind.ShardView or SymbolKind.Bridge) return hits;

        foreach (string file in ProjectVeinFiles(s.RootFolder))
        {
            if (currentMain is not null && string.Equals(file, currentMain, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var unit = BundleLoader.Load(file, new DiagnosticBag());
                var sources = BundleLoader.SourceFiles(file);
                foreach (var x in DefinitionIndex.Analyze(unit).All(name, kind))
                    Add(BundleLoader.PathOf(x.Span, sources) ?? file, x);
            }
            catch { /* a malformed file is skipped, not fatal to the search */ }
        }
        return hits;
    }

    /// Every `.vein` in the project that parses on its own: fragments come in through their main file,
    /// and a manifest is not a bundle.
    public static IEnumerable<string> ProjectVeinFiles(string root)
    {
        IEnumerable<string> all;
        try { all = Directory.EnumerateFiles(root, "*.vein", SearchOption.AllDirectories).ToList(); }
        catch { yield break; }
        foreach (string f in all)
        {
            if (BundleLoader.IsFragment(f)) continue;
            if (System.IO.Path.GetFileName(f).EndsWith(".app.vein", StringComparison.OrdinalIgnoreCase)) continue;
            yield return f;
        }
    }

    // ---- outline, hints, colours, fixes --------------------------------------------------------------

    /// The declarations in the text, for an outline.
    public static IReadOnlyList<SymbolSite> Symbols(EditorSnapshot s) => s.Definitions.Definitions.ToList();

    /// The `‹map›` hints: where each `.~name` ends, and its kind (D24).
    public static IReadOnlyList<(int Offset, string Label)> Hints(EditorSnapshot s) =>
        CollectionIndex.Reads(s.Text)
            .Select(r => s.Collections.Spec(r.Shape, r.Field) is { } c ? (r.End, "‹" + CollectionIndex.Tag(c) + "›") : (-1, ""))
            .Where(h => h.Item1 >= 0).ToList();

    /// What a diagnostic's Quick Fix would change.
    public static IReadOnlyList<QuickFix> Fixes(EditorSnapshot s, Diagnostic d) => QuickFixes.For(d, s.Text, s.ProjectDir);
}
