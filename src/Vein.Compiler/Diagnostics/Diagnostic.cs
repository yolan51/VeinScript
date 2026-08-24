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
}