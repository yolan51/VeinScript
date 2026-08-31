using Vein.Compiler.Parsing;

namespace Vein.Compiler.Tooling;

// A quick structural summary of a bundle — the declaration counts shown in the Workbench's bundle
// inspector. All counts are derived from the parsed AST. Marks are the odd one out: a mark may be
// declared (`mark #Enemy`) or merely used, and the count is the distinct marks either way, via
// SymbolIndex — an inspector wants the marks the bundle deals in, not just the ones it announced.
public sealed record BundleInfo(
    string Name, string? Author,
    int Shapes, int Events, int Builders, int Shards, int Views, int Marks)
{
    public static BundleInfo Analyze(BundleDecl bundle)
    {
        int shapes = 0, events = 0, builders = 0, shards = 0, views = 0;

        void Walk(IEnumerable<Decl> members)
        {
            foreach (var m in members)
                switch (m)
                {
                    case ShapeDecl: shapes++; break;
                    case EventDecl: events++; break;
                    case BuilderDecl: builders++; break;
                    case ShardDecl: shards++; break;
                    case ViewDecl: views++; break;
                    case PublicatorDecl p: Walk(p.Members); break;   // publicators hold shared shapes/events/builders
                }
        }
        Walk(bundle.Members);

        // Distinct marks this bundle deals in — declared, used, or both. SymbolIndex dedupes across the two.
        int marks = SymbolIndex.Collect(new CompilationUnit(new[] { bundle }, bundle.Span)).Marks.Count;

        return new BundleInfo(bundle.Name, bundle.Author, shapes, events, builders, shards, views, marks);
    }

    /// Analyze the first bundle in a parsed unit (a bundle file normally declares exactly one).
    public static BundleInfo? Analyze(CompilationUnit unit) =>
        unit.Bundles.Count > 0 ? Analyze(unit.Bundles[0]) : null;

    /// A compact one-line summary, e.g. "3 shards · 5 shapes · 4 events · 2 builders · 1 view · 2 marks".
    public string Summary =>
        $"{Shards} shards · {Shapes} shapes · {Events} events · {Builders} builders · {Views} views · {Marks} marks";
}
