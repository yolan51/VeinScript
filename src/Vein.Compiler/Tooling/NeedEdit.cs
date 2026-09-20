namespace Vein.Compiler.Tooling;

/// <summary>
/// Where a `need "Author.Bundle"` line goes, for an editor that just offered a name from a bundle this
/// file has not needed yet.
/// </summary>
/// <remarks>
/// <para>
/// <b>A BARE NAME WITHOUT ITS `need` RESOLVES TO NOTHING.</b> At a `target` or a `mark` the qualified
/// form is not available as a fallback (RULES 17b), so completing a shape from an un-needed bundle
/// leaves a line that compiles and matches no identity. Writing the import is the only outcome that
/// leaves the file working, and it is one line in a predictable place.
/// </para>
/// <para>
/// <b>PURE, AND HERE RATHER THAN IN THE EDITOR</b>, so the placement is testable: `Vein.Tests` is net8
/// and references only `Vein.Compiler`, so anything living in the Workbench cannot be covered
/// (docs/WORKBENCH.md).
/// </para>
/// </remarks>
public static class NeedEdit
{
    /// <summary>
    /// The offset and text to insert so <paramref name="source"/> needs <paramref name="bundleKey"/>,
    /// or null when it already does and nothing should change.
    /// </summary>
    /// <remarks>
    /// AFTER THE LAST EXISTING `need`, so the imports stay one block in the order they were added;
    /// otherwise on the line after `bundle … {`, which is where the first one goes. Indentation is
    /// copied from the line it follows rather than assumed, because a file indented with tabs should
    /// not acquire a space-indented line.
    /// </remarks>
    public static (int Offset, string Text)? For(string source, string bundleKey)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrWhiteSpace(bundleKey)) return null;

        string line = $"need \"{bundleKey}\"";
        if (Already(source, bundleKey)) return null;

        int lastNeed = LastLineStarting(source, "need ");
        if (lastNeed >= 0)
        {
            int end = EndOfLine(source, lastNeed);
            return (end, "\n" + Indent(source, lastNeed) + line);
        }

        // No imports yet: the line after the bundle header, indented one step in from it.
        int header = LastLineStarting(source, "bundle ");
        if (header < 0) return null;

        int headerEnd = EndOfLine(source, header);
        return (headerEnd, "\n" + Indent(source, header) + "    " + line);
    }

    /// <summary>Whether a `need` for this bundle is already written, aliased or not.</summary>
    private static bool Already(string source, string bundleKey)
    {
        string quoted = "\"" + bundleKey + "\"";
        foreach (string l in source.Split('\n'))
        {
            string t = l.TrimStart();
            if (t.StartsWith("need ", StringComparison.Ordinal) && t.Contains(quoted, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>The offset of the last line whose first word is <paramref name="prefix"/>, or -1.</summary>
    private static int LastLineStarting(string source, string prefix)
    {
        int found = -1, offset = 0;
        foreach (string l in source.Split('\n'))
        {
            if (l.TrimStart().StartsWith(prefix, StringComparison.Ordinal)) found = offset;
            offset += l.Length + 1;
        }
        return found;
    }

    private static int EndOfLine(string source, int lineStart)
    {
        int i = source.IndexOf('\n', lineStart);
        return i < 0 ? source.Length : i;
    }

    private static string Indent(string source, int lineStart)
    {
        int i = lineStart;
        while (i < source.Length && source[i] is ' ' or '\t') i++;
        return source[lineStart..i];
    }
}
