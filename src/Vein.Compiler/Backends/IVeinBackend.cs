using Vein.Compiler.Ir;

namespace Vein.Compiler.Backends;

// The extensibility seam that keeps VeinIR backend-independent (see docs/BACKEND-CONTRACT.md).
// A backend consumes a lowered IrModule and produces artifacts. No backend is implemented yet — the
// only runtime today is the reactive interpreter (Ir/Interp.cs); future C#/JS/WASM/native backends
// slot in here without touching the front end or the IR.

public sealed record EmittedFile(string RelativePath, string Contents);
public sealed record BackendResult(bool Success, IReadOnlyList<EmittedFile> Files, IReadOnlyList<string> Notes);

public interface IVeinBackend
{
    string Name { get; }             // e.g. "csharp", "js", "interp"
    string OutputExtension { get; }  // e.g. ".cs"

    BackendResult Emit(IrModule module);
}
