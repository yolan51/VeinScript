namespace Vein.Compiler.Lexing;

public readonly record struct SourceSpan(string File, int Line, int Col, int Length)
{
    public override string ToString() => $"{File}:{Line}:{Col}";
}

public readonly record struct Token(
    TokenKind Kind,
    string Text,        // sigils stripped; for Ident this is the raw name
    object? Value,      // long / double / string for literals, else null
    SourceSpan Span)
{
    public override string ToString() =>
        Value is null ? $"{Kind}({Text})" : $"{Kind}({Text}={Value})";
}