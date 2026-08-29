using Vein.Compiler.Lexing;

namespace Vein.Compiler.Diagnostics;

public enum Severity { Error, Warning, Info }

public sealed record Diagnostic(Severity Severity, string Code, string Message, SourceSpan Span)
{
    public override string ToString() =>
        $"{Span}: {Severity.ToString().ToLowerInvariant()} {Code}: {Message}";
}

public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = new();

    public IReadOnlyList<Diagnostic> Items => _items;
    public bool HasErrors => _items.Any(d => d.Severity == Severity.Error);

    public void Error(string code, string message, SourceSpan span) =>
        _items.Add(new Diagnostic(Severity.Error, code, message, span));

    public void Warning(string code, string message, SourceSpan span) =>
        _items.Add(new Diagnostic(Severity.Warning, code, message, span));

    /// Move diagnostics collected in a throwaway bag into this one. Used where a file has to be parsed
    /// SPECULATIVELY — AppLinker probes for an `app` header, and a plain bundle must not pay for that
    /// probe with a second copy of every parse error when the caller parses it again.
    public void AddRange(IEnumerable<Diagnostic> items) => _items.AddRange(items);
}