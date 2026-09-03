using Vein.Compiler.Service;
using Vein.Compiler.Project;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// Go-to-definition and find-references need positions, and SymbolIndex only ever had names. What makes
// this resolvable without a type checker is the SIGIL: `$Row` and `#Row` are different identities that
// are allowed to share a name (RULES 14e), and every use site says which one it means.
public class DefinitionIndexTests
{
    private static DefinitionIndex Index(string body) =>
        DefinitionIndex.Analyze(new VeinCompilerService()
            .Compile(new CompileRequest("t.vein", "bundle B by you {\n" + body + "\n}")).Ast!);

    [Fact]
    public void A_shape_is_found_where_it_is_declared()
    {
        var ix = Index("    shape $Worker { addr: string }");

        var def = ix.Define("Worker", SymbolKind.Shape);
        Assert.NotNull(def);
        Assert.Equal(2, def!.Span.Line);   // line 1 is the bundle header
        Assert.True(def.IsDefinition);
    }

    [Fact]
    public void A_shape_and_a_mark_sharing_a_name_stay_separate()
    {
        // The reason the index is keyed by (name, kind) and not by name. RULES 14e allows `$Row` and
        // `#Row` to coexist; a lookup by name alone would jump to whichever was written first, and be
        // wrong half the time in exactly the files that use the pattern.
        var ix = Index("    shape $Row { n: int }\n    mark #Row");

        var shape = ix.Define("Row", SymbolKind.Shape);
        var mark = ix.Define("Row", SymbolKind.Mark);

        Assert.NotNull(shape);
        Assert.NotNull(mark);
        Assert.NotEqual(shape!.Span.Line, mark!.Span.Line);
    }

    [Fact]
    public void A_target_over_a_shape_counts_as_a_use()
    {
        // `target $Worker as w` is the shape's real use site — what a reader follows when asking who
        // touches it. The shape arrives as the source EXPRESSION, not as a name on the statement.
        var ix = Index(
            "    shape $Worker { addr: string }\n" +
            "    shard S {\n        run once {\n            target $Worker as w { }\n        }\n    }");

        var all = ix.All("Worker", SymbolKind.Shape);
        Assert.Equal(2, all.Count);
        Assert.True(all[0].IsDefinition);
        Assert.False(all[1].IsDefinition);
        Assert.Equal("S", all[1].Owner);
    }

    [Fact]
    public void An_emit_and_a_hear_are_both_uses_of_the_event()
    {
        // The pair that matters for the chat workload: "who emits this, who hears it" is the question,
        // and both halves have to be sites or the answer is half an answer.
        var ix = Index(
            "    event @Ping { }\n" +
            "    shard A { run once { emit @Ping { } } }\n" +
            "    shard B { hear @Ping as p { } }");

        var all = ix.All("Ping", SymbolKind.Event);

        Assert.Equal(3, all.Count);
        Assert.Single(all, s => s.IsDefinition);
        Assert.Equal(new[] { "A", "B" }, all.Where(s => !s.IsDefinition).Select(s => s.Owner));
    }

    [Fact]
    public void A_bring_is_a_use_of_the_builder()
    {
        var ix = Index(
            "    shape $P { a: int }\n" +
            "    builder Panel { include $P }\n" +
            "    shard S { run once { bring Panel(1) } }");

        var all = ix.All("Panel", SymbolKind.Builder);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, s => !s.IsDefinition && s.Owner == "S");
    }

    [Fact]
    public void A_publicator_owns_what_it_declares()
    {
        // "Declared in Http" is the useful answer for a stdlib-shaped bundle; "declared in Vein.Web"
        // would be true and useless.
        var ix = Index("    publicator Http {\n        event @Request { path: string }\n    }");

        var def = ix.Define("Request", SymbolKind.Event);
        Assert.NotNull(def);
        Assert.Equal("Http", def!.Owner);
    }

    [Fact]
    public void A_symbol_declared_elsewhere_resolves_to_nothing_rather_than_to_a_guess()
    {
        // `*Vein.Console.Io.@Print` lives in the stdlib. Jumping somewhere plausible-but-wrong is worse
        // than not jumping, so Define returns null and the caller can say so.
        var ix = Index("    shard S { run once { emit *Vein.Console.Io.@Print { text: \"hi\" } } }");

        Assert.Null(ix.Define("Print", SymbolKind.Event));
    }

    [Fact]
    public void The_caret_finds_the_symbol_it_is_sitting_on()
    {
        var ix = Index("    shape $Worker { addr: string }");
        var def = ix.Define("Worker", SymbolKind.Shape)!;

        Assert.NotNull(ix.At(def.Span.Line, def.Span.Col));
        Assert.Equal("Worker", ix.At(def.Span.Line, def.Span.Col)!.Name);
    }

    [Fact]
    public void The_control_centre_sample_indexes_its_own_shape()
    {
        // The real file, so the walk is pinned against shipped code rather than only against strings
        // these tests wrote.
        string path = Path.Combine(RepoRoot(), "samples", "control_center.vein");
        var ix = DefinitionIndex.Analyze(new VeinCompilerService()
            .Compile(new CompileRequest("control_center.vein", File.ReadAllText(path), SourcePath: path)).Ast!);

        Assert.NotNull(ix.Define("Worker", SymbolKind.Shape));

        // $Worker is targeted three times across Relay and Reachability, plus attached once.
        var uses = ix.All("Worker", SymbolKind.Shape).Where(s => !s.IsDefinition).ToList();
        Assert.True(uses.Count >= 3, $"expected at least 3 uses of $Worker, found {uses.Count}");
        Assert.Contains(uses, u => u.Owner == "Relay");
        Assert.Contains(uses, u => u.Owner == "Reachability");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with stdlib/ not found");
    }
}
