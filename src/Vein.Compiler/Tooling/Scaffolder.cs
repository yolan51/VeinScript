using System.Text;
using System.Text.Json;
using Vein.Compiler.Parsing;

namespace Vein.Compiler.Tooling;

/// What `veinc scaffold` prints: the text, or why there is none.
public sealed record ScaffoldResult(string? Text, string? Error, Signature? Hit);

// WHY: the Workbench's `?` shows a signature in place; outside it — VS Code, a terminal — this is the same
// answer on stdout. It takes every way a reference is written (`@Sold`, `&Crate`, `$Price`, a bare name,
// `*kit.Shop.Api.@Sold`, an alias's `*S.Api.&Crate`) and prints EXACTLY what `?` would insert, so what you
// copy from the terminal is what the editor would have written. Resolution is `Signatures` — the compiler's
// own lookup — so a kit in `bundles/` and a bundle the file does not `need` both work, and the answer does
// not depend on the directory the command was run from.
public static class Scaffolder
{
    public static ScaffoldResult Render(CompilationUnit unit, string? projectDir, string reference, string fileLabel)
    {
        // Forgiving about quoting: a run line or a shell may hand over `'$Price'` with its quotes on.
        string raw = reference.Trim().Trim('\'', '"');
        if (SignatureRef.Parse(raw) is not { } r)
            return Fail($"'{reference}' is not a reference — write @Event, &Builder, $Shape, a bare name, or a path " +
                        "like '*Author.Bundle.Publicator.@Event'.");

        var scope = Signatures.For(unit, projectDir);
        var lookup = scope.Find(r);

        if (lookup.Ambiguous)
        {
            var sb = new StringBuilder($"'{r.Written}' is ambiguous here — two bundles this file needs both share it (VS0216). Choose one:");
            foreach (var c in lookup.Candidates.OrderBy(c => c, StringComparer.Ordinal))
                sb.Append("\n  veinc scaffold ").Append(fileLabel).Append(" '").Append(PathOf(c, r.Kind)).Append('\'');
            return Fail(sb.ToString());
        }

        if (lookup.Hit is not { } sig)
        {
            var near = new[] { SignatureKind.Event, SignatureKind.Builder, SignatureKind.Shape }
                .SelectMany(k => scope.InScope(k).Select(s => s.Sigil + s.Name))
                .Distinct(StringComparer.Ordinal);
            return Fail($"no event, builder or shape '{r.Written}' is reachable from {fileLabel}. " +
                        $"In scope here: {string.Join(", ", near)}. A member of a bundle this file does not need " +
                        "is reached by its path — '*Author.Bundle.Publicator.@Name' (`veinc symbols` lists them).");
        }

        return new ScaffoldResult(Text(sig, r), null, sig);
    }

    /// The comment header, then the head as it was asked for, then the body `?` inserts.
    public static string Text(Signature sig, SignatureRef asked)
    {
        var sb = new StringBuilder();
        bool qualified = asked.Path.Count > 0;

        switch (sig.Kind)
        {
            case SignatureKind.Event:
                sb.Append("// @").Append(sig.Name);
                if (sig.Origin is not null) sb.Append(" is ").Append(sig.Reference);
                if (sig.Doc is { Length: > 0 } doc) sb.Append(" — ").Append(doc);
                sb.Append('\n');
                sb.Append("emit ").Append(qualified ? sig.Reference : "@" + sig.Name).Append(' ').Append(QuestionFill.Full(sig));
                break;

            case SignatureKind.Builder:
                sb.Append("// &").Append(sig.Name).Append(" builds ").Append(sig.Generates);
                if (sig.Marks.Count > 0) sb.Append(' ').Append(string.Join(" ", sig.Marks.Select(m => "#" + m)));
                if (sig.Origin is not null) sb.Append("   (").Append(sig.Reference).Append(')');
                if (sig.Base is not null)
                    sb.Append("\n// a variant of &").Append(sig.Base).Append(", fixing ")
                      .Append(string.Join(", ", sig.Fixed.Select(kv => kv.Key + " = " + kv.Value)));
                sb.Append('\n');
                sb.Append("bring ").Append(qualified ? sig.Reference : sig.Name).Append(QuestionFill.Full(sig));
                break;

            default:
                sb.Append("// $").Append(sig.Name);
                if (sig.Origin is not null) sb.Append(" is ").Append(sig.Reference);
                if (sig.Doc is { Length: > 0 } sdoc) sb.Append(" — ").Append(sdoc);
                sb.Append('\n');
                if (sig.Marks.Count > 0)
                    sb.Append("// attaching it also marks ").Append(string.Join(" ", sig.Marks.Select(m => "#" + m))).Append('\n');
                // `attach` takes a BARE shape (RULES 17b) — a path is how you FIND it, not how you write it.
                if (qualified && sig.Origin is { } origin)
                    sb.Append("// attach takes the bare name: `need \"").Append(string.Join(".", origin.Split('.').Take(2)))
                      .Append("\"` makes $").Append(sig.Name).Append(" mean this one (RULES 17b)\n");
                sb.Append("attach $").Append(sig.Name).Append(" to e ").Append(QuestionFill.Full(sig));
                break;
        }
        return sb.Append('\n').ToString();
    }

    /// The result for a tool to read — a VS Code task, or an extension one day.
    public static string Json(ScaffoldResult result) => JsonSerializer.Serialize(result.Hit is not { } s ? null : new
    {
        reference = s.Reference,
        kind = s.Kind.ToString().ToLowerInvariant(),
        owner = s.Origin,
        doc = s.Doc,
        fields = s.Fields.Select(f => new { name = f.Name, type = f.Type, required = f.Required, @default = f.Default, from = f.OriginShape }),
        marks = s.Marks,
        generates = s.Generates,
        @base = s.Base,
        @fixed = s.Fixed,
        text = result.Text,
    }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static ScaffoldResult Fail(string why) => new(null, why, null);

    private static string PathOf(string key, SignatureKind? kind)
    {
        int dot = key.LastIndexOf('.');
        string sigil = kind switch { SignatureKind.Builder => "&", SignatureKind.Shape => "$", _ => "@" };
        return "*" + key[..(dot + 1)] + sigil + key[(dot + 1)..];
    }
}
