using System.Diagnostics;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;

namespace Vein.Compiler.Service;

// The single, canonical compilation entry point the Workbench (and anything else) consumes. It
// reuses the existing pipeline internals only — Lexer → Parser → AstTree/IrTreeRenderer (VeinIR) →
// Lower (IrModule). It does NOT create a second IR. The CLI and the IDE both route through here.

/// `ProjectDir` anchors cross-bundle resolution — the standard library plus this project's installed
/// `bundles/`. Omit it and resolution falls back to the CWD, which finds the stdlib but not a user project.
public sealed record CompileRequest(string FileName, string Source, bool Unicode = false, bool FullStrings = false, string? ProjectDir = null);

public sealed record CompilationResult(
    bool Success,
    IReadOnlyList<Diagnostic> Diagnostics,
    CompilationUnit? Ast,
    IReadOnlyList<IrNode> IrTree,   // the VeinIR display tree (what `veinc ir` shows)
    string IrText,                  // the rendered VeinIR tree
    IReadOnlyList<IrModule> Modules,// the lowered HIR, one per bundle
    long ElapsedMs);

public sealed class VeinCompilerService
{
    public CompilationResult Compile(CompileRequest request)
    {
        var sw = Stopwatch.StartNew();
        var diag = new DiagnosticBag();

        var tokens = new Lexer(request.Source, request.FileName, diag).Tokenize();
        var unit = new Parser(tokens, diag).ParseUnit();

        IReadOnlyList<IrNode> tree = Array.Empty<IrNode>();
        string irText = "";
        var modules = new List<IrModule>();

        // Only build IR when the front end is clean — matches the CLI's `!HasErrors` gate.
        if (!diag.HasErrors)
        {
            var opts = new IrTreeOptions(Unicode: request.Unicode, FullStrings: request.FullStrings);
            tree = new AstTree(unit, request.FullStrings).Roots(unit);
            irText = IrTreeRenderer.Render(request.FileName, tree, opts);

            var lower = new Lower(diag, request.ProjectDir);
            foreach (var bundle in unit.Bundles)
                modules.Add(lower.LowerBundle(bundle));
        }

        sw.Stop();
        return new CompilationResult(!diag.HasErrors, diag.Items, unit, tree, irText, modules, sw.ElapsedMilliseconds);
    }
}
