using System.Text.Json;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;
using Vein.Compiler.Project;
using Vein.Compiler.Tooling;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: veinc <new|tokens|ast|ir|render|run|build|graph|events|scaffold|symbols|exec> <file.vein> [arg]");
    return 2;
}

// `new` scaffolds a project — its second arg is `bundle|app`, not a file, so handle it before the
// file-reading path below.
if (args[0] == "new") return NewCommand.Run(args);

string command = args[0];
string path = args[1];

if (!File.Exists(path))
{
    Console.Error.WriteLine($"file not found: {path}");
    return 2;
}

string source = File.ReadAllText(path);
var diagnostics = new DiagnosticBag();

// Anchor cross-bundle resolution at the file being compiled, so `<app>/bundles/` is found alongside
// the standard library rather than only whatever sits above the current working directory.
string? projectDir = Path.GetDirectoryName(Path.GetFullPath(path));

switch (command)
{
    case "tokens":
    {
        var tokens = new Lexer(source, Path.GetFileName(path), diagnostics).Tokenize();
        foreach (var t in tokens)
            Console.WriteLine($"{t.Span,-24} {t.Kind,-14} {Display(t)}");
        break;
    }

    case "ast":
    {
        var unit = BundleLoader.Load(path, diagnostics, editing: (path, source));
        Console.WriteLine(AstPrinter.Print(unit));
        break;
    }

    case "ir":
    {
        bool legacy = args.Contains("--ir=legacy");
        var opts = new IrTreeOptions(
            Unicode: args.Contains("--ir-unicode"),
            Spans: args.Contains("--ir-spans"),
            FullStrings: args.Contains("--ir-full-strings"));
        var unit = BundleLoader.Load(path, diagnostics, editing: (path, source));
        if (!diagnostics.HasErrors)
        {
            if (legacy)
            {
                var lower = new Lower(diagnostics, projectDir);
                foreach (var bundle in unit.Bundles)
                    Console.Write(IrPrinter.Print(lower.LowerBundle(bundle)));
            }
            else
            {
                var roots = new AstTree(unit, opts.FullStrings).Roots(unit);
                Console.Write(IrTreeRenderer.Render(path.Replace('\\', '/'), roots, opts));
            }
        }
        break;
    }

    case "render":
    {
        var unit = BundleLoader.Load(path, diagnostics, editing: (path, source));
        if (!diagnostics.HasErrors)
        {
            // Positional arg = request path (legacy @Request); `--set k=v` overrides boot payload fields.
            string requestPath = "/";
            var inputs = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int i = 2; i < args.Length; i++)
            {
                var a = args[i];
                string? pair = a == "--set" && i + 1 < args.Length ? args[++i]
                             : a.StartsWith("--set=") ? a["--set=".Length..] : null;
                if (pair is not null) { var kv = pair.Split('=', 2); if (kv.Length == 2) inputs[kv[0]] = Coerce(kv[1]); }
                else if (!a.StartsWith("--")) requestPath = a;
            }
            var lower = new Lower(diagnostics, projectDir);
            foreach (var bundle in unit.Bundles)
            {
                var result = new Interp().Render(lower.LowerBundle(bundle), requestPath, inputs);
                foreach (var line in result.Log) Console.Error.WriteLine($"  · {line}");
                if (result.Body is not null)
                    Console.WriteLine($"HTTP {result.Status}\n{result.Body}");
                else
                    Console.Error.WriteLine($"(no @Response emitted for {requestPath})");
            }
        }
        break;
    }

    case "run":
    {
        // Console mode: boot the program, then pump stdin↔stdout via @Input/@Print. Ctrl+Z (Windows) /
        // Ctrl+D (Unix) ends input.
        var unit = BundleLoader.Load(path, diagnostics, editing: (path, source));
        if (!diagnostics.HasErrors)
        {
            var lower = new Lower(diagnostics, projectDir);
            foreach (var bundle in unit.Bundles)
                new Interp().Run(lower.LowerBundle(bundle), Console.In, Console.Out, messaging: true);
        }
        break;
    }

    case "build":
    {
        // Compile the program to a standalone console .exe (embeds the interpreter + source, then
        // `dotnet publish` self-contained). Returns the build's own exit code directly.
        string? outPath = null, rid = null;
        bool selfContained = true;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "-o" && i + 1 < args.Length) outPath = args[++i];
            else if (args[i] == "--rid" && i + 1 < args.Length) rid = args[++i];
            else if (args[i] == "--framework-dependent") selfContained = false;
        }
        return BuildCommand.Build(path, source, outPath, rid, selfContained);
    }

    case "graph":
    {
        var unit = BundleLoader.Load(path, diagnostics, editing: (path, source));
        if (!diagnostics.HasErrors)
        {
            string requestPath = args.Length > 2 ? args[2] : "/";
            var lower = new Lower(diagnostics, projectDir);
            foreach (var bundle in unit.Bundles)
            {
                var result = new Interp().Render(lower.LowerBundle(bundle), requestPath);
                Console.WriteLine($"Vein First-Class graph — bundle {bundle.Name}");
                Console.WriteLine("  nodes:");
                foreach (var n in result.Nodes)
                    Console.WriteLine($"    {n.Kind,-10} {n.Name,-14} {n.Identity}");
                Console.WriteLine("  emit edges:");
                foreach (var e in result.Edges)
                    Console.WriteLine($"    {e.FromKind}.{e.FromName}  --@{e.Event}-->");
            }
        }
        break;
    }

    case "events":
    {
        var unit = BundleLoader.Load(path, diagnostics, editing: (path, source));
        if (!diagnostics.HasErrors)
        {
            var events = EventCatalog.Catalog(unit);
            if (args.Contains("--json"))
                Console.WriteLine(JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true }));
            else
                Console.Write(EventCatalog.Render(events));
        }
        break;
    }

    case "scaffold":
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: veinc scaffold <file.vein> <EventName>"); return 2; }
        string want = args[2].TrimStart('@');
        var unit = BundleLoader.Load(path, diagnostics, editing: (path, source));
        if (!diagnostics.HasErrors)
        {
            var events = EventCatalog.Catalog(unit);
            var match = events.FirstOrDefault(e => string.Equals(e.Name, want, StringComparison.Ordinal));
            if (match is null)
            {
                Console.Error.WriteLine($"no event '{want}'. available: {string.Join(", ", events.Select(e => "@" + e.Name))}");
                return 2;
            }
            Console.Write(EventCatalog.Scaffold(match));
        }
        break;
    }

    case "symbols":
    {
        // Cross-bundle discovery: load the app + every bundle it loads, list qualified names, flag
        // collisions. `path` is the app file.
        var model = ProjectLoader.Load(path, diagnostics);
        if (args.Contains("--json"))
        {
            var payload = new
            {
                app = model.AppName,
                collisions = model.Collisions,
                starts = model.Starts.Select(b => new
                {
                    author = b.Author, bundle = b.Bundle, @event = b.Event,
                    fields = b.Fields.Select(f => new { name = f.Name, type = f.Type, required = f.Required })
                }),
                symbols = model.Symbols.Select(s => new
                {
                    qualified = s.QualifiedName,
                    author = s.Author, bundle = s.Bundle, publicator = s.Publicator,
                    kind = s.Kind.ToString(), name = s.Name
                })
            };
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        else Console.Write(model.Render());
        break;
    }

    case "exec":
    {
        // The derived execution model: when each trigger block runs, what identity state it touches, and
        // which blocks may run concurrently. Static analysis only — nothing is executed.
        var unit = BundleLoader.Load(path, diagnostics, editing: (path, source));
        if (!diagnostics.HasErrors)
        {
            var model = ExecutionModel.Analyze(unit);
            if (model is null) Console.Error.WriteLine("no bundle in file");
            else if (args.Contains("--json"))
                Console.WriteLine(JsonSerializer.Serialize(ExecutionReport.Json(model),
                    new JsonSerializerOptions { WriteIndented = true }));
            else
                Console.Write(ExecutionReport.Render(model, ascii: args.Contains("--ascii")));
        }
        break;
    }

    default:
        Console.Error.WriteLine($"unknown command: {command}");
        return 2;
}

foreach (var d in diagnostics.Items) Console.Error.WriteLine(d);
return diagnostics.HasErrors ? 1 : 0;

static string Display(Token t) =>
    t.Kind == TokenKind.Term ? "" :
    t.Value is not null ? $"{t.Text}  = {t.Value}" : t.Text;

// Coerce a `--set k=v` value to int/bool where it parses, else keep the string.
static object? Coerce(string v) =>
    long.TryParse(v, out var l) ? l : bool.TryParse(v, out var b) ? b : v;