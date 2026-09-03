using System.Text;

namespace Vein.Compiler.Tooling;

// A page showing every `Vein.WebTheme` component with the theme's own CSS applied.
//
// WHY A PAGE AND NOT A LIST. The done-bar is "pick `&Code` and `&Button` by sight", and a list of class
// names is not sight — `.callout` and `.card` are equally opaque as words and instantly different as
// boxes. The theme's whole purpose is that none of its component builders names a colour; you choose
// between them by looking.
//
// The CSS is read out of stdlib/WebTheme.vein rather than reimplemented. Each component builder assigns
// a `css` field built from string literals, so concatenating those literals reconstructs exactly what
// the builder emits — and a gallery styled by a hand-copied stylesheet would drift from the theme it
// claims to show, which is the one thing it must not do.
public static class ThemeGallery
{
    /// Build the gallery from `WebTheme.vein`, or null when it is not where it was expected.
    public static string? Build(string stdlibDir)
    {
        string path = Path.Combine(stdlibDir, "WebTheme.vein");
        if (!File.Exists(path)) return null;

        string source;
        try { source = File.ReadAllText(path); }
        catch { return null; }

        string css = ExtractCss(source);
        if (css.Length == 0) return null;

        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\">")
          .Append("<title>Vein.WebTheme</title><style>").Append(css).Append("</style></head>")
          .Append("<body class=\"dark\">");

        sb.Append("<div class=\"hero\"><h1>Vein.WebTheme</h1>")
          .Append("<p>Every component with the theme's own CSS. None of these builders names a colour.</p></div>");

        sb.Append("<div class=\"row\">")
          .Append(Card("&Cards", "A card — background, border, lift on hover."))
          .Append(Card("&HeroArea", "The banner above."))
          .Append(Card("&Callouts", "See the callout below."))
          .Append("</div>");

        sb.Append("<div class=\"callout\"><p>A callout — for the sentence that matters on the page.</p></div>");

        sb.Append("<p>Inline code looks like <code class=\"inline-code\">bring Panel(?)</code> — that is <b>&amp;Code</b>.</p>");
        sb.Append("<pre class=\"code-block\"><code>shard S {\n    run once { emit @Print { text: \"hi\" } }\n}</code></pre>");

        sb.Append("<div class=\"grid\">")
          .Append(Card("&Grid", "The grid this pair sits in."))
          .Append(Card("&Row", "The row the cards above sit in."))
          .Append("</div>");

        sb.Append("<div class=\"clock\">12:34:56 — &amp;Clock uses tabular numerals so it does not jitter</div>");

        sb.Append("<p style=\"opacity:.6;font-size:.85rem\">Read from stdlib/WebTheme.vein. ")
          .Append("Anything missing here is a component whose CSS this could not recover — the theme is the source, not this page.</p>");

        return sb.Append("</body></html>").ToString();
    }

    private static string Card(string title, string body) =>
        $"<div class=\"card\"><h3>{title}</h3><p>{body}</p></div>";

    /// Every string literal that looks like CSS, in file order.
    ///
    /// A literal counts when it contains a `{`: the component builders assign `css = ".card{…}" + …`,
    /// and the prose around them is comments, which are not literals at all. Concatenating in order
    /// reproduces each builder's emitted stylesheet.
    private static string ExtractCss(string source)
    {
        var sb = new StringBuilder();
        bool inString = false;
        int start = 0;

        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];

            if (!inString && c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                // Skip to end of line — a commented-out example must not become part of the stylesheet.
                int nl = source.IndexOf('\n', i);
                if (nl < 0) break;
                i = nl;
                continue;
            }

            if (c != '"' || (i > 0 && source[i - 1] == '\\')) continue;

            if (!inString) { inString = true; start = i + 1; }
            else
            {
                inString = false;
                string literal = source[start..i];
                if (literal.Contains('{')) sb.Append(literal);
            }
        }

        return sb.ToString();
    }
}
