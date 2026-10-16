using Vein.Compiler.Diagnostics;
using Vein.Compiler.Lexing;
using Vein.Compiler.Parsing;

namespace Vein.Compiler.Project;

/// `vein.settings` — a project's values for the SETTINGS its shards declare with `tune("…")` (D21).
///
/// ```
/// # this project's tuned values; the .vein sources keep their defaults
/// [kit.Cameras]
/// Fly.speed = 12.0
///
/// [you.MyGame]
/// Move.speed = 6.5
/// ```
///
/// ONE FILE PER PROJECT, A SECTION PER BUNDLE. A setting belongs to the project, not to the bundle that
/// declares it: one library serves many games, each tuned its own way — the reasoning that gives each
/// project one `vein.lock`. Keys are qualified (`[Author.Bundle]` + `Shard.var`), so any number of
/// needed bundles share the file without colliding.
///
/// A value is a VeinScript literal (`6.5`, `-2`, `30%`, `"text"`, `true`). The file is meant to be read
/// and edited by hand too, so a write changes one line and keeps every comment, blank line and order.
/// Deleting a line is resetting the setting to the default written in its source.
public sealed class SettingsFile
{
    public const string FileName = "vein.settings";

    /// One `name = value` line, under its `[section]`. `Line` is 1-based.
    public sealed record Entry(string Section, string Name, string Value, int Line)
    {
        public string Key => Section + "." + Name;
    }

    /// Where the file lives (whether or not it exists yet).
    public string Path { get; }
    public IReadOnlyList<Entry> Entries { get; }
    /// Lines that are neither a section, an entry, a comment nor blank: (line, text).
    public IReadOnlyList<(int Line, string Text)> Malformed { get; }

    private SettingsFile(string path, IReadOnlyList<Entry> entries, IReadOnlyList<(int, string)> malformed)
    {
        Path = path;
        Entries = entries;
        Malformed = malformed;
    }

    /// The project's settings file for a program in `startDir`: beside the nearest `.veinproj` walking up
    /// — the project root a starter game has — else in `startDir` itself.
    public static string Locate(string startDir)
    {
        string full = System.IO.Path.GetFullPath(startDir);
        for (var d = new DirectoryInfo(full); d is not null; d = d.Parent)
            if (File.Exists(System.IO.Path.Combine(d.FullName, VeinProject.FileName)))
                return System.IO.Path.Combine(d.FullName, FileName);
        return System.IO.Path.Combine(full, FileName);
    }

    /// Read the project's file; empty when there is none.
    public static SettingsFile Load(string startDir) => Read(Locate(startDir));

    public static SettingsFile Read(string path)
    {
        string text;
        try { text = File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch (IOException) { text = ""; }
        return Parse(path, text);
    }

    public static SettingsFile Parse(string path, string text)
    {
        var entries = new List<Entry>();
        var malformed = new List<(int, string)>();
        string section = "";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
            int eq = line.IndexOf('=');
            if (eq <= 0 || section.Length == 0) { malformed.Add((i + 1, lines[i])); continue; }
            entries.Add(new Entry(section, line[..eq].Trim(), line[(eq + 1)..].Trim(), i + 1));
        }
        return new SettingsFile(path, entries, malformed);
    }

    /// The value for `Author.Bundle` + `Shard.var`; the last line wins if one is written twice.
    public Entry? Find(string section, string name) =>
        Entries.LastOrDefault(e => e.Section == section && e.Name == name);

    /// A value as the literal it spells, when it is one of `wanted` (any literal when null): `6.5`, `-2`,
    /// `30%`, `"text"`, `true`. An int is accepted for a float, as in source, and becomes one. Null when it
    /// does not fit — the caller reports it and keeps the source's default.
    public static Expr? ParseValue(string text, LiteralKind? wanted, SourceSpan span)
    {
        var tokens = new Lexer(text, FileName, new DiagnosticBag()).Tokenize()
            .Where(t => t.Kind is not (TokenKind.Term or TokenKind.EndOfFile)).ToList();
        bool negative = tokens.Count == 2 && tokens[0].Kind == TokenKind.Minus;
        if (!(tokens.Count == 1 || negative)) return null;
        var token = tokens[^1];
        LiteralKind? kind = token.Kind switch
        {
            TokenKind.Int => LiteralKind.Int, TokenKind.Float => LiteralKind.Float, TokenKind.Percent => LiteralKind.Percent,
            TokenKind.String => LiteralKind.String, TokenKind.KwTrue or TokenKind.KwFalse => LiteralKind.Bool, _ => null,
        };
        if (kind is null || (negative && kind is LiteralKind.String or LiteralKind.Bool)) return null;
        bool widen = kind == LiteralKind.Int && wanted == LiteralKind.Float;
        if (wanted is not null && kind != wanted && !widen) return null;
        object? value = token.Kind switch
        {
            TokenKind.KwTrue => true,
            TokenKind.KwFalse => false,
            _ when widen => Convert.ToDouble(token.Value),
            _ => token.Value,
        };
        var literal = new LiteralExpr(value, widen ? LiteralKind.Float : kind.Value, span);
        return negative ? new UnaryExpr(UnOp.Neg, literal, span) : literal;
    }

    /// Write `name = value` under `[section]` in the file at `path`: the existing line is replaced in place,
    /// else it is added at the end of its section, else a new section is appended. Everything else stays.
    public static void Set(string path, string section, string name, string value)
    {
        var lines = ReadLines(path);
        var (start, end, at) = Find(lines, section, name);
        string entry = $"{name} = {value}";
        if (at >= 0) lines[at] = entry;
        else if (start >= 0)
        {
            // After the section's last entry — not after the blank lines and the comment that open the next.
            int insert = start + 1;
            for (int i = start + 1; i < end; i++)
                if (lines[i].Trim() is { Length: > 0 } t && !t.StartsWith('#')) insert = i + 1;
            lines.Insert(insert, entry);
        }
        else
        {
            if (lines.Count == 0)
                lines.Add("# vein.settings — this project's values for its shards' `tune` settings. Delete a line to go back to the default in the source.");
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add(entry);
        }
        Write(path, lines);
    }

    /// Remove `name` from `[section]` (back to the source's default). False when it was not there.
    public static bool Remove(string path, string section, string name)
    {
        var lines = ReadLines(path);
        var (_, _, at) = Find(lines, section, name);
        if (at < 0) return false;
        lines.RemoveAt(at);
        Write(path, lines);
        return true;
    }

    /// (section header index, index after the section, index of the entry) — -1 where absent.
    private static (int Start, int End, int At) Find(List<string> lines, string section, string name)
    {
        int start = -1, end = lines.Count, at = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            string t = lines[i].Trim();
            if (t.StartsWith('[') && t.EndsWith(']'))
            {
                if (start >= 0) { end = i; break; }
                if (t[1..^1].Trim() == section) start = i;
                continue;
            }
            if (start < 0 || t.StartsWith('#')) continue;
            int eq = t.IndexOf('=');
            if (eq > 0 && t[..eq].Trim() == name) at = i;
        }
        return (start, end, at);
    }

    private static List<string> ReadLines(string path)
    {
        if (!File.Exists(path)) return new List<string>();
        var lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);   // the final newline
        return lines;
    }

    private static void Write(string path, List<string> lines)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
    }
}
