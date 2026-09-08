using System.Text.Json;
using Vein.Compiler.Backends;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;
using Vein.Compiler.Project;
using Vein.Compiler.Tooling;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: veinc <new|check|tokens|ast|ir|render|serve|run|build|emit|graph|events|scaffold|symbols|exec> <file.vein> [arg]");
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
int reportedDiagnostics = 0;   // link diagnostics already printed; the final sweep skips them.

// An `app` manifest declares `load`s, not bundles, so `unit.Bundles` is empty for one. Rather than
// iterating that empty list (which printed nothing and exited 0 — "it ran and produced no output", the
// most misleading answer the CLI can give), link the app into one module and run THAT.
static AppLinker.LinkedApp? LinkApp(string file, string src, DiagnosticBag diag, out int reported)
{
    reported = 0;
    var linked = AppLinker.Link(file, src, diag);
    if (linked is null) return null;

    // Print link diagnostics NOW, not at exit. They are known before a single event fires, and
    // `veinc run` may never return — a console app blocks on input, so "printed at the end" means
    // "never printed" for exactly the composition mistakes this feature exists to surface.
    foreach (var d in diag.Items) Console.Error.WriteLine(d);
    reported = diag.Items.Count;

    // Loaded bundles by name; needed ones the same, so the report says what the manifest never had to.
    string needed = linked.Needed.Count == 0 ? ""
                  : $"; {linked.Needed.Count} of them by `need` [{string.Join(", ", linked.Needed)}]";
    Console.Error.WriteLine(
        $"app {linked.AppName}: principal '{linked.Principal}' boots; " +
        $"{linked.Bundles.Count} bundle(s) linked into one runtime [{string.Join(", ", linked.Bundles)}]{needed}");
    return linked;
}

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

    // `veinc check` — type-check a file and print every diagnostic, nothing else.
    //
    // It is the name every other toolchain uses and the first thing anyone tries, and it was missing:
    // `ir` was how you checked a file from the CLI. It also does something `ir` does NOT — `ir`'s
    // default renderer walks the AST and never calls Lower, which is where nearly every VS02xx is
    // raised, so `veinc ir` on a program with an unknown builder printed a tree and no complaint.
    // This runs Lower over every bundle so the same diagnostics an editor shows are what you get here.
    case "check":
    {
        var unit = BundleLoader.Load(path, diagnostics, editing: (path, source));
        if (!diagnostics.HasErrors)
        {
            // One Lower per bundle — its state is per-bundle, and a shared one leaked each bundle's
            // `use` list into the next (see Lower.LowerBundle).
            foreach (var bundle in unit.Bundles) new Lower(diagnostics, projectDir).LowerBundle(bundle);
        }

        foreach (var d in diagnostics.Items) Console.Error.WriteLine(d);
        int errors = diagnostics.Items.Count(d => d.Severity == Severity.Error);
        int warnings = diagnostics.Items.Count(d => d.Severity == Severity.Warning);
        Console.Error.WriteLine(errors == 0 && warnings == 0
            ? "ok"
            : $"{errors} error(s), {warnings} warning(s)");
        return errors == 0 ? 0 : 1;
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
                foreach (var bundle in unit.Bundles)
                    Console.Write(IrPrinter.Print(new Lower(diagnostics, projectDir).LowerBundle(bundle)));
            }
            else
            {
                var roots = new AstTree(unit, opts.FullStrings).Roots(unit);
                Console.Write(IrTreeRenderer.Render(path.Replace('\\', '/'), roots, opts));

                // The tree above is rendered from the AST, so nothing here had run the semantic pass —
                // and almost every VS02xx is raised by Lower. `veinc ir` therefore reported a clean file
                // that `veinc graph` called out, which is a worse failure than not checking at all: it
                // looks like an answer. Lower for the DIAGNOSTICS and throw the module away; the tree
                // printed is still the AST one, unchanged.
                foreach (var bundle in unit.Bundles) new Lower(diagnostics, projectDir).LowerBundle(bundle);
            }
        }
        break;
    }

    case "render":
    {
        var linkedApp = LinkApp(path, source, diagnostics, out reportedDiagnostics);
        var unit = linkedApp is null ? BundleLoader.Load(path, diagnostics, editing: (path, source)) : null;
        if (!diagnostics.HasErrors)
        {
            // Positional arg = request path (legacy @Request); `--set k=v` overrides boot payload fields;
            // `--ticks N` advances N frames of the ECS clock after boot.
            string requestPath = "/";
            int ticks = 0;
            var inputs = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (int i = 2; i < args.Length; i++)
            {
                var a = args[i];
                string? pair = a == "--set" && i + 1 < args.Length ? args[++i]
                             : a.StartsWith("--set=") ? a["--set=".Length..] : null;
                if (pair is not null) { var kv = pair.Split('=', 2); if (kv.Length == 2) inputs[kv[0]] = Coerce(kv[1]); }
                else if (Ticks(args, ref i) is { } t) ticks = t;
                else if (!a.StartsWith("--")) requestPath = a;
            }
            // An app is ONE module, so it renders once; a plain file still renders each bundle it holds.
            // A load-site `start { … }` override is laid down first, so an explicit --set still wins.
            var modules = new List<IrModule>();
            if (linkedApp is { } la)
            {
                foreach (var kv in la.BootOverrides) inputs.TryAdd(kv.Key, kv.Value);
                modules.Add(la.Module);
            }
            else
            {
                foreach (var bundle in unit!.Bundles)
                    modules.Add(new Lower(diagnostics, projectDir).LowerBundle(bundle));
            }

            foreach (var module in modules)
            {
                var result = new Interp { Ticks = ticks, BaseDirectory = projectDir }.Render(module, requestPath, inputs);
                foreach (var line in result.Log) Console.Error.WriteLine($"  · {line}");
                if (result.Body is not null)
                    Console.WriteLine($"HTTP {result.Status}\n{result.Body}");
                else
                    Console.Error.WriteLine($"(no @Response emitted for {requestPath})");
            }
        }
        break;
    }

    case "serve":
    {
        // The same @Request → @Response pipeline `render` drives, with a real socket in front of it.
        int port = 8080;
        string host = "localhost";
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out var p)) { port = p; i++; }
            else if (args[i].StartsWith("--port=") && int.TryParse(args[i]["--port=".Length..], out var p2)) port = p2;
            else if (args[i] == "--host" && i + 1 < args.Length) host = args[++i];
            else if (args[i].StartsWith("--host=")) host = args[i]["--host=".Length..];
        }
        return ServeCommand.Serve(path, source, port, host);
    }

    case "run":
    {
        // Console mode: boot the program, then pump stdin↔stdout via @Input/@Print. Ctrl+Z (Windows) /
        // Ctrl+D (Unix) ends input.
        var linkedRun = LinkApp(path, source, diagnostics, out reportedDiagnostics);
        var unit = linkedRun is null ? BundleLoader.Load(path, diagnostics, editing: (path, source)) : null;
        if (!diagnostics.HasErrors)
        {
            int ticks = 0;
            for (int i = 2; i < args.Length; i++) if (Ticks(args, ref i) is { } t) ticks = t;

            if (linkedRun is { } la)
            {
                // ONE interpreter for the whole app — one handler table and one event queue, which is
                // exactly what lets a `hear` in a capability bundle see an `emit` from the principal.
                // A relative `@WriteFile` path means the PROGRAM's folder, not the shell's — run the same
                // program from two directories and its saves used to land in two places, neither beside
                // the `.vein` that asked for them.
                new Interp { Ticks = ticks, BaseDirectory = projectDir }
                    .Run(la.Module, Console.In, Console.Out, messaging: true);
            }
            else
            {
                foreach (var bundle in unit!.Bundles)
                    new Interp { Ticks = ticks, BaseDirectory = projectDir }.Run(new Lower(diagnostics, projectDir).LowerBundle(bundle),
                                                     Console.In, Console.Out, messaging: true);
            }
        }
        break;
    }

    case "emit":
    {
        // HIR → C# source (docs/BACKEND-CONTRACT.md §2). Separate from `build`, which publishes an exe
        // around the interpreter: these are two different products and conflating them under one verb
        // would make "did I get compiled code?" impossible to answer from the command line.
        string? outDir = null;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "-o" && i + 1 < args.Length) outDir = args[++i];
            else if (args[i].StartsWith("-o=")) outDir = args[i]["-o=".Length..];
        }
        // AN APP MANIFEST IS EMITTED AS THE ONE MODULE IT LINKS TO, exactly as `run` runs it. This used
        // to fall through `BundleLoader.Load`, which finds no `bundle` in a manifest and returns zero of
        // them — so the loop below ran zero times, `veinc emit KitDemo.app.vein` exited 0, wrote nothing
        // and said nothing. A kit-built game IS an app, so no kit-built game could be compiled at all,
        // and the interpreter's ceiling was the only ceiling such a game had.
        var linkedEmit = LinkApp(path, source, diagnostics, out reportedDiagnostics);
        var unit = linkedEmit is null ? BundleLoader.Load(path, diagnostics, editing: (path, source)) : null;
        if (diagnostics.HasErrors) break;

        var backend = new CSharpBackend();
        var toEmit = linkedEmit is { } la
            ? new[] { la.Module }
            : unit!.Bundles.Select(b => new Lower(diagnostics, projectDir).LowerBundle(b)).ToArray();

        foreach (var module in toEmit)
        {
            var result = backend.Emit(module);
            foreach (var note in result.Notes) Console.Error.WriteLine($"  note: {note}");
            foreach (var file in result.Files)
            {
                if (outDir is null) { Console.Write(file.Contents); continue; }
                Directory.CreateDirectory(outDir);
                string target = Path.Combine(outDir, file.RelativePath);
                File.WriteAllText(target, file.Contents);
                Console.Error.WriteLine($"wrote {target}");
            }
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
            foreach (var bundle in unit.Bundles)
            {
                var result = new Interp().Render(new Lower(diagnostics, projectDir).LowerBundle(bundle), requestPath);
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
            var events = EventCatalog.Catalog(unit, projectDir);
            if (args.Contains("--json"))
                Console.WriteLine(JsonSerializer.Serialize(events, new JsonSerializerOptions { WriteIndented = true }));
            else
                Console.Write(EventCatalog.Render(events));
        }
        break;
    }

    case "scaffold":
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: veinc scaffold <file.vein> <@Event | &Builder>"); return 2; }
        // The sigil picks which catalog to look in; a bare name tries the event first, then the builder,
        // so `veinc scaffold f.vein Unit` finds an identity template without needing to know the spelling.
        string raw = args[2];
        string want = raw.TrimStart('@', '&');
        var unit = BundleLoader.Load(path, diagnostics, editing: (path, source));
        if (!diagnostics.HasErrors)
        {
            var events = raw.StartsWith('&') ? new List<EventEntry>() : EventCatalog.Catalog(unit, projectDir);
            var builders = raw.StartsWith('@') ? new List<BuilderEntry>() : EventCatalog.Builders(unit, projectDir);

            var ev = events.FirstOrDefault(e => string.Equals(e.Name, want, StringComparison.Ordinal));
            var bl = builders.FirstOrDefault(b => string.Equals(b.Name, want, StringComparison.Ordinal));

            if (ev is not null) Console.Write(EventCatalog.Scaffold(ev));
            else if (bl is not null) Console.Write(EventCatalog.Scaffold(bl));
            else
            {
                var available = events.Select(e => "@" + e.Name).Concat(builders.Select(b => "&" + b.Name));
                Console.Error.WriteLine($"no event or builder '{want}'. available: {string.Join(", ", available)}");
                return 2;
            }
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

foreach (var d in diagnostics.Items.Skip(reportedDiagnostics)) Console.Error.WriteLine(d);
return diagnostics.HasErrors ? 1 : 0;

static string Display(Token t) =>
    t.Kind == TokenKind.Term ? "" :
    t.Value is not null ? $"{t.Text}  = {t.Value}" : t.Text;

// `--ticks N` / `--ticks=N`: frames of the ECS clock to advance after boot. Null when args[i] is not it.
static int? Ticks(string[] args, ref int i)
{
    string? n = args[i] == "--ticks" && i + 1 < args.Length ? args[++i]
              : args[i].StartsWith("--ticks=") ? args[i]["--ticks=".Length..] : null;
    return n is not null && int.TryParse(n, out var v) ? v : null;
}

// Coerce a `--set k=v` value to int/bool where it parses, else keep the string.
static object? Coerce(string v) =>
    long.TryParse(v, out var l) ? l : bool.TryParse(v, out var b) ? b : v;
