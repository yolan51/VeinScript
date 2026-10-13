using Vein.Compiler.Diagnostics;
using Vein.Compiler.Lexing;

namespace Vein.Hosting;

/// What a piece of a line IS, for an editor that colours VeinScript — the same categories, and the same
/// colours if it likes, as the Workbench's VeinScript.xshd.
public enum VeinTokenKind { Keyword, Comment, String, Number, Shape, Mark, Event, Builder, Shard }

/// One coloured run: a 0-based column within the line and its length.
public readonly record struct VeinToken(int Column, int Length, VeinTokenKind Kind);

/// Classifies one line with the COMPILER'S OWN LEXER, so an editor's colours can never disagree with what
/// the language parses: a word is a keyword exactly when `Lexer` says so, `!Name` is a shard exactly when
/// it lexes as one. Engine-neutral — a Godot syntax highlighter, an LSP or a web view maps the kinds to
/// colours.
///
/// Per line, because that is how editors ask. The cost of that: a line in the MIDDLE of a multi-line
/// `/* … */` comment is not known to be one. `//` comments, which are what VeinScript code uses, are exact.
public static class VeinSyntax
{
    public static IReadOnlyList<VeinToken> Classify(string line)
    {
        var result = new List<VeinToken>();
        if (string.IsNullOrEmpty(line)) return result;

        var tokens = new Lexer(line, "line", new DiagnosticBag()).Tokenize();
        var strings = new List<(int Start, int End)>();

        foreach (var t in tokens)
        {
            VeinTokenKind? kind = t.Kind switch
            {
                TokenKind.String => VeinTokenKind.String,
                TokenKind.Int or TokenKind.Float or TokenKind.Percent => VeinTokenKind.Number,
                TokenKind.ShapeRef => VeinTokenKind.Shape,
                TokenKind.MarkRef => VeinTokenKind.Mark,
                TokenKind.EventRef => VeinTokenKind.Event,
                TokenKind.BuilderRef => VeinTokenKind.Builder,
                TokenKind.ShardRef => VeinTokenKind.Shard,
                _ when t.Kind.ToString().StartsWith("Kw", StringComparison.Ordinal) => VeinTokenKind.Keyword,
                _ => null,
            };
            if (kind is null || t.Span.Length <= 0 || t.Span.Line != 1) continue;
            int column = t.Span.Col - 1;
            result.Add(new VeinToken(column, Math.Min(t.Span.Length, line.Length - column), kind.Value));
            if (kind == VeinTokenKind.String) strings.Add((column, column + t.Span.Length));
        }

        // The lexer DROPS comments, so they are found here: the first `//` (or `/*`) that is not inside a
        // string token. `//` runs to the end of the line; `/* … */` to its close on this line, if any.
        for (int i = 0; i + 1 < line.Length; i++)
        {
            if (line[i] != '/' || (line[i + 1] != '/' && line[i + 1] != '*')) continue;
            if (strings.Any(s => i >= s.Start && i < s.End)) continue;
            int end = line.Length;
            if (line[i + 1] == '*' && line.IndexOf("*/", i + 2, StringComparison.Ordinal) is var close and >= 0) end = close + 2;
            result.RemoveAll(t => t.Column >= i && t.Column < end);
            result.Add(new VeinToken(i, end - i, VeinTokenKind.Comment));
            if (end == line.Length) break;
            i = end - 1;
        }

        result.Sort((a, b) => a.Column.CompareTo(b.Column));
        return result;
    }
}
