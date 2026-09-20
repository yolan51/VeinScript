namespace Vein.Compiler.Tooling;

/// <summary>One occurrence: which file, which line and column, and the line it sits on.</summary>
/// <param name="Line">1-based, like every other span in this compiler.</param>
/// <param name="Col">1-based, and the column of THIS occurrence — a line may hold several.</param>
/// <param name="Text">The whole line, trimmed of indentation, so a result list reads as code.</param>
public sealed record TextMatch(string File, int Line, int Col, string Text);

/// <summary>What a search found, and whether it stopped early.</summary>
/// <remarks>
/// TRUNCATION IS PART OF THE ANSWER, not a detail. A list that silently stops at two thousand rows
/// tells you a name is used in 2000 places when it is used in 40000, and you would act on that.
/// </remarks>
public sealed record TextSearchResult(IReadOnlyList<TextMatch> Matches, bool Truncated);

/// <summary>
/// Plain substring search over a set of files — the engine behind Find in Files.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY A PLAIN SUBSTRING AND NOT A REGEX.</b> What you search a VeinScript project for is a name:
/// `$Mover`, `@Collided`, `speed`. A sigil is not a regex metacharacter but `.` and `*` and `?` all
/// are, and they are in the language's own syntax everywhere — `*Vein.Console.Io.@Print` typed into a
/// regex box finds almost anything. A literal is what people mean, and a regex option is a separate
/// decision that should be made on evidence rather than reflex.
/// </para>
/// <para>
/// <b>EVERY OCCURRENCE, NOT EVERY LINE.</b> `target $Mover as m { m.Mover.speed = 0 }` mentions the
/// shape twice. Reporting the line once would make the second one unreachable by clicking, and the
/// column is what lets the caret land on the one you picked.
/// </para>
/// <para>
/// Lives here rather than in the Workbench so it can be tested: `Vein.Tests` references the compiler
/// and not the UI project, which is why the analysis a panel renders always lives on this side.
/// </para>
/// </remarks>
public static class TextSearch
{
    /// <summary>The most rows a search returns before it stops looking.</summary>
    /// <remarks>
    /// A guard on the UI, not on the search: building a hundred thousand list items freezes the window
    /// for a query — a stray `e` — that was going to be retyped anyway.
    /// </remarks>
    public const int DefaultLimit = 2000;

    /// <summary>Find <paramref name="needle"/> in each of <paramref name="files"/>.</summary>
    /// <param name="read">
    /// How a file's text is obtained. The default reads from disk; the editor overrides it so an open
    /// file is searched as it currently READS, not as it was last saved. Returning null skips the file,
    /// which is what an unreadable one should do — one locked file must not end the search.
    /// </param>
    public static TextSearchResult InFiles(
        IEnumerable<string> files, string needle,
        bool caseSensitive = false, Func<string, string?>? read = null, int limit = DefaultLimit)
    {
        var matches = new List<TextMatch>();
        if (string.IsNullOrEmpty(needle)) return new TextSearchResult(matches, false);

        read ??= path => { try { return File.ReadAllText(path); } catch { return null; } };
        var how = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        foreach (string file in files)
        {
            if (read(file) is not { } text) continue;

            int line = 0;
            foreach (string raw in text.Split('\n'))
            {
                line++;
                string content = raw.TrimEnd('\r');

                int from = 0;
                while (from <= content.Length - needle.Length)
                {
                    int at = content.IndexOf(needle, from, how);
                    if (at < 0) break;

                    matches.Add(new TextMatch(file, line, at + 1, content.Trim()));
                    if (matches.Count >= limit) return new TextSearchResult(matches, true);

                    // Past the match, not past one character: overlapping hits of the same name are one
                    // use, and `aaa` searched for `aa` should be one result and not two.
                    from = at + needle.Length;
                }
            }
        }

        return new TextSearchResult(matches, false);
    }
}
