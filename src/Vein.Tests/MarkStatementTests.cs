using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// `mark e #A #B` marks e with EVERY mark named, as a builder's `mark #A #B` does — and `unmark` removes every one.
// It used to read the first and silently drop the rest: samples/desktop/todo's `mark t #Shown #Row` drew its task
// rows down instead of across, and `mark g #Shown #Hidden` left every group showing.
public class MarkStatementTests
{
    private static string Run(string body)
    {
        string source = "bundle M by me {\n  shape $S { n: int = 0 }\n  mark #A\n  mark #B\n  mark #C\n" +
                        "  shard X {\n    run once {\n      let e = spawn()\n      attach $S to e { }\n" + body + "\n    }\n" +
                        "    hear *Vein.Time.Clock.@Ticked as t {\n      if t.frame == 2 {\n" +
                        "        target $S as s { emit *Vein.Console.Io.@Print { text: \"A=\" + string(s carries #A) + \" B=\" + string(s carries #B) + \" C=\" + string(s carries #C) } }\n" +
                        "      }\n    }\n  }\n}";
        var r = new VeinCompilerService().Compile(new CompileRequest("m.vein", source));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));
        var sw = new StringWriter();
        new Interp { Ticks = 3 }.Run(r.Entry!, new StringReader(""), sw);
        return sw.ToString();
    }

    [Fact]
    public void Mark_with_several_marks_applies_every_one() =>
        Assert.Contains("A=true B=true C=true", Run("      mark e #A #B #C"));

    [Fact]
    public void Unmark_with_several_marks_removes_every_one() =>
        Assert.Contains("A=false B=false C=true", Run("      mark e #A #B #C\n      unmark e #A #B"));
}
