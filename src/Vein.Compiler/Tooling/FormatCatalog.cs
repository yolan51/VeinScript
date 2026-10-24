using System.Globalization;
using System.Text;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;
using Vein.Compiler.Project;

namespace Vein.Compiler.Tooling;

/// <summary>A format a program can name: where it comes from, its code, an example of what it reads, and the
/// looms that use it. <see cref="Editable"/> is true for this program's own `formats/<Name>.vein`.</summary>
public sealed record CatalogFormat(
    string Name, string Group, string? File, int Line, string? Base, string Label, string Example, string Code,
    WeaveFormat? Flat, IReadOnlyList<string> UsedBy, bool Editable);

/// <summary>A loom of the program, with its parameters in binding order.</summary>
public sealed record CatalogLoom(string Name, string Format, IReadOnlyList<WeaveParam> Params, WeaveFormat? Flat, string File, int Line);

/// <summary>One record of a dry run: the bring it becomes, or the problem `@Unwoven` would report.</summary>
public sealed record BenchLine(string Record, string? Bring, string? Problem);

// WHY: a format is a small language of its own — which character ends a record, which cuts the values —
// and the only way to know what one does is to watch it read. The Workbench's Formats tab and `veinc
// formats` answer from here: every format a program can name, and a DRY RUN of any of them against pasted
// text, through the same `Weave.ReadAll` the interpreter weaves with. A record is shown as the `bring` it
// becomes — the language's own promise (D23) — so what the editor shows is what the program will do.
public sealed class FormatCatalog
{
    public const string BuiltIn = "Built-in";
    public const string ThisProgram = "This program";
    public const string Official = "Vein.Weaving";

    public required string Program { get; init; }
    public required IReadOnlyList<CatalogFormat> Formats { get; init; }
    public required IReadOnlyList<CatalogLoom> Looms { get; init; }

    /// Where a new format of this program is saved: `formats/` beside its main file.
    public required string FormatsFolder { get; init; }

    /// Errors that stopped the program compiling; the catalog shows what did.
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();

    public CatalogFormat? Find(string name) =>
        Formats.FirstOrDefault(f => f.Group == ThisProgram && f.Name == name) ?? Formats.FirstOrDefault(f => f.Name == name);

    // ---- building it -----------------------------------------------------------------------------------

    private static readonly (string Name, string Label, string Example)[] BuiltIns =
    {
        ("Json", "each object of a top-level array, or the one object; fields by key", "[{\"name\": \"Goblin\", \"hp\": 30}]"),
        ("Csv", "a header line naming the columns, then a record per line; \"…\" keeps a comma", "name,hp  ↵  Goblin,30"),
        ("Ini", "each [section] is a record named by its title; key = value lines; ; and # comments", "[Goblin]  ↵  hp = 30"),
        ("KeyValue", "values in parameter order, or name=value; a record per line or per ;", "Goblin 30; Orc hp=50 x=9"),
    };

    /// What a built-in format reads, in one line with an example — for a hover on `loom<Csv>`. Null for
    /// any other name.
    public static string? DescribeBuiltIn(string name) =>
        BuiltIns.FirstOrDefault(b => b.Name == name) is { Name: not null } b
            ? $"{b.Name} — built in: {b.Label}\ne.g.  {b.Example}" : null;

    /// The catalog of the program at `programPath` — linked as `veinc run` would link it.
    public static FormatCatalog For(string programPath)
    {
        string path = Path.GetFullPath(programPath);
        string source = File.ReadAllText(path);
        var diag = new DiagnosticBag();

        var parts = (AppLinker.Link(path, source, diag) ?? AppLinker.LinkNeeds(path, source, diag))?.Parts.ToList();
        if (parts is null)
        {
            parts = new List<AppLinker.LinkedPart>();
            var unit = BundleLoader.Load(path, diag);
            foreach (var bundle in unit.Bundles)
            {
                var lower = new Lower(diag, Path.GetDirectoryName(path));
                var module = lower.LowerBundle(bundle);
                parts.Add(new AppLinker.LinkedPart(bundle, path, module, lower.FlowSites, false) { Looms = lower.Looms, Formats = lower.Formats });
            }
        }

        string main = BundleLoader.LocateMainFile(path) ?? path;
        var index = BundleIndex.For(Path.GetDirectoryName(path));
        var mine = parts.Where(p => !p.Needed).ToList();
        var ownBundles = parts.Select(p => (p.Decl.Author ?? "local") + "." + p.Decl.Name).ToHashSet(StringComparer.Ordinal);

        // Looms: the program's, then every loom a bundle in scope shares (for "used by").
        var looms = new List<CatalogLoom>();
        foreach (var part in mine)
        {
            var files = BundleLoader.SourceFiles(part.File);
            foreach (var l in part.Looms)
                looms.Add(new CatalogLoom(l.Name, l.Format, l.Params, l.Flat,
                    BundleLoader.PathOf(l.Span, files) ?? part.File, l.Span.Line));
        }
        var sharedLooms = index.Builders.Where(kv => kv.Value.Loom is not null)
                                        .Select(kv => (Key: kv.Key, Loom: kv.Value)).ToList();

        IReadOnlyList<string> UsedBy(string name, string group) =>
            looms.Where(l => l.Format == name && (group == ThisProgram || group == BuiltIn || !mine.Any(m => m.Formats.Any(f => f.Decl.Name == name))))
                 .Select(l => l.Name)
                 .Concat(sharedLooms.Where(s => s.Loom.Loom == name && !ownBundles.Any(b => s.Key.StartsWith(b + ".", StringComparison.Ordinal)))
                                    .Select(s => s.Key))
                 .Distinct().ToList();

        var formats = new List<CatalogFormat>();
        foreach (var (name, label, example) in BuiltIns)
            formats.Add(new CatalogFormat(name, BuiltIn, null, 0, null, label, example,
                $"// {name} is built in — nothing to declare. To change it, extend it:\n//     format My{name} from {name} {{ lists \",\" }}",
                new WeaveFormat(name, name, new Dictionary<string, string>(StringComparer.Ordinal)), UsedBy(name, BuiltIn), false));

        // The program's own: resolved by Lower exactly as weaving will.
        var mineByName = new Dictionary<string, WeaveFormat?>(StringComparer.Ordinal);
        foreach (var part in mine)
        {
            var files = BundleLoader.SourceFiles(part.File);
            foreach (var f in part.Formats)
            {
                string? file = BundleLoader.PathOf(f.Decl.Span, files);
                bool own = file is not null && string.Equals(Path.GetFileName(Path.GetDirectoryName(file)), BundleLoader.FormatsFolder, StringComparison.OrdinalIgnoreCase);
                var (lbl, ex) = Comments(file, f.Decl);
                formats.Add(new CatalogFormat(f.Decl.Name, ThisProgram, file, f.Decl.Span.Line, f.Decl.Base,
                    f.Decl.Doc ?? lbl, ex, Code(f.Decl), f.Flat, UsedBy(f.Decl.Name, ThisProgram), own));
                mineByName[f.Decl.Name] = f.Flat;
            }
        }

        // Every bundle in scope that shares formats — the standard library's, an installed kit's.
        foreach (var (key, decl) in index.Formats.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var parts2 = key.Split('.');
            string bundle = parts2.Length >= 2 ? parts2[0] + "." + parts2[1] : key;
            if (ownBundles.Contains(bundle)) continue;
            var at = index.Symbols.FirstOrDefault(s => s.Kind == SymbolKind.Format && s.Name == decl.Name &&
                                                       s.QualifiedName.TrimStart('*').StartsWith(bundle + ".", StringComparison.Ordinal))?.At;
            var (lbl, ex) = Comments(at?.File, decl);
            var flat = Flatten(decl, index, bundle, mineByName);
            formats.Add(new CatalogFormat(decl.Name, bundle, at?.File, at?.Line ?? decl.Span.Line, decl.Base,
                decl.Doc ?? lbl, ex, Code(decl), flat, UsedBy(decl.Name, bundle), false));
        }

        return new FormatCatalog
        {
            Program = path,
            Formats = formats,
            Looms = looms,
            FormatsFolder = Path.Combine(Path.GetDirectoryName(main) ?? ".", BundleLoader.FormatsFolder),
            Problems = diag.Items.Where(d => d.Severity == Severity.Error).Select(d => d.ToString()).ToList(),
        };
    }

    /// An indexed format flattened for the dry run: its base found in the same bundle, then the program,
    /// then the built-ins, then anywhere in the index.
    private static WeaveFormat? Flatten(FormatDecl decl, BundleIndex index, string bundle, IReadOnlyDictionary<string, WeaveFormat?> mine)
    {
        var chain = new List<FormatDecl>();
        var seen = new HashSet<FormatDecl>(ReferenceEqualityComparer.Instance);
        string root = "";
        WeaveFormat? programBase = null;
        for (var cur = decl; cur is not null;)
        {
            if (!seen.Add(cur)) return null;
            chain.Add(cur);
            if (cur.Base is null) break;
            string baseName = cur.Base;
            var next = index.Formats.FirstOrDefault(kv => kv.Key.StartsWith(bundle + ".", StringComparison.Ordinal) && kv.Key.EndsWith("." + baseName, StringComparison.Ordinal)).Value;
            if (next is null && mine.TryGetValue(baseName, out var p) && p is not null) { programBase = p; break; }
            if (next is null && Weave.BuiltIns.Contains(baseName)) { root = baseName; break; }
            next ??= index.Formats.FirstOrDefault(kv => kv.Key.EndsWith("." + baseName, StringComparison.Ordinal)).Value;
            if (next is null) return null;
            cur = next;
        }
        var lines = new Dictionary<string, string>(programBase?.Lines ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        var columns = programBase?.Columns;
        foreach (var f in Enumerable.Reverse(chain))
            foreach (var l in f.Lines)
                if (l.Word == "columns") columns = l.Columns;
                else if (l.Text is not null) lines[l.Word] = l.Text;
        return new WeaveFormat(decl.Name, programBase?.Root ?? root, lines, columns);
    }

    /// The label and the example: the `// Example:` line and the first other comment line, from the
    /// comments right above the format (or at the top of its file).
    private static (string Label, string Example) Comments(string? file, FormatDecl decl)
    {
        if (file is null || !File.Exists(file)) return ("", "");
        var lines = File.ReadAllLines(file);
        var above = new List<string>();
        for (int i = decl.Span.Line - 2; i >= 0 && lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal); i--)
            above.Insert(0, lines[i].TrimStart()[2..].Trim());
        string example = above.FirstOrDefault(l => l.StartsWith("Example:", StringComparison.Ordinal))?["Example:".Length..].Trim() ?? "";
        // Every other comment line, joined: a label that wraps in the file should not be cut in the list.
        string label = string.Join(" ", above.Where(l => l.Length > 0 && !l.StartsWith("Example:", StringComparison.Ordinal)));
        return (label, example);
    }

    // ---- code ----------------------------------------------------------------------------------------

    /// A format as code: the declaration, one line per word.
    public static string Code(FormatDecl f) =>
        Code(f.Name, f.Base, f.Lines.Select(l => (l.Word, l.Text, l.Columns)));

    public static string Code(string name, string? bass, IEnumerable<(string Word, string? Text, IReadOnlyList<(string Name, bool Optional)>? Columns)> lines)
    {
        var sb = new StringBuilder($"format {name}{(bass is null ? "" : " from " + bass)} {{\n");
        foreach (var (word, text, columns) in lines)
            sb.Append("    ").Append(word).Append(' ')
              .Append(columns is null ? Quote(text ?? "") : string.Join(", ", columns.Select(c => c.Optional ? $"[{c.Name}]" : c.Name)))
              .Append('\n');
        return sb.Append("}\n").ToString();
    }

    private static string Quote(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\t", "\\t").Replace("\n", "\\n") + "\"";

    /// What `veinc new format` and the Formats tab's New write: every line, commented, with what it does.
    public static string Template(string name) =>
        $"// Example:  S1=9811:S2+S4; S6=box.lan:9816:S5\n" +
        $"// {name}: say what one record of this text looks like. Every line is optional — keep the ones you need.\n" +
        $"format {name} {{\n" +
        "    // records \";\"      // a record also ends at this (it always ends at a line end)\n" +
        "    // comments \"#\"     // a record starting with this is skipped\n" +
        "    // name \"=\"         // the record's name (its first parameter) is the text before this\n" +
        "    // values \":\"       // the rest is cut at this, exactly — empties kept; without it, at spaces\n" +
        "    // pairs \":\"        // a value written key:value fills the parameter it names\n" +
        "    // lists \"+\"        // a list<…> parameter's text is cut at this (\"+\" when unsaid)\n" +
        "    // quotes \"\\\"\"     // a quoted value keeps the separators inside it\n" +
        "    // columns a, [b], c // names the values in order, when they are not the parameters' order; [b] may be missing\n" +
        "}\n";

    // ---- the dry run -------------------------------------------------------------------------------

    /// Each record of `samples` read through `format` into `loom`'s parameters — as the `bring` it becomes,
    /// or the problem `@Unwoven` would report. Names resolve across the pasted text, forward ones included.
    public static List<BenchLine> Bench(CatalogLoom loom, WeaveFormat format, string samples)
    {
        var records = Weave.ReadAll(samples, format, loom.Params);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var duplicate = new HashSet<WovenRecord>();
        foreach (var r in records.Where(r => r.Problem is null && r.Name is { Length: > 0 }))
            if (!names.Add(r.Name!)) duplicate.Add(r);

        var lines = new List<BenchLine>();
        foreach (var r in records)
        {
            if (r.Problem is not null) { lines.Add(new BenchLine(r.Text, null, $"\"{r.Text}\": {r.Problem}")); continue; }
            if (duplicate.Contains(r)) { lines.Add(new BenchLine(r.Text, null, $"\"{r.Text}\": a record named \"{r.Name}\" was already woven")); continue; }

            var problems = new List<string>();
            var args = new List<string>();
            foreach (var p in loom.Params)
            {
                if (r.Missing.Contains(p.Name)) { args.Add("base"); continue; }
                object? v = r.Values[p.Name];
                if (p.Type == "Entity")
                {
                    string n = Weave.Str(v);
                    if (!names.Contains(n)) problems.Add($"`{p.Name}`: no record named \"{n}\"");
                    args.Add(n);
                }
                else if (p.IsList && v is List<object?> items)
                {
                    if (p.Element == "Entity")
                        foreach (var n in items.Select(Weave.Str).Where(n => !names.Contains(n)))
                            problems.Add($"`{p.Name}`: no record named \"{n}\"");
                    args.Add("[" + string.Join(", ", items.Select(i => p.Element == "Entity" ? Weave.Str(i) : Literal(i))) + "]");
                }
                else args.Add(Literal(v));
            }
            lines.Add(new BenchLine(r.Text, $"bring {loom.Name}({string.Join(", ", args)})",
                problems.Count == 0 ? null : string.Join("; ", problems.Select(x => $"\"{r.Text}\": {x}"))));
        }
        return lines;
    }

    private static string Literal(object? v) => v switch
    {
        string s => Quote(s),
        double d => d.ToString("0.0##############", CultureInfo.InvariantCulture),
        _ => Weave.Str(v),
    };

    /// A format read from the Format editor's text and flattened against this catalog — what the bench
    /// reads with while you type. Null with the reason when the text is not one format.
    public WeaveFormat? Compile(string source, out string? error)
    {
        error = null;
        var diag = new DiagnosticBag();
        List<Decl> decls;
        try { decls = new Parser(new Lexer(source, "format.vein", diag).Tokenize(), diag).ParseFragment(exported: true); }
        catch (Exception ex) { error = ex.Message; return null; }
        if (diag.Items.FirstOrDefault(d => d.Severity == Severity.Error) is { } bad) { error = bad.Message; return null; }
        if (decls.OfType<FormatDecl>().FirstOrDefault() is not { } f) { error = "the text holds no `format Name { … }`"; return null; }
        if (f.Lines.FirstOrDefault(l => !Weave.Words.Contains(l.Word)) is { } unknown)
        {
            error = $"`{unknown.Word}` is not a format word — a format says {string.Join(", ", Weave.Words)}";
            return null;
        }

        var lines = new Dictionary<string, string>(StringComparer.Ordinal);
        IReadOnlyList<(string Name, bool Optional)>? columns = null;
        string root = "";
        if (f.Base is not null)
        {
            if (Find(f.Base)?.Flat is not { } b) { error = $"no format '{f.Base}' to start from"; return null; }
            root = b.Root;
            foreach (var (k, v) in b.Lines) lines[k] = v;
            columns = b.Columns;
        }
        foreach (var l in f.Lines)
            if (l.Word == "columns") columns = l.Columns;
            else if (l.Text is not null) lines[l.Word] = l.Text;
        return new WeaveFormat(f.Name, root, lines, columns);
    }

    // ---- inference -------------------------------------------------------------------------------------

    /// <summary>The format that reads every sample, proposed from the samples and the loom's parameters.</summary>
    /// <remarks>
    /// The built-ins are tried first, and when one reads everything the answer is that one — a new format
    /// would only restate it. Otherwise a grid of custom candidates is tried, each through the SAME
    /// reader the program weaves with (typed parameters, the arrangement search, names resolved across
    /// the samples), and the one reading the most records without a problem wins, fewest lines first.
    /// So `S6=box.lan:9816` is only ever explained by a format whose values are cut at `:`.
    /// </remarks>
    public static (string? Code, string? BuiltIn, string Why) Infer(CatalogLoom loom, string samples, string name)
    {
        if (samples.Trim().Length == 0) return (null, null, "paste a few records to infer from");

        // How many records a format reads cleanly. Pasted samples often hold a typo or a name from
        // elsewhere, so the best custom format is the one reading the MOST cleanly — not one that must
        // read every line, which a single bad line would make impossible.
        int Clean(WeaveFormat f, out int total)
        {
            var bench = Bench(loom, f, samples);
            total = bench.Count;
            return bench.Count(b => b.Problem is null);
        }

        // A built-in is proposed only when it reads EVERYTHING: "no new format needed" must be true.
        foreach (var b in new[] { "Json", "Ini", "Csv", "KeyValue" })
            if (Clean(new WeaveFormat(b, b, new Dictionary<string, string>(StringComparer.Ordinal)), out int n) == n && n > 0)
                return (null, b, $"these read as {b} ({n} record(s)) — `loom<{b}> {loom.Name}` needs no new format");

        bool hasList = loom.Params.Any(p => p.IsList);
        string?[] recordsOpts = { null, ";", "|" };
        string?[] nameOpts = { null, "=", ":" };
        string?[] valuesOpts = { null, ":", ",", "|", ";", "\t" };
        string?[] listsOpts = hasList ? new string?[] { "+", ",", "|", ";" } : new string?[] { null };

        (Dictionary<string, string> Lines, int Count, int Total)? best = null;
        foreach (var rec in recordsOpts)
        foreach (var nm in nameOpts)
        foreach (var val in valuesOpts)
        foreach (var lst in listsOpts)
        {
            var used = new[] { rec, nm, val, lst }.Where(x => x is not null).ToList();
            if (used.Count != used.Distinct().Count()) continue;

            var lines = new Dictionary<string, string>(StringComparer.Ordinal);
            if (rec is not null) lines["records"] = rec;
            if (nm is not null) lines["name"] = nm;
            if (val is not null) lines["values"] = val;
            if (lst is not null) lines["lists"] = lst;
            int count = Clean(new WeaveFormat(name, "", lines), out int total);
            if (count == 0) continue;
            if (best is null || count > best.Value.Count || (count == best.Value.Count && lines.Count < best.Value.Lines.Count))
                best = (lines, count, total);
        }

        if (best is null)
            return (null, null, $"no simple format reads any sample into {loom.Name} — write one by hand (New), and the bench will show where it stops");

        var order = new[] { "records", "name", "values", "lists" };
        string code = $"// Example:  {Weave.Records(samples, best.Value.Lines.GetValueOrDefault("records")).FirstOrDefault() ?? ""}\n" +
                      Code(name, null, order.Where(best.Value.Lines.ContainsKey).Select(w => (w, (string?)best.Value.Lines[w], (IReadOnlyList<(string, bool)>?)null)));
        return (code, null, best.Value.Count == best.Value.Total
            ? $"reads all {best.Value.Count} record(s) into {loom.Name}"
            : $"reads {best.Value.Count} of {best.Value.Total} record(s) into {loom.Name} — the bench shows what the others need");
    }

    // ---- veinc formats -------------------------------------------------------------------------------

    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Formats — {Path.GetFileName(Program)}");
        foreach (var p in Problems) sb.AppendLine("  ! " + p);
        foreach (var group in Formats.GroupBy(f => f.Group))
        {
            sb.AppendLine();
            sb.AppendLine(group.Key);
            foreach (var f in group)
            {
                sb.AppendLine($"  {f.Name}{(f.Base is null ? "" : " from " + f.Base)}{(f.Label.Length > 0 ? "  — " + f.Label : "")}");
                if (f.Example.Length > 0) sb.AppendLine($"      e.g.  {f.Example}");
                if (f.UsedBy.Count > 0) sb.AppendLine($"      used by  {string.Join(", ", f.UsedBy)}");
            }
        }
        if (Looms.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Looms");
            foreach (var l in Looms)
                sb.AppendLine($"  loom<{l.Format}> {l.Name}({string.Join(", ", l.Params.Select(p => p.Name + ": " + p.Type + (p.Element is null ? "" : "<" + p.Element + ">") + (p.HasDefault ? " = …" : "")))})");
        }
        sb.AppendLine();
        sb.AppendLine($"A new format of this program goes in {FormatsFolder}  (veinc new format <Name>)");
        return sb.ToString();
    }
}
