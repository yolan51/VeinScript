using Vein.Compiler.Parsing;

namespace Vein.Compiler.Project;

// The cross-bundle symbol index, as seen by tooling that only cares about "what can I reference".
//
// This is now a thin shim over BundleIndex, which searches an ORDERED LIST of roots — the standard
// library first, then this project's installed `bundles/`. The name is kept because the stdlib is still
// the first root and every existing call site reads naturally; see BundleIndex for the why.
public static class StdlibIndex
{
    /// Shared symbols visible from `startDir` (stdlib + installed bundles). Empty if nothing is found.
    public static IReadOnlyList<QualifiedSymbol> Symbols(string? startDir = null) =>
        BundleIndex.For(startDir).Symbols;

    /// Shared builders, keyed `Author.Bundle.Publicator.Name` — resolves `bring *A.B.Pub.&Builder(…)`.
    public static IReadOnlyDictionary<string, BuilderDecl> Builders(string? startDir = null) =>
        BundleIndex.For(startDir).Builders;

    /// Shared shapes — resolves a qualified `$Shape` include in an event/builder signature.
    public static IReadOnlyDictionary<string, ShapeDecl> Shapes(string? startDir = null) =>
        BundleIndex.For(startDir).Shapes;

    /// Shared `fn`/`SF` declarations — resolves a qualified call `*A.B.Pub.name(…)`.
    public static IReadOnlyDictionary<string, FuncDecl> Functions(string? startDir = null) =>
        BundleIndex.For(startDir).Functions;

    /// The `stdlib/` folder itself, or null. Retained for callers that want the standard library
    /// specifically rather than the whole search path.
    public static string? Locate(string? startDir) => BundleIndex.LocateNamed(startDir, "stdlib");
}
