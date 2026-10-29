using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Project;
using Vein.Compiler.Tooling;

namespace Vein.Hosting;

/// The program as the Workbench's understanding tabs and `veinc` show it — the event flow, the events and
/// their payloads, the symbols across bundles, the IR tree, the formats, what runs when, and what a bundle
/// takes from others — for an engine's editor (Godot's VeinScript tab). The same models and the same
/// renderers as `veinc flow/events/symbols/ir/formats/exec`, so the views and the CLI agree; each runs with
/// the stdlib and the library packages in reach (see VeinEditorSession).
///
/// `text` is the editor's unsaved text of `path`, when there is one: the views follow what is typed.
public static class VeinViews
{
    /// Who emits what and who hears it, across every bundle the program links (`veinc flow`).
    public static EventFlow Flow(string path) => InReach(() => EventFlow.For(path));

    /// Every event the program can emit or hear, with its payload (`veinc events`).
    public static string Events(string path, string? text = null) => WithUnit(path, text, (unit, dir) =>
        EventCatalog.Render(EventCatalog.Catalog(unit, dir)));

    /// Every qualified name the program and the bundles it loads declare, and their collisions (`veinc symbols`).
    public static string Symbols(string path) => InReach(() =>
    {
        var diag = new DiagnosticBag();
        string rendered = ProjectLoader.Load(path, diag).Render();
        return Problems(diag) + rendered;
    });

    /// The program as a tree — bundles, shards, blocks, statements (`veinc ir`).
    public static string Ir(string path, string? text = null) => WithUnit(path, text, (unit, _) =>
        IrTreeRenderer.Render(Path.GetFileName(path), new AstTree(unit).Roots(unit), new IrTreeOptions(Unicode: true)));

    /// Every format the program can name, with an example and the looms that use it (`veinc formats`).
    public static string Formats(string path) => InReach(() => FormatCatalog.For(path).Render());

    /// When each block runs, what it touches, what may run at once (`veinc exec`).
    public static string Execution(string path, string? text = null) => WithUnit(path, text, (unit, _) =>
        ExecutionModel.Analyze(unit) is { } model ? ExecutionReport.Render(model) : "no bundle in this file\n");

    /// What the file's bundle takes from other bundles, by author and bundle (the Workbench's Dependencies).
    public static DependencyModel? Dependencies(string path, string? text = null) => InReach(() =>
    {
        var diag = new DiagnosticBag();
        var unit = BundleLoader.Load(path, diag, (path, text ?? File.ReadAllText(path)));
        return DependencyModel.Analyze(unit, StdlibIndex.Symbols(Path.GetDirectoryName(path)));
    });

    /// Any Vein.Compiler tool, run with the stdlib and the library packages in reach — for a view not here.
    public static T InReach<T>(Func<T> view)
    {
        using var _ = BundleSearch.WithFallback(StdlibCache.Root);
        using var __ = BundleSearch.WithLibraries(VeinLibraries.Roots);
        return view();
    }

    /// A view of the file's bundle as loaded with its fragments; when it does not load, why not.
    private static string WithUnit(string path, string? text, Func<Vein.Compiler.Parsing.CompilationUnit, string?, string> render) => InReach(() =>
    {
        var diag = new DiagnosticBag();
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        using var _ = BundleSearch.Scope(dir);
        var unit = BundleLoader.Load(path, diag, (path, text ?? File.ReadAllText(path)));
        return diag.HasErrors ? Problems(diag) : render(unit, dir);
    });

    private static string Problems(DiagnosticBag diag) =>
        diag.HasErrors ? string.Join("\n", diag.Items.Where(d => d.Severity == Severity.Error)) + "\n\n" : "";
}
