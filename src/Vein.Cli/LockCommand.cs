using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Project;

// vein.lock from the command line: the commands that RUN a program record what it uses from `bundles/`,
// and `veinc lock` is the one way to accept a signature that changed. See Project/SignatureLock.cs.
internal static class LockCommand
{
    /// Called by `run`, `render`, `serve` and `build` after a clean compile. Adds lines for members not yet
    /// recorded and never touches one that changed — that difference is the diagnostic.
    ///
    /// A lock that cannot be written (a read-only checkout, say) is reported and nothing else: failing to
    /// keep a record is no reason to refuse to run the program the record is about.
    public static void Record(string? projectDir, IEnumerable<LockEntry> uses)
    {
        try
        {
            var result = SignatureLock.Baseline(projectDir, uses);
            if (result.Added.Count > 0)
                Console.Error.WriteLine(
                    $"vein.lock: recorded {result.Added.Count} signature(s) this program uses from bundles/ — {result.Path}. Commit it.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"vein.lock: not updated — {ex.Message}");
        }
    }

    /// `veinc lock <app-or-file>` — make vein.lock say what the program uses NOW, printing each difference.
    ///
    /// Refuses on any error but VS0344, which is the one this command exists to accept: a program that
    /// does not compile for another reason is not one to take a record of.
    public static int Run(string path, string source, string? projectDir)
    {
        var diagnostics = new DiagnosticBag();
        var uses = new List<LockEntry>();

        // Linked directly rather than through the CLI's LinkApp, which prints every diagnostic as it
        // goes — here the VS0344s are the input, and printing them as errors before accepting them would
        // read as a failure.
        var linked = AppLinker.Link(path, source, diagnostics);
        if (linked is not null) uses.AddRange(linked.OutsideUses);
        else
        {
            var unit = BundleLoader.Load(path, diagnostics, editing: (path, source));
            if (!diagnostics.HasErrors)
                foreach (var bundle in unit.Bundles)
                {
                    var lower = new Lower(diagnostics, projectDir);
                    lower.LowerBundle(bundle);
                    uses.AddRange(lower.OutsideUses);
                }
        }

        var blocking = diagnostics.Items.Where(d => d.Severity == Severity.Error && d.Code != "VS0344").ToList();
        if (blocking.Count > 0)
        {
            foreach (var d in blocking) Console.Error.WriteLine(d);
            Console.Error.WriteLine("vein.lock: not written — fix the errors above first.");
            return 1;
        }

        // An app manifest is the whole program, so an unused line can go; a single file may be one
        // bundle of several, so its unused lines stay.
        SignatureLock.Relock(projectDir, uses, pruneUnused: linked is not null, Console.Out);
        return 0;
    }
}
