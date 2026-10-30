using System.Text;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Parsing;
using Vein.Compiler.Project;
using Vein.Compiler.Service;

namespace Vein.Compiler.Tooling;

/// <summary>
/// `veinc uwu` — makes a .vein file look like VeinScript: C# spellings become VeinScript words, the long
/// forms become the short ones, and what only a person can restructure is said as a hint.
/// </summary>
/// <remarks>
/// <para>
/// <b>WHY.</b> Code written from C# instinct is the most common VeinScript there is: an LLM in a fresh session
/// writes `a != b;`, `int x = 0`, `-> int`, a bare `print(…)`, and a game's state as eight shard `var`s. The
/// compiler rejects the first few one at a time; the last compiles and reads like C#. The corpus has the long
/// forms by the hundred (docs/CORPUS.md rows 1, 5, 6): `emit *Vein.Console.Io.@Print { text: … }` where
/// `do print(…)` exists, `x = x + 1` where `+=` does, `"HP: " + hp` where `"HP: \(hp)"` does.
/// </para>
/// <para>
/// <b>EACH REWRITE IS CERTAIN, AND EACH PASS IS CHECKED.</b> A rewrite is made only when the new text means
/// exactly what the old one did (`x = x + a + b` is NOT `x += a + b` when x is text; `x + " left"` is not
/// `"\(x) left"` when x is a list) — the same bar QuickFixes sets. Then every pass is compiled: a pass that
/// leaves more errors of any code than the file had is dropped whole and named in `Skipped`, so a case the
/// rules did not foresee costs a missed rewrite, never a broken file.
/// </para>
/// <para>
/// <b>WHAT IS NOT REWRITTEN IS HINTED.</b> "These five shard vars are a hero and a slime" is a redesign
/// (RULES-OF-THUMB 1), and doing it for the author would be choosing their shapes. A hint names the rule and
/// the VeinScript form; the author makes the change.
/// </para>
/// </remarks>
public static class Uwu
{
    /// One rewrite: where it was (a line of the file as given), which rule, and the line before and after.
    public sealed record Change(int Line, string Rule, string Before, string After);

    /// Something to restructure by hand. `Line` is in the rewritten text, `OriginalLine` in the text given.
    public sealed record Hint(int Line, int OriginalLine, string Rule, string Text);

    public sealed record Report(string Text, IReadOnlyList<Change> Changes, IReadOnlyList<Hint> Hints, IReadOnlyList<string> Skipped);

    /// The rewritten <paramref name="source"/>, what changed, and the hints. `path` makes the file's
    /// fragments and bundles part of each check, as `veinc check` would see them.
    public static Report Tidy(string source, string? path = null, string? projectDir = null)
    {
        // A file that breaks a rule on purpose says so (RULES-OF-THUMB, last line): a sample teaching the
        // full `*Vein.Console.Io.@Print` path is not to be "fixed" into the short one.
        if (source.Split('\n').Any(l => l.TrimStart().StartsWith(OffSwitch, StringComparison.Ordinal)))
            return new Report(source, Array.Empty<Change>(), Array.Empty<Hint>(), new[] { $"the whole file: it says `{OffSwitch}`" });

        string name = path is null ? "untitled.vein" : System.IO.Path.GetFileName(path);
        var service = new VeinCompilerService();
        CompilationResult Compile(string text)
        {
            using var _ = BundleSearch.Scope(projectDir);
            return service.Compile(new CompileRequest(name, text, ProjectDir: projectDir, SourcePath: path));
        }

        string text = source.Replace("\r\n", "\n");
        bool crlf = text.Length != source.Length;
        var changes = new List<Change>();
        var skipped = new List<string>();
        int[] toOrig = Enumerable.Range(0, LineCount(text) + 1).ToArray();   // current line → line given
        var result = Compile(text);

        var passes = new (string Rule, Func<string, CompilationResult, List<Edit>> Run)[]
        {
            ("no semicolons", (t, _) => Semicolons(Scan(t), t)),
            ("and / or / not", (t, _) => Logic(Scan(t), t)),
            ("the operator ends the line", (t, _) => Continuations(Scan(t), t)),
            ("C# declaration", (t, _) => Declarations(Scan(t), t)),
            ("\\( ) interpolation", (t, _) => ForeignStrings(Scan(t), t)),
            ("do print", Prints),
            (CompilerFixRule, (t, r) => CompilerFixes(t, r, name)),
            ("+=", (t, _) => Compound(Scan(t), t)),
            ("interpolation", (t, _) => Interpolate(Scan(t), t)),
        };

        foreach (var (rule, run) in passes)
        {
            // The compiler's fixes can unlock more of themselves (an `->` fixed lets the rest of the fn parse, and
            // its calls are then checked), so that pass runs until it finds nothing, and is judged once at the
            // end on the errors it leaves. Every other pass is one sweep.
            bool compilers = rule == CompilerFixRule;
            var (passText, passResult, passChanges, passMap) = (text, result, new List<Change>(), toOrig);
            for (int round = 0; round < (compilers ? 5 : 1); round++)
            {
                var edits = NonOverlapping(run(passText, passResult));
                if (edits.Count == 0) break;
                string next = Apply(passText, edits);
                passChanges.AddRange(Describe(passText, edits).Select(c => c with { Line = Orig(passMap, c.Line), Rule = rule }));
                passMap = Remap(passText, edits, passMap);
                passText = next;
                passResult = Compile(next);
            }
            if (passText == text) continue;

            string? why = compilers ? Errors(passResult) > Errors(result) ? "it left more errors than it fixed, so it was not applied" : null
                                    : Worse(result.Diagnostics, passResult.Diagnostics);
            if (why is not null) { skipped.Add($"{rule}: {why}"); continue; }
            changes.AddRange(passChanges);
            (text, result, toOrig) = (passText, passResult, passMap);
        }

        // One entry per line, however many passes touched it: the line as given, and as it ends.
        changes = changes.GroupBy(c => c.Line).Select(g => g.Count() == 1 ? g.First()
                      : new Change(g.Key, string.Join(", ", g.Select(c => c.Rule).Distinct()), g.First().Before, g.Last().After)).ToList();

        var hints = Hints(text, result, name).Select(h => h with { OriginalLine = Orig(toOrig, h.Line) }).ToList();
        changes.Sort((a, b) => a.Line.CompareTo(b.Line));
        hints.Sort((a, b) => a.Line.CompareTo(b.Line));
        return new Report(crlf ? text.Replace("\n", "\r\n") : text, changes, hints, skipped);
    }

    private const string CompilerFixRule = "do / return";

    /// The comment that keeps uwu out of a file.
    public const string OffSwitch = "// uwu: off";

    // ---- passes: C# spellings ------------------------------------------------------------------------

    /// `x = 1;` — a trailing semicolon goes; one between two statements becomes a line break. Inside `( )` it
    /// is a C `for` header, which no rewrite makes VeinScript, so it is left for the hint.
    private static List<Edit> Semicolons(List<T> toks, string s)
    {
        var edits = new List<Edit>();
        int depth = 0;
        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.Is("(") || t.Is("[")) depth++;
            else if ((t.Is(")") || t.Is("]")) && depth > 0) depth--;
            if (!t.Is(";") || depth > 0) continue;

            int next = NextCode(toks, i);
            int from = t.Start;
            while (from > 0 && s[from - 1] is ' ' or '\t') from--;
            if (next >= toks.Count || toks[next].Kind is K.Newline || toks[next].Is("}"))
                edits.Add(new Edit(from, t.End - from, ""));
            else
            {
                int to = t.End;
                while (to < s.Length && s[to] is ' ' or '\t') to++;
                edits.Add(new Edit(from, to - from, "\n" + IndentAt(s, t.Start)));
            }
        }
        return edits;
    }

    /// `&&` `||` `!=` `!x` → `and` `or` `not (a == b)` `not x` (mistake 1: VeinScript spells logic in words).
    private static List<Edit> Logic(List<T> toks, string s)
    {
        var edits = new List<Edit>();
        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.Is("&&") || t.Is("||"))
                edits.Add(new Edit(t.Start, t.End - t.Start, Spaced(s, t, t.Is("&&") ? "and" : "or")));
            else if (t.Is("!="))
            {
                int l = Side(toks, i, -1), r = Side(toks, i, +1);
                if (l < 0 || r < 0 || PrevCode(toks, l) is var p && p >= 0 && toks[p].IsWord("not")) continue;
                string left = s[toks[l].Start..t.Start].Trim(), right = s[t.End..toks[r].End].Trim();
                edits.Add(new Edit(toks[l].Start, toks[r].End - toks[l].Start, $"not ({left} == {right})"));
            }
            // `!x`, `!(…)`, `! x`. A `!` before a capital is a shard (`socket !TaskRow`, D19) and is left alone.
            else if ((t.Is("!") || t.Kind == K.Sigil && t.Text[0] == '!' && char.IsLower(t.Text[1]))
                     && ExpressionStarts(toks, i))
            {
                int next = i + 1;
                if (t.Is("!") && (next >= toks.Count || !(toks[next].Is("(") || toks[next].Kind is K.Word)))
                    continue;
                edits.Add(new Edit(t.Start, 1, t.Is("!") && s.Length > t.End && s[t.End] == ' ' ? "not" : "not "));
            }
        }
        return edits;
    }

    /// Mistakes 3 and 4: a newline ends a statement unless the line ends with an operator, so C#'s
    /// `+ "b"` at the start of a line moves to the end of the one before, and `}` newline `else` joins.
    private static List<Edit> Continuations(List<T> toks, string s)
    {
        var edits = new List<Edit>();
        for (int i = 0; i < toks.Count; i++)
        {
            if (toks[i].Kind != K.Newline) continue;
            int first = NextCode(toks, i);
            if (first >= toks.Count) break;
            var f = toks[first];
            int last = PrevCode(toks, i);
            if (last < 0 || toks[last].Kind == K.Newline || HasCommentBetween(toks, last, first)) continue;
            var l = toks[last];

            if ((f.Is("+") || f.IsWord("and") || f.IsWord("or")) && !Continues(l))
            {
                int to = f.End;
                while (to < s.Length && s[to] == ' ') to++;
                edits.Add(new Edit(l.End, 0, " " + f.Text));
                edits.Add(new Edit(f.Start, to - f.Start, ""));
            }
            // Only a `}` alone on its line: a `match` arm's `when X { … }` may be followed by `else { … }` on the next.
            else if (f.IsWord("else") && l.Is("}") && OnlyNewlinesBetween(toks, last, first) && StatementStarts(toks, last))
                edits.Add(new Edit(l.End, f.Start - l.End, " "));
        }
        return edits;
    }

    /// Mistake 9 and C's counters: `int x = 0` → `var x: int = 0`, `i++` → `i += 1`, at the start of a statement.
    private static List<Edit> Declarations(List<T> toks, string s)
    {
        var edits = new List<Edit>();
        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (!StatementStarts(toks, i)) continue;

            if (t.Kind == K.Word && t.Text is "int" or "float" or "string" or "bool" or "double"
                && NextCode(toks, i) is var n && n < toks.Count && toks[n].Kind == K.Word && !IsKeyword(toks[n].Text)
                && NextCode(toks, n) is var after && (after >= toks.Count || toks[after].Is("=") || toks[after].Kind == K.Newline))
            {
                string type = t.Text == "double" ? "float" : t.Text;
                edits.Add(new Edit(t.Start, toks[n].End - t.Start, $"var {toks[n].Text}: {type}"));
                continue;
            }

            // `i++` / `++i` / `i--` as a whole statement.
            int end = Path(toks, i);
            if (end > i && end < toks.Count && (toks[end].Is("++") || toks[end].Is("--")) && EndsStatement(toks, end + 1))
                edits.Add(new Edit(toks[end - 1].End, toks[end].End - toks[end - 1].End, toks[end].Is("++") ? " += 1" : " -= 1"));
            else if ((t.Is("++") || t.Is("--")) && Path(toks, i + 1) is var pe && pe > i + 1 && EndsStatement(toks, pe))
            {
                edits.Add(new Edit(t.Start, toks[i + 1].Start - t.Start, ""));
                edits.Add(new Edit(toks[pe - 1].End, 0, t.Is("++") ? " += 1" : " -= 1"));
            }
        }
        return edits;
    }

    /// C#'s `$"HP: {hp}"` and JavaScript's `` `HP: ${hp}` `` → `"HP: \(hp)"` (mistake 13b). A format
    /// specifier (`{x:F2}`) has no VeinScript spelling, so a string holding one is left for a person.
    private static List<Edit> ForeignStrings(List<T> toks, string s)
    {
        var edits = new List<Edit>();
        foreach (var t in toks)
        {
            if (t.Kind is not (K.CsString or K.JsString)) continue;
            bool js = t.Kind == K.JsString;
            string body = js ? s[(t.Start + 1)..(t.End - 1)] : s[(t.Start + 2)..(t.End - 1)];
            if (Holes(body, js) is { } converted) edits.Add(new Edit(t.Start, t.End - t.Start, "\"" + converted + "\""));
        }
        return edits;
    }

    private static string? Holes(string body, bool js)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (!js && c == '{' && i + 1 < body.Length && body[i + 1] == '{') { sb.Append('{'); i++; continue; }
            if (!js && c == '}' && i + 1 < body.Length && body[i + 1] == '}') { sb.Append('}'); i++; continue; }
            bool open = js ? c == '$' && i + 1 < body.Length && body[i + 1] == '{' : c == '{';
            if (open)
            {
                int from = i + (js ? 2 : 1), close = body.IndexOf('}', from);
                if (close < 0) return null;
                string expr = body[from..close].Trim();
                if (expr.Length == 0 || expr.Contains(':') || expr.Contains('{') || expr.Contains('"')) return null;
                sb.Append("\\(").Append(expr).Append(')');
                i = close;
                continue;
            }
            if (js && c == '"') { sb.Append("\\\""); continue; }
            if (js && c == '\\' && i + 1 < body.Length && body[i + 1] == '`') { sb.Append('`'); i++; continue; }
            if (c == '\\' && i + 1 < body.Length && body[i + 1] == '(') { sb.Append("\\\\("); i++; continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ---- passes: the short forms ---------------------------------------------------------------------

    /// `emit *Vein.Console.Io.@Print { text: x }` → `do print(x)`, the same for `@PrintInline`/`@PrintError`,
    /// `do *Vein.Console.Io.print(x)` → `do print(x)`, and C#'s `Console.WriteLine(x)` → `do print(x)` — with
    /// the file's `need "Vein.Console"` added when it has none (a bare name resolves only through one,
    /// RULES 17b). A bundle declaring its own `print` keeps the long form: its `SF print` usually IS that
    /// emit, and rewriting the body would make it call itself.
    private static List<Edit> Prints(string s, CompilationResult r)
    {
        var edits = new List<Edit>();
        if (DeclaresOwnPrint(r.Ast)) return edits;
        var toks = Scan(s);
        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.IsWord("emit") && StarPath(toks, i + 1, out int at, "Vein", "Console", "Io")
                && at < toks.Count && toks[at].Kind == K.Sigil && toks[at].Text[0] == '@' && Short(toks[at].Text[1..]) is { } sf
                && NextCode(toks, at) is var lb && lb < toks.Count && toks[lb].Is("{")
                && OnlyField(toks, lb, "text") is { } field)
            {
                var (valueFrom, valueTo, rb) = field;
                string value = s[toks[valueFrom].Start..toks[valueTo].End].Trim();
                edits.Add(new Edit(t.Start, toks[rb].End - t.Start, $"do {sf}({value})"));
                i = rb;
            }
            else if (t.IsWord("do") && StarPath(toks, i + 1, out int name, "Vein", "Console", "Io")
                     && name < toks.Count && toks[name].Kind == K.Word && toks[name].Text is "print" or "printInline" or "printError")
                edits.Add(new Edit(toks[i + 1].Start, toks[name].Start - toks[i + 1].Start, ""));
            else if (t.IsWord("Console") && StatementStarts(toks, i) && i + 3 < toks.Count && toks[i + 1].Is(".")
                     && toks[i + 2].Kind == K.Word && toks[i + 2].Text is "WriteLine" or "Write" && toks[i + 3].Is("("))
                edits.Add(new Edit(t.Start, toks[i + 2].End - t.Start, toks[i + 2].Text == "Write" ? "do printInline" : "do print"));
        }
        if (edits.Count > 0 && NeedEdit.For(s, "Vein.Console") is { } need) edits.Add(new Edit(need.Offset, 0, need.Text));
        return edits;

        static string? Short(string ev) => ev switch
        {
            "Print" => "print", "PrintInline" => "printInline", "PrintError" => "printError", _ => null
        };
    }

    /// VS0341 (`do` on an SF, none on an fn) and VS0349 (`->` for `return`): the compiler's own Quick Fixes,
    /// the ones a C# habit trips. Taken from QuickFixes so the two can never disagree about the edit.
    private static List<Edit> CompilerFixes(string s, CompilationResult r, string file)
    {
        var edits = new List<Edit>();
        foreach (var d in r.Diagnostics)
        {
            if (d.Code is not ("VS0341" or "VS0349") || !SameFile(d.Span.File, file)) continue;
            if (QuickFixes.For(d, s).FirstOrDefault() is { } fix)
                edits.Add(new Edit(fix.Edit.Offset, fix.Edit.Length, fix.Edit.Text));
        }
        return edits;
    }

    /// `x = x + n` → `x += n` (CORPUS row 6). Lowered, the two are the same assignment (Lower.LowerAssign), so
    /// the only question is whether the right side regroups: `x = x - a + b` is not `x -= a + b`, and
    /// `s = s + 1 + 2` is "s12" where `s += 1 + 2` is "s3". So what follows `x +` may hold only `*` `/` `%`
    /// — or, for `+`, start with a string, after which every `+` joins text either way.
    private static List<Edit> Compound(List<T> toks, string s)
    {
        var edits = new List<Edit>();
        for (int i = 0; i < toks.Count; i++)
        {
            if (!StatementStarts(toks, i)) continue;
            int targetEnd = Path(toks, i);
            if (targetEnd <= i || targetEnd + 1 >= toks.Count || !toks[targetEnd].Is("=")) continue;
            int repeat = targetEnd + 1, repeatEnd = Path(toks, repeat);
            if (repeatEnd - repeat != targetEnd - i) continue;
            bool same = true;
            for (int k = 0; k < targetEnd - i; k++) same &= toks[i + k].Text == toks[repeat + k].Text;
            if (!same || repeatEnd >= toks.Count) continue;

            var op = toks[repeatEnd];
            if (!(op.Is("+") || op.Is("-") || op.Is("*") || op.Is("/"))) continue;
            var rest = Operators(toks, repeatEnd + 1, out int restEnd, out bool startsWithString);
            if (rest is null || !EndsStatement(toks, restEnd)) continue;

            bool ok = op.Text switch
            {
                "+" => rest.All(o => o is "*" or "/" or "%") || startsWithString && rest.All(o => o == "+"),
                "-" => rest.All(o => o is "*" or "/" or "%"),
                _ => rest.Count == 0
            };
            if (!ok) continue;

            int restStart = toks[repeatEnd + 1].Start;
            edits.Add(new Edit(toks[targetEnd].Start, restStart - toks[targetEnd].Start, op.Text + "= "));
            i = restEnd;
        }
        return edits;
    }

    /// `"HP: " + hp + "/" + max` → `"HP: \(hp)/\(max)"` (D29, CORPUS row 1). The interpolation lexes to the
    /// same chain plus a trailing `""`, so the text is the same exactly when a string comes first: then every
    /// `+` already joins text. One value in front of the first string (`n + " left"`) is taken only when it
    /// cannot be a list — a field or a number: `xs + "a"` APPENDS to a list (Interp, IrBinOp.Add), where
    /// `"\(xs)a"` writes it out. Chains broken over lines are left as they are: they are long on purpose.
    private static List<Edit> Interpolate(List<T> toks, string s)
    {
        var edits = new List<Edit>();
        for (int i = 0; i < toks.Count; i++)
        {
            if (toks[i].Kind == K.Comment || !ChainMayStart(toks, i)) continue;
            var operands = new List<(int From, int To)>();
            int at = i;
            while (true)
            {
                int end = Operand(toks, at);
                if (end < 0) break;
                operands.Add((at, end));
                if (end + 1 < toks.Count && toks[end + 1].Is("+") && end + 2 < toks.Count && toks[end + 2].Kind != K.Newline)
                    at = end + 2;
                else break;
            }
            if (operands.Count < 2) continue;
            int last = operands[^1].To;
            if (!ChainMayEnd(toks, last + 1)) { i = last; continue; }

            bool IsText((int From, int To) o) => o.From == o.To && toks[o.From].Kind == K.String && toks[o.From].Text.Length >= 2 && toks[o.From].Text[^1] == '"';
            int firstText = operands.FindIndex(IsText);
            bool safeLead = firstText == 0
                         || firstText == 1 && (operands[0].To > operands[0].From && toks[operands[0].From + 1].Is(".")
                                               || operands[0].From == operands[0].To && toks[operands[0].From].Kind == K.Number);
            if (firstText < 0 || !safeLead || operands.All(IsText)) { i = last; continue; }

            var sb = new StringBuilder("\"");
            foreach (var o in operands)
            {
                if (IsText(o)) { sb.Append(s[(toks[o.From].Start + 1)..(toks[o.From].End - 1)]); continue; }
                string expr = s[toks[o.From].Start..toks[o.To].End].Trim();
                if (toks[o.From].Is("(") && Matching(toks, o.From) == o.To) expr = expr[1..^1].Trim();
                sb.Append("\\(").Append(expr).Append(')');
            }
            sb.Append('"');
            edits.Add(new Edit(toks[i].Start, toks[last].End - toks[i].Start, sb.ToString()));
            i = last;
        }
        return edits;
    }

    // ---- hints ---------------------------------------------------------------------------------------

    private static readonly HashSet<string> NotAThing = new(StringComparer.Ordinal)
    {
        "is", "has", "max", "min", "num", "last", "total", "cur", "current", "next", "prev", "old", "new", "my",
        "the", "on", "in", "to", "show", "can", "should", "did", "was", "start", "end", "first", "base", "default",
    };

    private static List<Hint> Hints(string s, CompilationResult r, string file)
    {
        var hints = new List<Hint>();
        void Add(int line, string rule, string text) => hints.Add(new Hint(line, line, rule, text));

        // C# words that have no rewrite: what VeinScript says instead (VEINSCRIPT-FOR-LLMS §2).
        var toks = Scan(s);
        for (int i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.Kind != K.Word || NextCode(toks, i) is var n && n < toks.Count && (toks[n].Is(":") || toks[n].Is("=") || toks[n].Is("."))) continue;
            if (!StatementStarts(toks, i) && !(t.Text == "new" && PrevCode(toks, i) is var p && p >= 0 && toks[p].Is("="))) continue;
            string? say = t.Text switch
            {
                "class" or "struct" => "VeinScript has no classes: the data is a `shape $Name { … }` on an identity (or a `type` for a plain value), the behaviour a `shard` (mistake 7).",
                "for" or "foreach" => "No `for`: `repeat n as i { }` counts, `while c { }` loops, `target xs as x { }` walks a list, `target $Shape as e { }` walks identities (mistake 6).",
                "switch" => "No `switch`: `match` an enum or a mark (`match e.State.kind { … }`), or `if … else if`.",
                "void" => "A function that does something is `SF name(…) { }`, called `do name(…)`; one that answers is `fn name(…) return int { }` (mistakes 8, 10).",
                "public" or "private" or "static" or "protected" => "No access words: a bundle's API is its `publicator`, and `shared(\"…\")` documents a member.",
                "new" => "No `new`: `spawn()` makes an identity, `attach $Shape to e { … }` gives it data, `[ ]` is a list.",
                _ => null
            };
            if (say is not null) Add(t.Line, "a C# word", say);
        }

        // `.Add(x)` / `.push(x)` on a list — mistake 11.
        for (int i = 0; i + 2 < toks.Count; i++)
            if (toks[i].Is(".") && toks[i + 1].Kind == K.Word && toks[i + 1].Text is "Add" or "add" or "push" or "Push" or "append"
                && toks[i + 2].Is("(") && i > 0 && toks[i - 1].Kind == K.Word && StatementStarts(toks, i - 1))
                Add(toks[i].Line, "a C# word", $"A list grows with `{toks[i - 1].Text} += value`; values have no methods (mistake 11). A `~` collection field uses `add … to` (RULES 13d).");

        if (r.Ast is null) return hints;

        foreach (var bundle in r.Ast.Bundles)
            foreach (var decl in Decls(bundle.Members))
            {
                if (decl is ShardDecl shard && SameFile(shard.Span.File, file)) ShardVars(shard, Add);
                if (decl is FuncDecl { Name: "print" or "printInline" or "printError" } f && SameFile(f.Span.File, file)
                    && f.Body.Statements is [EmitStmt { Event: var ev }] && ev.EndsWith("Print", StringComparison.Ordinal))
                    Add(f.Span.Line, "do print", $"This `{f.Name}` is the one `Vein.Console` already has: `need \"Vein.Console\"`, delete it, and run uwu again for the short prints.");
            }

        foreach (var (stmt, depth) in Statements(r.Ast, file))
        {
            switch (stmt)
            {
                case QueryStmt or TargetStmt when depth == 3:
                    Add(stmt.Span.Line, "a missing link", "A third `target` inside two others is a search for something the data should point at: give it a field (`$Parent`, `$Owner`, or your own), or `target … on x` (RULES-OF-THUMB 2, 4).");
                    break;
                case QueryStmt { Body.Statements: [IfStmt { Else: null, Cond: UnaryExpr { Op: UnOp.Not, Operand: CarriesExpr { IsShape: false, Subject: NameExpr n, Name: var mark } } }] } q
                    when n.Name == q.Bind:
                    Add(q.Span.Line, "say it in the query", $"Leave it out in the query itself: `target … not #{mark} as {q.Bind}` (D25, RULES-OF-THUMB 6).");
                    break;
                case Block b:
                    FoundFlags(b, Add);
                    break;
            }
        }

        // `fromJson("{}")` as a blank record, and saves joined by hand.
        for (int i = 0; i + 3 < toks.Count; i++)
        {
            if (toks[i].IsWord("fromJson") && toks[i + 1].Is("(") && toks[i + 2].Kind == K.String && toks[i + 2].Text == "\"{}\"" && toks[i + 3].Is(")"))
                Add(toks[i].Line, "data first", "`fromJson(\"{}\")` as a blank record: if each one is a thing in the program, a `shape` on an identity says what it holds and the compiler checks it (RULES-OF-THUMB 1).");
            if (toks[i].IsWord("join") && toks[i + 1].Is("(") && LineHas(toks, i, t => t.Kind == K.String && (t.Text.StartsWith("\"[") || t.Text.StartsWith("\"{"))))
                Add(toks[i].Line, "one loom each way", "A save joined by hand restates the format: `loom<Json> Saved { $Shape }` and `toText<Json>(Saved)` write it, `weave Saved(text)` reads it back (RULES-OF-THUMB 10).");
        }

        return hints.GroupBy(h => (h.Line, h.Text)).Select(g => g.First()).ToList();
    }

    /// Shard `var`s that are one thing's facts (`heroHp`, `heroGold`) or the same fact of two things
    /// (`heroHp`, `slimeHp`) — RULES-OF-THUMB 1. This is the C# program's object, still in the shard.
    private static void ShardVars(ShardDecl shard, Action<int, string, string> add)
    {
        var vars = shard.Members.OfType<VarDecl>().Where(v => v.Mutable).ToList();
        var split = vars.Select(v => (Var: v, Words: CamelWords(v.Name))).Where(x => x.Words.Count >= 2).ToList();

        foreach (var g in split.GroupBy(x => x.Words[0]).Where(g => g.Count() >= 2 && !NotAThing.Contains(g.Key)))
        {
            string thing = Upper(g.Key);
            var names = g.Select(x => LowerFirst(string.Concat(x.Words.Skip(1)))).ToList();
            string fields = string.Join(", ", g.Zip(names, (x, n) => n + ": " + TypeOf(x.Var)));
            add(g.First().Var.Span.Line, "data first",
                $"{Listed(g.Select(x => x.Var.Name))} are one {g.Key}'s facts: `shape ${thing} {{ {fields} }}` on an identity (`let {g.Key} = spawn()`), read as `{g.Key[0]}.{thing}.{names[0]}` in `target ${thing} as {g.Key[0]}` (RULES-OF-THUMB 1).");
        }

        foreach (var g in split.GroupBy(x => x.Words[^1]).Where(g => g.Select(x => x.Words[0]).Distinct().Count() >= 2))
        {
            var owners = g.Select(x => string.Concat(x.Words.SkipLast(1))).ToList();
            if (owners.Any(o => NotAThing.Contains(o))) continue;
            string field = LowerFirst(g.Key);
            add(g.First().Var.Span.Line, "data first",
                $"{Listed(owners)} {(owners.Count == 2 ? "both" : "all")} have `{field}`: make them identities carrying one shape, `shape ${Upper(g.Key)} {{ {field}: {TypeOf(g.First().Var)} }}`, and one `target ${Upper(g.Key)} as x` reaches them all (RULES-OF-THUMB 1).");
        }
    }

    private static string TypeOf(VarDecl v) => v.Type?.Name ?? (v.Init as LiteralExpr)?.Kind switch
    {
        LiteralKind.Int => "int", LiteralKind.Float => "float", LiteralKind.String => "string", LiteralKind.Bool => "bool", _ => "…"
    };

    private static string Listed(IEnumerable<string> items)
    {
        var l = items.ToList();
        return l.Count <= 1 ? string.Concat(l) : string.Join(", ", l.Take(l.Count - 1)) + " and " + l[^1];
    }

    /// `var found = false`, then a `target` that sets it: a search, which reads as an `fn` that answers it
    /// (RULES-OF-THUMB 12, CORPUS row 13).
    private static void FoundFlags(Block b, Action<int, string, string> add)
    {
        for (int i = 0; i < b.Statements.Count; i++)
        {
            if (b.Statements[i] is not LocalVarStmt { Decl: { Mutable: true, Init: LiteralExpr { Value: false } } d }) continue;
            foreach (var later in b.Statements.Skip(i + 1))
                if (later is QueryStmt or TargetStmt && Inner(later).Any(x => x is AssignStmt { Target: NameExpr n, Value: LiteralExpr { Value: true } } && n.Name == d.Name))
                {
                    add(d.Span.Line, "a question is an fn", $"`{d.Name}` is a search with a flag: `fn {d.Name}(…) return bool {{ target … {{ return true }} return false }}` names the question (RULES-OF-THUMB 12).");
                    break;
                }
        }
    }

    // ---- AST walking ---------------------------------------------------------------------------------

    private static IEnumerable<Node> Decls(IEnumerable<Node> members)
    {
        foreach (var m in members)
        {
            yield return m;
            if (m is PublicatorDecl p) foreach (var x in Decls(p.Members)) yield return x;
        }
    }

    private static bool DeclaresOwnPrint(CompilationUnit? ast) =>
        ast is not null && ast.Bundles.SelectMany(b => Decls(b.Members)).Concat(ast.Bundles.SelectMany(b => Decls(b.Members)).OfType<ShardDecl>().SelectMany(s => s.Members))
            .Any(d => d is FuncDecl { Name: "print" or "printInline" or "printError" });

    /// Every statement of this file, with how many `target`s enclose it (itself included).
    private static IEnumerable<(Stmt, int)> Statements(CompilationUnit ast, string file)
    {
        foreach (var bundle in ast.Bundles)
            foreach (var decl in Decls(bundle.Members))
            {
                if (!SameFile(decl.Span.File, file)) continue;
                var bodies = decl switch
                {
                    FuncDecl f => new[] { f.Body },
                    ShardDecl s => s.Members.Select(m => m switch { ScheduleBlock sb => sb.Body, HearBlock h => h.Body, FuncDecl f => f.Body, _ => null })
                                            .OfType<Block>().ToArray(),
                    _ => Array.Empty<Block>()
                };
                foreach (var body in bodies)
                    foreach (var x in Walk(body, 0)) yield return x;
            }
    }

    private static IEnumerable<(Stmt, int)> Walk(Stmt s, int depth)
    {
        int here = s is QueryStmt or TargetStmt ? depth + 1 : depth;
        yield return (s, here);
        foreach (var child in Children(s))
            foreach (var x in Walk(child, here)) yield return x;
    }

    private static IEnumerable<Stmt> Inner(Stmt s) => Children(s).SelectMany(c => Walk(c, 0).Select(x => x.Item1));

    private static IEnumerable<Stmt> Children(Stmt s) => s switch
    {
        Block b => b.Statements,
        IfStmt i => i.Else is Stmt e ? new Stmt[] { i.Then, e } : new Stmt[] { i.Then },
        WhileStmt w => new[] { w.Body },
        TargetStmt t => new[] { t.Body },
        QueryStmt q => new[] { q.Body },
        RepeatStmt r => new[] { r.Body },
        ChanceStmt c => new[] { c.Body },
        MatchStmt m => m.Arms.Select(a => (Stmt)a.Body).Concat(m.Else is null ? Array.Empty<Stmt>() : new Stmt[] { m.Else }),
        OrderedStmt o => o.Body,
        _ => Array.Empty<Stmt>()
    };

    // ---- the check after each pass -------------------------------------------------------------------

    /// Why `after` is worse than `before`, or null. A file that parses is judged per code — no code may have
    /// more errors than it had — not per position, because a pass moves lines. A file that does not parse is
    /// judged on how many parse errors (VS00xx, VS01xx) it has in all: where the parser recovers decides
    /// which codes the rest of the file gets, and a `;` removed lets it reach a line it never read before.
    private static string? Worse(IReadOnlyList<Diagnostic> before, IReadOnlyList<Diagnostic> after)
    {
        static bool Parse(Diagnostic d) => d.Code.StartsWith("VS00", StringComparison.Ordinal) || d.Code.StartsWith("VS01", StringComparison.Ordinal);
        var errorsBefore = before.Where(d => d.Severity == Severity.Error).ToList();
        var errorsAfter = after.Where(d => d.Severity == Severity.Error).ToList();
        if (errorsBefore.Any(Parse))
        {
            int was = errorsBefore.Count(Parse), now = errorsAfter.Count(Parse);
            return now > was ? $"it would leave {now} parse errors where there were {was}, so it was not applied" : null;
        }
        var count = errorsBefore.GroupBy(d => d.Code).ToDictionary(g => g.Key, g => g.Count());
        foreach (var g in errorsAfter.GroupBy(d => d.Code))
            if (g.Count() > count.GetValueOrDefault(g.Key))
                return $"it would leave {g.Key} ({g.First().Message}), so it was not applied";
        return null;
    }

    private static int Errors(CompilationResult r) => r.Diagnostics.Count(d => d.Severity == Severity.Error);

    private static bool SameFile(string spanFile, string file) =>
        string.Equals(System.IO.Path.GetFileName(spanFile), file, StringComparison.OrdinalIgnoreCase);

    // ---- edits ---------------------------------------------------------------------------------------

    private readonly record struct Edit(int Offset, int Length, string Text);

    private static List<Edit> NonOverlapping(List<Edit> edits)
    {
        var kept = new List<Edit>();
        int end = -1;
        foreach (var e in edits.OrderBy(e => e.Offset).ThenBy(e => e.Length))
        {
            if (e.Offset < end || e.Offset == end && e.Length == 0 && kept.Count > 0 && kept[^1].Length == 0 && kept[^1].Offset == e.Offset) continue;
            kept.Add(e);
            end = Math.Max(end, e.Offset + e.Length);
        }
        return kept;
    }

    private static string Apply(string s, List<Edit> edits)
    {
        var sb = new StringBuilder(s.Length);
        int at = 0;
        foreach (var e in edits)
        {
            sb.Append(s, at, e.Offset - at).Append(e.Text);
            at = e.Offset + e.Length;
        }
        return sb.Append(s, at, s.Length - at).ToString();
    }

    /// One Change per run of touched lines: the old lines and what they became.
    private static IEnumerable<Change> Describe(string s, List<Edit> edits)
    {
        var groups = new List<(int From, int To, List<Edit> Edits)>();
        foreach (var e in edits)
        {
            int from = LineStart(s, e.Offset), to = LineEnd(s, e.Offset + e.Length);
            if (groups.Count > 0 && from <= groups[^1].To) groups[^1] = (groups[^1].From, Math.Max(to, groups[^1].To), groups[^1].Edits.Append(e).ToList());
            else groups.Add((from, to, new List<Edit> { e }));
        }
        foreach (var (from, to, group) in groups)
        {
            string before = s[from..to];
            string after = Apply(before, group.Select(e => e with { Offset = e.Offset - from }).ToList());
            yield return new Change(LineOf(s, from), "", Squash(before), Squash(after));
        }
    }

    private static string Squash(string lines) => string.Join("\n", lines.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    /// The line map after `edits`: for each line of the new text, the line of the text first given. A line an
    /// edit adds belongs to the line the edit starts on.
    private static int[] Remap(string s, List<Edit> edits, int[] toOrig)
    {
        int Old(int line) => toOrig[Math.Min(line, toOrig.Length - 1)];
        var map = new List<int> { 0, Old(1) };
        int line = 1, at = 0;
        void Copy(int upTo)
        {
            for (; at < upTo; at++) if (s[at] == '\n') map.Add(Old(++line));
        }
        foreach (var e in edits)
        {
            Copy(e.Offset);
            int from = line;
            foreach (char c in e.Text) if (c == '\n') map.Add(Old(from));
            for (int k = e.Offset; k < e.Offset + e.Length; k++) if (s[k] == '\n') line++;
            at = e.Offset + e.Length;
        }
        Copy(s.Length);
        return map.ToArray();
    }

    private static int Orig(int[] toOrig, int line) => line >= 0 && line < toOrig.Length ? toOrig[line] : line;

    // ---- the scanner ---------------------------------------------------------------------------------
    //
    // Its own, rather than the Lexer's tokens, because a rewrite needs every character where it was: the
    // Lexer drops comments, gives an interpolated string as a `+` chain, and stops at `;` and `&&`, which are
    // exactly what this tool exists to read.

    private enum K { Word, Number, String, CsString, JsString, Sigil, Punct, Newline, Comment }

    private readonly record struct T(K Kind, int Start, int End, string Text, int Line)
    {
        public bool Is(string punct) => Kind == K.Punct && Text == punct;
        public bool IsWord(string word) => Kind == K.Word && Text == word;
    }

    private static readonly string[] Multi = { "==", "!=", "<=", ">=", "+=", "-=", "*=", "/=", "->", "&&", "||", "++", "--" };

    private static List<T> Scan(string s)
    {
        var toks = new List<T>();
        int i = 0, line = 1;
        while (i < s.Length)
        {
            char c = s[i];
            int start = i;
            if (c is ' ' or '\t' or '\r') { i++; continue; }
            if (c == '\n') { toks.Add(new T(K.Newline, i, i + 1, "\n", line)); line++; i++; continue; }
            if (c == '/' && At(s, i + 1) == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                toks.Add(new T(K.Comment, start, i, s[start..i], line));
                continue;
            }
            if (c == '/' && At(s, i + 1) == '*')
            {
                int close = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? s.Length : close + 2;
                toks.Add(new T(K.Comment, start, i, s[start..i], line));
                line += s[start..i].Count(ch => ch == '\n');
                continue;
            }
            if (c == '"')
            {
                i = StringEnd(s, i);
                toks.Add(new T(K.String, start, i, s[start..i], line));
                continue;
            }
            if (c == '$' && At(s, i + 1) == '"')
            {
                i = SimpleEnd(s, i + 1, '"');
                toks.Add(new T(K.CsString, start, i, s[start..i], line));
                continue;
            }
            if (c == '`')
            {
                i = SimpleEnd(s, i, '`');
                toks.Add(new T(K.JsString, start, i, s[start..i], line));
                continue;
            }
            if (c is '$' or '@' or '#' or '&' or '!' && (char.IsLetter(At(s, i + 1)) || At(s, i + 1) == '_'))
            {
                i++;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                toks.Add(new T(K.Sigil, start, i, s[start..i], line));
                continue;
            }
            if (char.IsDigit(c))
            {
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' && char.IsDigit(At(s, i + 1)))) i++;
                if (At(s, i) == '%') i++;
                toks.Add(new T(K.Number, start, i, s[start..i], line));
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                toks.Add(new T(K.Word, start, i, s[start..i], line));
                continue;
            }
            string op = Multi.FirstOrDefault(m => string.CompareOrdinal(s, i, m, 0, 2) == 0) ?? c.ToString();
            i += op.Length;
            toks.Add(new T(K.Punct, start, i, op, line));
        }
        return toks;
    }

    private static char At(string s, int i) => i >= 0 && i < s.Length ? s[i] : '\0';

    /// One past the closing quote of the string at `quote` — interpolations, with strings of their own,
    /// skipped whole, as the Lexer does. An unclosed string ends at its line.
    private static int StringEnd(string s, int quote)
    {
        for (int i = quote + 1; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\n') return i;
            if (c == '"') return i + 1;
            if (c != '\\') continue;
            if (At(s, i + 1) == '(')
            {
                int depth = 0;
                for (i += 2; i < s.Length && s[i] != '\n'; i++)
                {
                    if (s[i] == '"') { i = StringEnd(s, i) - 1; continue; }
                    if (s[i] == '(') depth++;
                    else if (s[i] == ')') { if (depth == 0) break; depth--; }
                }
            }
            else i++;
        }
        return s.Length;
    }

    private static int SimpleEnd(string s, int open, char quote)
    {
        for (int i = open + 1; i < s.Length && s[i] != '\n'; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == quote) return i + 1;
        }
        int nl = s.IndexOf('\n', open);
        return nl < 0 ? s.Length : nl;
    }

    // ---- reading tokens ------------------------------------------------------------------------------

    private static int NextCode(List<T> toks, int i)
    {
        int j = i + 1;
        while (j < toks.Count && toks[j].Kind == K.Comment) j++;
        return j;
    }

    private static int PrevCode(List<T> toks, int i)
    {
        int j = i - 1;
        while (j >= 0 && toks[j].Kind == K.Comment) j--;
        return j;
    }

    /// The previous token that matters to an expression: a newline after an operator continues the line
    /// (RULES 1), so it is skipped to that operator.
    private static int PrevInExpression(List<T> toks, int i)
    {
        int j = PrevCode(toks, i);
        if (j >= 0 && toks[j].Kind == K.Newline)
        {
            int k = j;
            while (k >= 0 && toks[k].Kind is K.Newline or K.Comment) k--;
            if (k >= 0 && Continues(toks[k])) return k;
        }
        return j;
    }

    private static bool Continues(T t) =>
        t.Kind == K.Punct && t.Text is not (")" or "]" or "}" or "{") || t.IsWord("and") || t.IsWord("or") || t.IsWord("not");

    private static bool StatementStarts(List<T> toks, int i)
    {
        int p = PrevCode(toks, i);
        return p < 0 || toks[p].Kind == K.Newline && (PrevInExpression(toks, i) is var q && (q < 0 || toks[q].Kind == K.Newline))
               || toks[p].Is("{") || toks[p].Is(";");
    }

    private static bool EndsStatement(List<T> toks, int i)
    {
        int n = i < toks.Count && toks[i].Kind == K.Comment ? NextCode(toks, i) : i;
        return n >= toks.Count || toks[n].Kind == K.Newline || toks[n].Is("}") || toks[n].Is(";") || toks[n].Kind == K.Comment;
    }

    private static bool ExpressionStarts(List<T> toks, int i)
    {
        int p = PrevInExpression(toks, i);
        if (p < 0) return true;
        var t = toks[p];
        return t.Kind == K.Newline || t.Kind == K.Punct && t.Text is "(" or "[" or "," or "=" or ":" or "{" or "&&" or "||" or "==" or "+="
               || t.Kind == K.Word && t.Text is "if" or "while" or "and" or "or" or "return" or "let" or "var";
    }

    private static bool HasCommentBetween(List<T> toks, int a, int b)
    {
        for (int k = a + 1; k < b; k++) if (toks[k].Kind == K.Comment) return true;
        return false;
    }

    private static bool OnlyNewlinesBetween(List<T> toks, int a, int b)
    {
        int lines = 0;
        for (int k = a + 1; k < b; k++)
        {
            if (toks[k].Kind != K.Newline) return false;
            lines++;
        }
        return lines == 1;
    }

    private static bool LineHas(List<T> toks, int i, Func<T, bool> test) =>
        toks.Where(t => t.Line == toks[i].Line).Any(test);

    /// `name`, `a.B.c`, `a.B.~list` — one past the last token of a place that can be assigned, or `i` itself.
    private static int Path(List<T> toks, int i)
    {
        if (i >= toks.Count || toks[i].Kind != K.Word || IsKeyword(toks[i].Text)) return i;
        int j = i + 1;
        while (j + 1 < toks.Count && toks[j].Is(".") && toks[j + 1].Kind is K.Word or K.Sigil) j += 2;
        return j;
    }

    /// `*Vein.Console.Io.` followed by the member: true, with `member` its index.
    private static bool StarPath(List<T> toks, int i, out int member, params string[] path)
    {
        member = -1;
        if (i >= toks.Count || !toks[i].Is("*")) return false;
        int j = i + 1;
        foreach (string part in path)
        {
            if (j + 1 >= toks.Count || !toks[j].IsWord(part) || !toks[j + 1].Is(".")) return false;
            j += 2;
        }
        member = j;
        return true;
    }

    /// `{ text: value }` holding only that field, possibly over several lines: the value's first and last
    /// token, and the `}`. Null for anything else (a second field, a `?`).
    private static (int, int, int)? OnlyField(List<T> toks, int lb, string field)
    {
        int f = lb + 1;
        while (f < toks.Count && toks[f].Kind is K.Newline or K.Comment) f++;
        if (f + 2 >= toks.Count || !toks[f].IsWord(field) || !toks[f + 1].Is(":")) return null;
        int rb = Matching(toks, lb);
        if (rb < 0) return null;
        int from = f + 2, to = rb - 1;
        while (to > from && toks[to].Kind is K.Newline or K.Comment) to--;
        int depth = 0;
        for (int k = from; k <= to; k++)
        {
            var t = toks[k];
            if (t.Kind == K.Comment) return null;
            if (t.Is("(") || t.Is("[") || t.Is("{")) depth++;
            else if (t.Is(")") || t.Is("]") || t.Is("}")) depth--;
            else if (depth == 0 && (t.Is(",") || t.Is("?"))) return null;
        }
        return to >= from ? (from, to, rb) : null;
    }

    private static int Matching(List<T> toks, int open)
    {
        string o = toks[open].Text, c = o switch { "(" => ")", "[" => "]", _ => "}" };
        int depth = 0;
        for (int k = open; k < toks.Count; k++)
        {
            if (toks[k].Is(o)) depth++;
            else if (toks[k].Is(c) && --depth == 0) return k;
        }
        return -1;
    }

    /// One side of an `!=`: the index of its farthest token, stopping where a comparison's operand stops —
    /// `and`, `or`, a comma, a bracket that is not its own, the line's end. -1 when that side is empty or
    /// holds another comparison.
    private static int Side(List<T> toks, int op, int step)
    {
        int depth = 0, far = -1;
        for (int k = op + step; k >= 0 && k < toks.Count; k += step)
        {
            var t = toks[k];
            if (t.Kind == K.Comment) break;
            bool opens = step < 0 ? t.Is(")") || t.Is("]") : t.Is("(") || t.Is("[");
            bool closes = step < 0 ? t.Is("(") || t.Is("[") : t.Is(")") || t.Is("]");
            if (opens) depth++;
            else if (closes) { if (depth == 0) break; depth--; }
            else if (depth == 0)
            {
                if (t.Kind == K.Newline || t.Is(",") || t.Is("{") || t.Is("}") || t.Is("=") || t.Is(":") || t.Is("&&") || t.Is("||") || t.Is(";")
                    || t.Kind == K.Word && t.Text is "and" or "or" or "if" or "while" or "return" or "not" or "let" or "var")
                    break;
                if (t.Kind == K.Punct && t.Text is "==" or "!=" or "<" or ">" or "<=" or ">=") return -1;
            }
            far = k;
        }
        return depth == 0 ? far : -1;
    }

    /// The operators between the operands that start at `i`, up to the end of the line; null when something
    /// other than an operand and `+ - * / %` comes first. `restEnd` is one past the last operand.
    private static List<string>? Operators(List<T> toks, int i, out int restEnd, out bool startsWithString)
    {
        restEnd = i;
        startsWithString = i < toks.Count && toks[i].Kind == K.String && Operand(toks, i) == i;
        var ops = new List<string>();
        int at = i;
        while (true)
        {
            int end = Operand(toks, at);
            if (end < 0) return null;
            restEnd = end + 1;
            if (restEnd < toks.Count && toks[restEnd].Kind == K.Punct && toks[restEnd].Text is "+" or "-" or "*" or "/" or "%")
            {
                ops.Add(toks[restEnd].Text);
                at = restEnd + 1;
                if (at >= toks.Count || toks[at].Kind == K.Newline) return null;
            }
            else return ops;
        }
    }

    private static readonly HashSet<string> Keywords = new(Lexing.Lexer.KeywordNames.Except(new[] { "true", "false", "Entity", "Index", "base" }), StringComparer.Ordinal);

    private static bool IsKeyword(string word) => Keywords.Contains(word);

    /// The last token of one operand of a `+` chain on one line — a value, a call, a member read, a bracketed
    /// group, a sign in front — or -1.
    private static int Operand(List<T> toks, int i)
    {
        if (i >= toks.Count) return -1;
        if (toks[i].Is("-") || toks[i].IsWord("not")) i++;
        if (i >= toks.Count) return -1;
        var t = toks[i];
        int end;
        if (t.Kind is K.String or K.Number or K.Sigil) end = i;
        else if (t.Kind == K.Word && !IsKeyword(t.Text))
        {
            end = i;
            // `toText<Json>(Saved)`: the format in angle brackets belongs to the call.
            if (i + 4 < toks.Count && toks[i + 1].Is("<") && toks[i + 2].Kind == K.Word && toks[i + 3].Is(">") && toks[i + 4].Is("("))
                end = i + 3;
        }
        else if (t.Is("*") && i + 1 < toks.Count && toks[i + 1].Kind == K.Word)
        {
            end = i + 1;
            while (end + 2 < toks.Count && toks[end + 1].Is(".") && toks[end + 2].Kind is K.Word or K.Sigil) end += 2;
        }
        else if (t.Is("(") || t.Is("["))
        {
            end = Matching(toks, i);
            if (end < 0 || SpansLines(toks, i, end)) return -1;
        }
        else return -1;

        while (end + 1 < toks.Count)
        {
            var n = toks[end + 1];
            if (n.Is(".") && end + 2 < toks.Count && toks[end + 2].Kind is K.Word or K.Sigil) end += 2;
            else if (n.Is(".") && end + 3 < toks.Count && toks[end + 2].Is("~") && toks[end + 3].Kind == K.Word) end += 3;
            else if (n.Is("(") || n.Is("["))
            {
                int close = Matching(toks, end + 1);
                if (close < 0 || SpansLines(toks, end + 1, close)) return -1;
                end = close;
            }
            else break;
        }
        return end;
    }

    private static bool SpansLines(List<T> toks, int a, int b)
    {
        for (int k = a; k <= b; k++) if (toks[k].Kind is K.Newline or K.Comment) return true;
        return false;
    }

    /// Before a `+` chain may stand only what binds more loosely than `+`: so the chain is a whole operand.
    private static bool ChainMayStart(List<T> toks, int i)
    {
        int p = PrevInExpression(toks, i);
        if (p < 0) return true;
        var t = toks[p];
        return t.Kind == K.Newline && StatementStarts(toks, i)
               || t.Kind == K.Punct && t.Text is "(" or "[" or "," or ":" or "=" or "+=" or "{" or "==" or "<=" or ">="
               || t.Kind == K.Word && t.Text is "return" or "and" or "or" or "if" or "while";
    }

    private static bool ChainMayEnd(List<T> toks, int i)
    {
        if (i >= toks.Count) return true;
        var t = toks[i];
        return t.Kind is K.Newline or K.Comment
               || t.Kind == K.Punct && t.Text is ")" or "]" or "," or "}" or "{" or "==" or "<=" or ">="
               || t.Kind == K.Word && t.Text is "and" or "or";
    }

    // ---- text helpers --------------------------------------------------------------------------------

    private static string Spaced(string s, T t, string word) =>
        (t.Start > 0 && s[t.Start - 1] != ' ' ? " " : "") + word + (t.End < s.Length && s[t.End] != ' ' ? " " : "");

    private static string IndentAt(string s, int offset)
    {
        int from = LineStart(s, offset), i = from;
        while (i < s.Length && s[i] is ' ' or '\t') i++;
        return s[from..i];
    }

    private static int LineStart(string s, int offset) => offset <= 0 ? 0 : s.LastIndexOf('\n', Math.Min(offset, s.Length) - 1) + 1;

    private static int LineEnd(string s, int offset)
    {
        int nl = s.IndexOf('\n', Math.Min(offset, s.Length));
        return nl < 0 ? s.Length : nl;
    }

    private static int LineOf(string s, int offset)
    {
        int line = 1;
        for (int i = 0; i < offset && i < s.Length; i++) if (s[i] == '\n') line++;
        return line;
    }

    private static int LineCount(string s) => s.Count(c => c == '\n') + 1;

    private static List<string> CamelWords(string name)
    {
        var words = new List<string>();
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            if ((char.IsUpper(c) || c == '_') && sb.Length > 0) { words.Add(sb.ToString()); sb.Clear(); }
            if (c != '_') sb.Append(c);
        }
        if (sb.Length > 0) words.Add(sb.ToString());
        return words;
    }

    private static string Upper(string w) => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..];
    private static string LowerFirst(string w) => w.Length == 0 ? w : char.ToLowerInvariant(w[0]) + w[1..];
}
