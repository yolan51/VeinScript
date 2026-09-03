namespace Vein.Compiler.Tooling;

/// Finding the partner of a brace. Separate from the renderer because it is the part with a rule in it.
public static class BracketMatcher
{
    /// The pair covering the caret, or null. Looks at the character before the caret as well as the one
    /// after it — a caret sitting just past a `}` is on it, as far as a reader is concerned.
    ///
    /// Strings and comments are skipped, because `"{"` in a text literal is not a brace and matching it
    /// would point at the wrong partner for everything after it. That happens constantly in this
    /// codebase's samples, which emit HTML.
    public static (int Open, int Close)? Match(string text, int caret)
    {
        foreach (int at in new[] { caret, caret - 1 })
        {
            if (at < 0 || at >= text.Length) continue;
            if (InStringOrComment(text, at)) continue;

            char c = text[at];
            if (c == '{' && Forward(text, at) is { } close) return (at, close);
            if (c == '}' && Backward(text, at) is { } open) return (open, at);
        }
        return null;
    }

    private static int? Forward(string text, int from)
    {
        int depth = 0;
        for (int i = from; i < text.Length; i++)
        {
            if (InStringOrComment(text, i)) continue;
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return i;
        }
        return null;
    }

    private static int? Backward(string text, int from)
    {
        int depth = 0;
        for (int i = from; i >= 0; i--)
        {
            if (InStringOrComment(text, i)) continue;
            if (text[i] == '}') depth++;
            else if (text[i] == '{' && --depth == 0) return i;
        }
        return null;
    }

    /// Whether `at` falls inside a string literal or a `//` comment.
    ///
    /// Scanned from the start of the line rather than the start of the file: VeinScript has no
    /// multi-line strings or block comments, so a line is self-contained, and re-scanning the whole file
    /// per character would make matching quadratic on a long one.
    private static bool InStringOrComment(string text, int at)
    {
        int lineStart = text.LastIndexOf('\n', Math.Max(0, Math.Min(at, text.Length - 1))) + 1;

        bool inString = false;
        for (int i = lineStart; i < at && i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"' && (i == 0 || text[i - 1] != '\\')) inString = !inString;
            else if (!inString && c == '/' && i + 1 < text.Length && text[i + 1] == '/') return true;
        }
        return inString;
    }
}
