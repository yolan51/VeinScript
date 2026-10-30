using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// `veinc uwu`: C# spellings and the long forms rewritten, the rest hinted (Uwu.cs).
//
// Most cases here are the ones that must NOT be rewritten. A rewrite that changes what a line means is the
// one thing this tool cannot be allowed to do, and each refusal below is a place where the obvious edit is
// wrong: `s = s + 1 + 2` is "s12", `xs + "a"` appends to a list, `"a" + n * 2` is not `"a\(n)" * 2`.
public class UwuTests
{
    private static string Bundle(string body) => "bundle T by you {\n" + body + "\n}\n";

    private static string Shard(string run, string vars = "") =>
        Bundle("    need \"Vein.Console\"\n    shard S {\n" + vars + "        run once {\n" + run + "\n        }\n    }");

    private static Uwu.Report Tidy(string source) => Uwu.Tidy(source);

    private static void Ok(string source)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", source));
        Assert.True(r.Diagnostics.All(d => d.Severity != Compiler.Diagnostics.Severity.Error),
            string.Join("\n", r.Diagnostics) + "\n---\n" + source);
    }

    // ---- the short forms -----------------------------------------------------------------------------

    [Fact]
    public void LongPrintBecomesDoPrintAndTheNeedIsAdded()
    {
        var r = Tidy(Bundle("    shard S {\n        run once {\n            emit *Vein.Console.Io.@Print { text: \"hi\" }\n        }\n    }"));
        Assert.Contains("do print(\"hi\")", r.Text);
        Assert.Contains("need \"Vein.Console\"", r.Text);
        Assert.DoesNotContain("@Print", r.Text);
        Ok(r.Text);
    }

    [Fact]
    public void APrintOverSeveralLinesIsOneCall()
    {
        var r = Tidy(Shard("            emit *Vein.Console.Io.@PrintError {\n                text: \"bad\"\n            }"));
        Assert.Contains("do printError(\"bad\")", r.Text);
        Ok(r.Text);
    }

    [Fact]
    public void APrintWithASecondFieldIsLeft()
    {
        string src = Shard("            emit *Vein.Console.Io.@Send { to: #Server, text: \"x\" }");
        Assert.Equal(src, Tidy(src).Text);
    }

    [Fact]
    public void ABundleWithItsOwnPrintKeepsTheLongFormAndIsTold()
    {
        // Its SF print IS the emit. Rewritten, the body would call itself.
        string src = Bundle("    SF print(msg: string) {\n        emit *Vein.Console.Io.@Print { text: msg }\n    }\n" +
                            "    shard S {\n        run once { do print(\"x\") }\n    }");
        var r = Tidy(src);
        Assert.Contains("emit *Vein.Console.Io.@Print { text: msg }", r.Text);
        Assert.Contains(r.Hints, h => h.Rule == "do print");
    }

    [Fact]
    public void InterpolationWhenAStringComesFirst()
    {
        var r = Tidy(Shard("            let hp = 3\n            do print(\"HP: \" + hp + \"/\" + (hp + 1))"));
        Assert.Contains("do print(\"HP: \\(hp)/\\(hp + 1)\")", r.Text);
        Ok(r.Text);
    }

    [Fact]
    public void OneFieldInFrontOfTheStringIsTakenAndABareNameIsNot()
    {
        // `xs + "a"` APPENDS when xs is a list; a field read is a value, so `\(…)` writes it as `+` would.
        string src = Shard("            let e = spawn()\n            let n = 2\n            target $Hp as h { do print(h.Hp.hp + \" left\") }\n            do print(n + \" left\")",
                           "");
        src = src.Replace("    shard S", "    shape $Hp { hp: int }\n    shard S");
        var r = Tidy(src);
        Assert.Contains("do print(\"\\(h.Hp.hp) left\")", r.Text);
        Assert.Contains("do print(n + \" left\")", r.Text);
    }

    [Theory]
    [InlineData("do print(\"a\" + n * 2)")]        // `*` binds first: the chain is not the whole operand
    [InlineData("do print(n * \"a\" + n)")]
    [InlineData("do print(\"a\" + n - 1)")]
    [InlineData("do print(\"a\" + \"b\")")]         // only text: a join kept on purpose
    [InlineData("do print(\"a\" +\n                \"b\" + n)")]    // over lines: long on purpose
    public void ChainsThatAreNotTheWholeExpressionAreLeft(string line)
    {
        string src = Shard("            let n = 2\n            " + line);
        Assert.Equal(src, Tidy(src).Text);
    }

    [Fact]
    public void CompoundAssignment()
    {
        var r = Tidy(Shard("            n = n + 1\n            n = n - 2 * 3\n            s = s + \"a\" + n", "        var n: int = 0\n        var s: string = \"\"\n"));
        Assert.Contains("n += 1", r.Text);
        Assert.Contains("n -= 2 * 3", r.Text);
        Assert.Contains("s += \"a\\(n)\"", r.Text);
        Ok(r.Text);
    }

    [Theory]
    [InlineData("n = n - 1 + 2")]       // (n - 1) + 2 is not n - (1 + 2)
    [InlineData("s = s + 1 + 2")]       // "s12", where s += 1 + 2 is "s3"
    [InlineData("n = n * 2 / 3")]       // integer division regroups
    [InlineData("n = m + n")]
    public void CompoundOnlyWhenNothingRegroups(string line)
    {
        string src = Shard("            " + line, "        var n: int = 0\n        var m: int = 0\n        var s: string = \"\"\n");
        string text = Tidy(src).Text;
        Assert.Contains(line, text);
        Assert.DoesNotContain("+=", text);
        Assert.DoesNotContain("-=", text);
        Assert.DoesNotContain("*=", text);
    }

    // ---- C# spellings --------------------------------------------------------------------------------

    [Fact]
    public void ACSharpishBlockBecomesVeinScript()
    {
        string src = Shard(
            "            int n = 3;\n" +
            "            bool ok = true;\n" +
            "            n++;\n" +
            "            if (n != 4 && !ok || !(n == 0)) {\n" +
            "                Console.WriteLine($\"n is {n}\");\n" +
            "            }\n" +
            "            else {\n" +
            "                do print(`odd: ${n}`)\n" +
            "            }\n" +
            "            let total = \"n: \"\n" +
            "                + n");
        var r = Tidy(src);
        Assert.Contains("var n: int = 3\n", r.Text);
        Assert.Contains("var ok: bool = true\n", r.Text);
        Assert.Contains("n += 1\n", r.Text);
        Assert.Contains("if (not (n == 4) and not ok or not (n == 0)) {", r.Text);
        Assert.Contains("do print(\"n is \\(n)\")", r.Text);
        Assert.Contains("} else {", r.Text);
        Assert.Contains("do print(\"odd: \\(n)\")", r.Text);
        Assert.Contains("let total = \"n: \" +\n                n", r.Text);
        Ok(r.Text);
    }

    [Fact]
    public void TheCompilersOwnFixesForArrowAndDo()
    {
        string src = Bundle("    need \"Vein.Console\"\n    fn twice(x: int) -> int { return x * 2 }\n    SF say(t: string) { do print(t) }\n" +
                            "    shard S {\n        run once {\n            say(\"x \\(twice(2))\")\n        }\n    }");
        var r = Tidy(src);
        Assert.Contains("fn twice(x: int) return int", r.Text);
        Assert.Contains("do say(", r.Text);
        Ok(r.Text);
    }

    [Fact]
    public void StringsCommentsAndShardRefsAreNotCode()
    {
        string src = Bundle("    need \"Vein.Console\"\n    shard Game {\n        run once {\n" +
                            "            // a != b; x++ && y\n" +
                            "            do print(\"a != b; && || x++\")\n" +
                            "            mute Game\n        }\n    }");
        Assert.Equal(src, Tidy(src).Text);
    }

    [Fact]
    public void AnElseAfterAMatchArmStaysOnItsLine()
    {
        string src = Bundle("    need \"Vein.Console\"\n    shape $M { kind: enum { A, B } }\n    shard S {\n        each tick {\n" +
                            "            target $M as m {\n                match m.M.kind {\n" +
                            "                    when A { do print(\"a\") }\n" +
                            "                    else   { do print(\"b\") }\n" +
                            "                }\n            }\n        }\n    }");
        Assert.Equal(src, Tidy(src).Text);
    }

    // ---- the check after each pass -------------------------------------------------------------------

    [Fact]
    public void APassThatWouldBreakTheFileIsSkipped()
    {
        // Two bundles in one file: the need lands after the LAST `need`, which is in the other bundle, so
        // `do print` in the first would not resolve. The pass is refused, the rest still runs.
        string src = "bundle A by you {\n    shard S {\n        run once { emit *Vein.Console.Io.@Print { text: \"a \" + 1 } }\n    }\n}\n" +
                     "bundle B by you {\n    need \"Vein.Math\"\n}\n";
        var r = Tidy(src);
        Assert.Contains(r.Skipped, s => s.StartsWith("do print", StringComparison.Ordinal));
        Assert.Contains("emit *Vein.Console.Io.@Print { text: \"a \\(1)\" }", r.Text);
    }

    [Fact]
    public void OffSwitch()
    {
        string src = "// uwu: off — kept long on purpose\n" + Shard("            emit *Vein.Console.Io.@Print { text: \"a\" + 1 }");
        var r = Tidy(src);
        Assert.Equal(src, r.Text);
        Assert.Single(r.Skipped);
    }

    [Fact]
    public void ASecondRunFindsNothing()
    {
        string src = Shard("            int n = 1;\n            n = n + 1\n            emit *Vein.Console.Io.@Print { text: \"n \" + n }");
        var once = Tidy(src);
        Assert.NotEmpty(once.Changes);
        var twice = Tidy(once.Text);
        Assert.Empty(twice.Changes);
        Assert.Equal(once.Text, twice.Text);
    }

    [Fact]
    public void WindowsLineEndingsAreKept()
    {
        string src = Shard("            emit *Vein.Console.Io.@Print { text: \"a\" }").Replace("\n", "\r\n");
        var r = Tidy(src);
        Assert.Contains("do print(\"a\")\r\n", r.Text);
        Assert.DoesNotContain("\r\r", r.Text);
        Assert.Equal(r.Text.Count(c => c == '\n'), r.Text.Count(c => c == '\r'));
    }

    [Fact]
    public void ChangesAreReportedAtTheLinesGiven()
    {
        // The `need` added near the top moves every line after it; the report still names the line as written.
        string src = Bundle("    shard S {\n        run once {\n            let n = 1\n            emit *Vein.Console.Io.@Print { text: \"n \" + n }\n        }\n    }");
        var r = Tidy(src);
        var print = Assert.Single(r.Changes, c => c.Before.StartsWith("emit", StringComparison.Ordinal));
        Assert.Equal(5, print.Line);
        Assert.Equal("do print, interpolation", print.Rule);
        Assert.Equal("do print(\"n \\(n)\")", print.After);
    }

    // ---- hints ---------------------------------------------------------------------------------------

    [Fact]
    public void ShardVarsThatAreThingsAreHinted()
    {
        string src = Bundle("    shard Game {\n        var heroHp: int = 30\n        var heroGold: int = 0\n        var slimeHp: int = 20\n        run once { }\n    }");
        var hints = Tidy(src).Hints.Where(h => h.Rule == "data first").Select(h => h.Text).ToList();
        Assert.Contains(hints, h => h.Contains("`shape $Hero { hp: int, gold: int }`"));
        Assert.Contains(hints, h => h.Contains("hero and slime both have `hp`"));
    }

    [Fact]
    public void ExclusionInsideTheBodyIsHinted()
    {
        string src = Bundle("    shape $Item { n: int }\n    mark #Sold\n    shard S {\n        each tick {\n" +
                            "            target $Item as it {\n                if not (it carries #Sold) { it.Item.n += 1 }\n            }\n        }\n    }");
        Assert.Contains(Tidy(src).Hints, h => h.Text.Contains("`target … not #Sold as it`"));
    }

    [Fact]
    public void AFoundFlagIsHinted()
    {
        string src = Bundle("    shape $Item { n: int }\n    shard S {\n        run once {\n            var found = false\n" +
                            "            target $Item as i { if i.Item.n == 3 { found = true } }\n        }\n    }");
        Assert.Contains(Tidy(src).Hints, h => h.Rule == "a question is an fn" && h.Text.Contains("`found`"));
    }

    [Fact]
    public void CSharpWordsWithNoRewriteAreHinted()
    {
        string src = Bundle("    class Hero {\n    }\n    shard S {\n        run once {\n            for (var i = 0; i < 3; i += 1) { }\n        }\n    }");
        var r = Tidy(src);
        Assert.Contains(r.Hints, h => h.Text.StartsWith("VeinScript has no classes", StringComparison.Ordinal));
        Assert.Contains(r.Hints, h => h.Text.StartsWith("No `for`", StringComparison.Ordinal));
        Assert.Contains("for (var i = 0; i < 3; i += 1)", r.Text);   // a `for` header's `;` is left for the person
    }
}
