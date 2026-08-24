using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: veinc <tokens|ast|ir|render|graph> <file.vein> [path]");
    return 2;
}

string command = args[0];
string path = args[1];

if (!File.Exists(path))
{
    Console.Error.WriteLine($"file not found: {path}");
    return 2;
}

string source = File.ReadAllText(path);
var diagnostics = new DiagnosticBag();

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
        var tokens = new Lexer(source, Path.GetFileName(path), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).ParseUnit();
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
        var tokens = new Lexer(source, Path.GetFileName(path), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).ParseUnit();
        if (!diagnostics.HasErrors)
        {
            if (legacy)
            {
                var lower = new Lower(diagnostics);
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
        var tokens = new Lexer(source, Path.GetFileName(path), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).ParseUnit();
        if (!diagnostics.HasErrors)
        {
            string requestPath = args.Length > 2 ? args[2] : "/";
            var lower = new Lower(diagnostics);
            foreach (var bundle in unit.Bundles)
            {
                var result = new Interp().Render(lower.LowerBundle(bundle), requestPath);
                foreach (var line in result.Log) Console.Error.WriteLine($"  · {line}");
                if (result.Body is not null)
                    Console.WriteLine($"HTTP {result.Status}\n{result.Body}");
                else
                    Console.Error.WriteLine($"(no @Response emitted for {requestPath})");
            }
        }
        break;
    }

    case "graph":
    {
        var tokens = new Lexer(source, Path.GetFileName(path), diagnostics).Tokenize();
        var unit = new Parser(tokens, diagnostics).ParseUnit();
        if (!diagnostics.HasErrors)
        {
            string requestPath = args.Length > 2 ? args[2] : "/";
            var lower = new Lower(diagnostics);
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

    default:
        Console.Error.WriteLine($"unknown command: {command}");
        return 2;
}

foreach (var d in diagnostics.Items) Console.Error.WriteLine(d);
return diagnostics.HasErrors ? 1 : 0;

static string Display(Token t) =>
    t.Kind == TokenKind.Term ? "" :
    t.Value is not null ? $"{t.Text}  = {t.Value}" : t.Text;