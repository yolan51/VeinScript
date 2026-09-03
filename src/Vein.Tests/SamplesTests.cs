using Vein.Compiler.Diagnostics;
using Vein.Compiler.Project;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// stdlib/ has been guarded by StdlibTests since it was written; samples/ never was — which is how
// LANGUAGE-TOUR.vein came to sit in the repo not parsing, teaching `target` outside a schedule. These
// discover the sample files rather than listing them, so a new sample is covered the moment it lands.
public class SamplesTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with stdlib/ not found");
    }

    private static IEnumerable<string> VeinFiles(string folder) =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), folder), "*.vein", SearchOption.AllDirectories)
                 .OrderBy(p => p, StringComparer.Ordinal);

    /// A fragment under `publicators/` or `shards/` is not a compilation unit — it carries no `bundle`
    /// header, so compiling one on its own is a guaranteed VS0101. It reaches the compiler only through
    /// its bundle's main file, which is what `SourcePath` below asks BundleLoader to do.
    private static bool IsFragment(string path) =>
        Path.GetDirectoryName(path) is { } dir &&
        BundleLoader.FragmentFolders.Contains(Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<object[]> SampleFiles =>
        VeinFiles("samples").Where(p => !IsFragment(p))
                            .Select(p => new object[] { Path.GetRelativePath(RepoRoot(), p).Replace('\\', '/') });

    [Theory]
    [MemberData(nameof(SampleFiles))]
    public void Sample_compiles(string relative)
    {
        string path = Path.Combine(RepoRoot(), relative);
        // SourcePath is what pulls in the bundle's fragments. Without it a multi-file sample compiles as
        // only its main file, and a shard living under shards/ would go unchecked — which is precisely
        // the silent break BundleLoader's own header warns about.
        var result = new VeinCompilerService().Compile(new CompileRequest(
            Path.GetFileName(path), File.ReadAllText(path), SourcePath: path));
        Assert.True(result.Success, $"{relative}:\n  " + string.Join("\n  ", result.Diagnostics.Select(d => d.ToString())));
    }

    /// Every warning every shipped .vein file produces, as one report.
    ///
    /// `Sample_compiles` above asserts `result.Success`, which is ERRORS only — so a warning has never
    /// failed anything here. And `veinc ir` does not show them either: its default renderer walks the
    /// AST and never calls `Lower`, which is where almost every VS02xx is raised. Between the two, a
    /// warning could sit in the tree indefinitely with nothing pointing at it.
    [Fact]
    public void No_shipped_vein_file_produces_a_warning()
    {
        var svc = new VeinCompilerService();
        var found = new List<string>();

        foreach (var path in VeinFiles("samples").Concat(VeinFiles("stdlib")))
        {
            if (IsFragment(path)) continue;
            var result = svc.Compile(new CompileRequest(
                Path.GetFileName(path), File.ReadAllText(path), SourcePath: path));

            foreach (var d in result.Diagnostics.Where(d => d.Severity == Severity.Warning))
                found.Add($"{Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/')}: {d.Code} {d.Message}");
        }

        Assert.True(found.Count == 0, $"{found.Count} warning(s):\n  " + string.Join("\n  ", found));
    }

    /// Every sample's IR tree, checked for nodes the renderer had no case for.
    ///
    /// `tools/check-ir.sh` golden-checks 8 of the 59 samples, and NOTHING renders the other 51 — so a
    /// statement the renderer did not know about printed as a childless stub named after its C# class,
    /// and stayed that way. `ordered by` did exactly that: `AstTree` had no `OrderedStmt` case, so the
    /// whole block rendered as one leaf and every `bring` inside it was missing from the tree.
    ///
    /// A golden per sample would also have caught it, at the price of 51 files that churn on every edit.
    /// This costs nothing to keep and catches the NEXT one automatically, which is the half that matters:
    /// the hole was never that one node was wrong, it was that nothing was looking.
    [Fact]
    public void No_sample_renders_a_node_the_IR_tree_has_no_case_for()
    {
        var svc = new VeinCompilerService();
        var offenders = new List<string>();

        foreach (var path in VeinFiles("samples"))
        {
            if (IsFragment(path)) continue;

            var result = svc.Compile(new CompileRequest(
                Path.GetFileName(path), File.ReadAllText(path), SourcePath: path));
            if (result.Ast is not { } unit) continue;

            string rel = Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/');
            var roots = new Vein.Compiler.Ir.AstTree(unit).Roots(unit);
            string tree = Vein.Compiler.Ir.IrTreeRenderer.Render(rel, roots, new Vein.Compiler.Ir.IrTreeOptions());

            offenders.AddRange(tree.Split('\n')
                .Where(l => l.Contains("Unrendered", StringComparison.Ordinal))
                .Select(l => $"{rel}: {l.Trim()}"));
        }

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} node(s) fell through to the renderer's fallback:\n  " +
            string.Join("\n  ", offenders.Distinct()));
    }

    /// How much of the HIR carries a type, across every sample.
    ///
    /// IR-SPEC.md's first invariant is *"Fully typed. Every IrExpr has a resolved IrTypeRef."* It was
    /// aspirational: `ResolvedType` existed and nothing assigned it, so consumers re-derived types and
    /// disagreed — the C# backend printed "True" where the interpreter printed "true", and formatted
    /// doubles in the machine's culture.
    ///
    /// A RATIO rather than a demand that every node be typed, because some genuinely cannot be: a
    /// `fromJson` result and a collection binding are dynamic by nature. The number is the point — it
    /// makes the invariant a measurement instead of a claim, and a change that drops it will say so.
    [Fact]
    public void Most_of_the_HIR_carries_a_resolved_type()
    {
        var svc = new VeinCompilerService();
        int typed = 0, total = 0;
        var untyped = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var path in VeinFiles("samples"))
        {
            if (IsFragment(path)) continue;
            var r = svc.Compile(new CompileRequest(
                Path.GetFileName(path), File.ReadAllText(path), SourcePath: path));

            foreach (var m in r.Modules)
                foreach (var e in AllExprs(m))
                {
                    total++;
                    if (e.ResolvedType is not null) { typed++; continue; }
                    // Tallied by node kind AND file, because a bare percentage says nothing about what
                    // to fix next, and "which sample" is usually the faster of the two questions.
                    string k = e is Vein.Compiler.Ir.IrFieldAccess fa
                        ? "field ." + fa.Field + " on " + (fa.Receiver.ResolvedType?.Name ?? "untyped")
                        : e.GetType().Name;
                    untyped[k] = untyped.GetValueOrDefault(k) + 1;
                }
        }

        string worst = string.Join(", ", untyped.OrderByDescending(kv => kv.Value)
                                                .Take(8).Select(kv => kv.Key + "=" + kv.Value));

        Assert.True(total > 1000, $"expected a meaningful sample of expressions, saw {total}");

        double pct = 100.0 * typed / total;
        Assert.True(pct >= 99.0,   // 99.7% today. What remains is genuinely dynamic: a `target` over a
                                  // fromJson result has no element type, and inventing one would be worse.
        
        
        
            $"only {typed}/{total} ({pct:F1}%) of HIR expressions carry a type — Semantics/Resolve " +
            "has regressed, and every consumer is back to guessing. Untyped: " + worst);
    }

    /// Every expression in a module, including the ones nested in statements.
    private static IEnumerable<Vein.Compiler.Ir.IrExpr> AllExprs(Vein.Compiler.Ir.IrModule m)
    {
        foreach (var f in m.Functions)
            foreach (var e in InBlock(f.Body)) yield return e;
        foreach (var s in m.Shards)
            foreach (var fn in s.Methods)
                foreach (var e in InBlock(fn.Body)) yield return e;
    }

    private static IEnumerable<Vein.Compiler.Ir.IrExpr> InBlock(Vein.Compiler.Ir.IrBlock b)
    {
        foreach (var s in b.Statements)
            foreach (var e in InStmt(s)) yield return e;
    }

    private static IEnumerable<Vein.Compiler.Ir.IrExpr> InStmt(Vein.Compiler.Ir.IrStmt? s)
    {
        switch (s)
        {
            case null: yield break;
            case Vein.Compiler.Ir.IrBlock b:
                foreach (var e in InBlock(b)) yield return e;
                break;
            case Vein.Compiler.Ir.IrLet l:
                foreach (var e in InExpr(l.Init)) yield return e;
                break;
            case Vein.Compiler.Ir.IrAssign a:
                foreach (var e in InExpr(a.Target)) yield return e;
                foreach (var e in InExpr(a.Value)) yield return e;
                break;
            case Vein.Compiler.Ir.IrIf i:
                foreach (var e in InExpr(i.Cond)) yield return e;
                foreach (var e in InBlock(i.Then)) yield return e;
                if (i.Else is not null) foreach (var e in InBlock(i.Else)) yield return e;
                break;
            case Vein.Compiler.Ir.IrExprStmt x:
                foreach (var e in InExpr(x.Expr)) yield return e;
                break;
            case Vein.Compiler.Ir.IrReturn r:
                foreach (var e in InExpr(r.Value)) yield return e;
                break;
            case Vein.Compiler.Ir.IrOrdered o:
                foreach (var e in InBlock(o.Collect)) yield return e;
                break;
            case Vein.Compiler.Ir.IrOrderedBring ob:
                foreach (var e in InExpr(ob.Key)) yield return e;
                foreach (var e in InBlock(ob.Body)) yield return e;
                break;
            case Vein.Compiler.Ir.IrMatch m:
                foreach (var e in InExpr(m.Subject)) yield return e;
                foreach (var arm in m.Arms) foreach (var e in InBlock(arm.Body)) yield return e;
                if (m.Else is not null) foreach (var e in InBlock(m.Else)) yield return e;
                break;
            case Vein.Compiler.Ir.IrLoop lp:
                foreach (var e in InExpr(lp.Cond)) yield return e;
                foreach (var e in InExpr(lp.Count)) yield return e;
                foreach (var e in InExpr(lp.Source)) yield return e;
                foreach (var e in InBlock(lp.Body)) yield return e;
                break;
        }
    }

    private static IEnumerable<Vein.Compiler.Ir.IrExpr> InExpr(Vein.Compiler.Ir.IrExpr? e)
    {
        if (e is null) yield break;
        yield return e;

        switch (e)
        {
            case Vein.Compiler.Ir.IrBinary b:
                foreach (var x in InExpr(b.Left)) yield return x;
                foreach (var x in InExpr(b.Right)) yield return x;
                break;
            case Vein.Compiler.Ir.IrUnary u:
                foreach (var x in InExpr(u.Operand)) yield return x;
                break;
            case Vein.Compiler.Ir.IrFieldAccess fa:
                foreach (var x in InExpr(fa.Receiver)) yield return x;
                break;
            case Vein.Compiler.Ir.IrIndex ix:
                foreach (var x in InExpr(ix.Receiver)) yield return x;
                foreach (var x in InExpr(ix.Index)) yield return x;
                break;
            case Vein.Compiler.Ir.IrCall c:
                foreach (var a in c.Args) foreach (var x in InExpr(a)) yield return x;
                break;
            case Vein.Compiler.Ir.IrRuntimeCall rc:
                foreach (var a in rc.Args) foreach (var x in InExpr(a)) yield return x;
                break;
            case Vein.Compiler.Ir.IrStructInit si:
                foreach (var (_, v) in si.Fields) foreach (var x in InExpr(v)) yield return x;
                break;
            case Vein.Compiler.Ir.IrList li:
                foreach (var i in li.Items) foreach (var x in InExpr(i)) yield return x;
                break;
        }
    }

    [Fact]
    public void Multi_file_samples_bring_in_their_fragments()
    {
        // Guards the line above: web_app keeps /docs in shards/Reference.vein, so a compile that missed
        // fragments would still pass Sample_compiles while serving a 404 for the whole reference.
        string main = Path.Combine(RepoRoot(), "samples", "web_app", "web_app.vein");
        var result = new VeinCompilerService().Compile(new CompileRequest(
            Path.GetFileName(main), File.ReadAllText(main), SourcePath: main));

        Assert.True(result.Success);
        Assert.Contains(result.Ast!.Bundles[0].Members.OfType<Vein.Compiler.Parsing.ShardDecl>(),
                        s => s.Name == "Reference");
    }

    [Fact]
    public void No_vein_source_uses_the_removed_scope_sigil()
    {
        // `::` was removed from the language: `::Shape.field` is now `<target-binding>.Shape.field`, and
        // cross-bundle access is the `*Author.Bundle.Publicator.@member` star path.
        var offenders = VeinFiles("samples").Concat(VeinFiles("stdlib"))
            .SelectMany(p => File.ReadAllLines(p).Select((line, i) => (Path: p, No: i + 1, Line: line)))
            .Where(x => x.Line.Contains("::", StringComparison.Ordinal))
            .Select(x => $"{Path.GetFileName(x.Path)}:{x.No}: {x.Line.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0, "`::` is no longer valid VeinScript:\n  " + string.Join("\n  ", offenders));
    }
}
