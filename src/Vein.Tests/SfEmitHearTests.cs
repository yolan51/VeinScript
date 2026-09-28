using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// samples/sf_emit_hear.vein: an SF's emits are ordinary queued events that any shard can hear, and an SF
// cannot hold a `hear` of its own.
public class SfEmitHearTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }

    [Fact]
    public void Events_an_SF_emits_are_heard_by_other_shards_in_queue_order()
    {
        string path = Path.Combine(Root(), "samples", "sf_emit_hear.vein");
        var r = new VeinCompilerService().Compile(new CompileRequest("sf_emit_hear.vein", File.ReadAllText(path), SourcePath: path));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics));

        var output = new StringWriter();
        new Interp().Run(r.Modules[0], new StringReader(""), output);
        var lines = output.ToString().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        Assert.Equal(new[]
        {
            "1. before the SF", "2. after the SF",
            "Log heard @Hit: slime takes 5", "Log heard @Hit: goblin takes 3",
            "Log heard @Hit: goblin takes 6", "Log heard @Healed: goblin +2",
        }, lines);
    }

    [Fact]
    public void A_hear_inside_an_SF_is_one_clear_error()
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein",
            "bundle T by you {\n event @Ping { n: int }\n SF listen() {\n  hear @Ping as p { }\n }\n shard S { }\n}"));

        var d = Assert.Single(r.Diagnostics);
        Assert.Equal("VS0113", d.Code);
        Assert.Contains("put the `hear` in a shard", d.Message);
    }
}
