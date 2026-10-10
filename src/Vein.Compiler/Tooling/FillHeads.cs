using System.Text.RegularExpressions;

namespace Vein.Compiler.Tooling;

/// A statement head the tooling recognises: what is being emitted, brought or attached, as written.
/// `Path` is the `*` qualifier without the star (empty when bare); `Keyword` is `emit`, `start`, `bring`
/// or `attach`.
public sealed record FillHead(SignatureKind Kind, IReadOnlyList<string> Path, string Name, string Keyword);

// WHY ONE SET: `?`, the in-brace popup, signature help and hover each recognised "an emit/bring waiting
// for its fields" with a pattern of their own, and each missed a different form. Signature help could not
// see past an `&` or a count — `bring *Vein.Console.Io.&Console(`, the samples' own spelling, got no hints
// at all — while `?` accepted a qualified emit but not a qualified start. One set, tested once.
public static class FillHeads
{
    /// `emit @E`, `start @E`, `emit *A.B.P.@E`.
    public const string Event = @"(?<![\w.])(?<kw>emit|start)\s+(?:\*(?<path>\w+(?:\.\w+)*)\.)?@(?<name>\w+)";

    /// `bring X`, `bring &X`, `bring 8 X`, `bring n &X`, `bring *A.B.P.&X`, `bring *A.B.P.X`. The count is
    /// a number or a name followed by another word, so `bring Unit` never reads `Unit` as a count.
    public const string Bring =
        @"(?<![\w.])bring\s+(?:(?:\d+|\w+)\s+(?=[*&\w]))?(?:\*(?<path>\w+(?:\.\w+)*)\.)?&?(?<name>\w+)";

    /// `attach $S to e` — bare only, as the language requires at a use site (RULES 17b).
    public const string Attach = @"(?<![\w.])attach\s+\$(?<name>\w+)\s+to\s+(?<target>\S(?:[^{}\n]*\S)?)";

    /// The head that ENDS at `end` — only whitespace between — on the same line, or null. `end` is where
    /// the `(`, the `{` or a typed `?` sits.
    public static FillHead? Before(string text, int end)
    {
        if (end < 0 || end > text.Length) return null;
        int lineStart = end == 0 ? 0 : text.LastIndexOf('\n', end - 1) + 1;
        string line = text[lineStart..end];

        foreach (var (kind, pattern) in Patterns)
        {
            var m = Regex.Match(line, pattern + @"\s*$");
            if (!m.Success) continue;
            var path = m.Groups["path"].Success ? m.Groups["path"].Value.Split('.') : Array.Empty<string>();
            string keyword = kind switch
            {
                SignatureKind.Event => m.Groups["kw"].Value,
                SignatureKind.Builder => "bring",
                _ => "attach",
            };
            return new FillHead(kind, path, m.Groups["name"].Value, keyword);
        }
        return null;
    }

    private static readonly (SignatureKind Kind, string Pattern)[] Patterns =
    {
        (SignatureKind.Event, Event),
        (SignatureKind.Builder, Bring),
        (SignatureKind.Shape, Attach),
    };
}
