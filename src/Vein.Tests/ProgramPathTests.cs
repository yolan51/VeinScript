using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// Handover S — where a RELATIVE path in `@ReadFile` / `@WriteFile` points.
//
// It used to be `Environment.CurrentDirectory`, so the same program run from two folders wrote two
// files in two places, neither beside the `.vein` that asked. That looks defensible under `veinc run`
// and is indefensible everywhere else: a game opened from an editor writes next to the EDITOR, and one
// launched from a shortcut writes wherever the shortcut starts. Both are folders the author never chose,
// and the failure is silent — the write succeeds, `@FileWritten` fires, and the save is simply missing
// the next time the game looks for it.
//
// It was also inconsistent with the language's other kind of path: `$Image { source: "art/hero.png" }`
// means relative to the program in every tool that reads one, so an author writing "art/hero.png" and
// "saves/auto.save" in one file had written two identical-looking strings that meant different folders.
public class ProgramPathTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-progpath-" + Guid.NewGuid().ToString("N"));
    private readonly string _cwd = Directory.GetCurrentDirectory();

    public ProgramPathTests() => Directory.CreateDirectory(Path.Combine(_dir, "program"));

    public void Dispose()
    {
        // Restore the process directory before anything else — the tests below move it on purpose.
        Directory.SetCurrentDirectory(_cwd);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static IrModule Module(string src)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", src));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        return r.Modules[0];
    }

    private const string Writer =
        "bundle W by me {\n  mark #S\n" +
        "  shard Boot { run once { emit *Vein.Files.Io.@WriteFile " +
        "{ path: \"out/save.txt\", text: \"score=99\", append: false, tag: #S } } }\n}";

    [Fact]
    public void A_relative_write_lands_beside_the_program_not_the_shell()
    {
        string program = Path.Combine(_dir, "program");
        string elsewhere = Path.Combine(_dir, "elsewhere");
        Directory.CreateDirectory(elsewhere);

        // Stand somewhere else entirely — the case an editor or a shortcut creates.
        Directory.SetCurrentDirectory(elsewhere);

        new Interp { Ticks = 1, BaseDirectory = program }
            .Run(Module(Writer), new StringReader(""), new StringWriter());

        Assert.True(File.Exists(Path.Combine(program, "out", "save.txt")), "the save is not beside the program");
        Assert.False(Directory.Exists(Path.Combine(elsewhere, "out")), "the save leaked into the shell's folder");
    }

    [Fact]
    public void The_folder_the_shell_happens_to_be_in_does_not_change_the_answer()
    {
        // The whole point: two runs from two directories, one file in one place.
        string program = Path.Combine(_dir, "program");
        foreach (var from in new[] { _dir, program })
        {
            Directory.SetCurrentDirectory(from);
            new Interp { Ticks = 1, BaseDirectory = program }
                .Run(Module(Writer), new StringReader(""), new StringWriter());
        }

        Assert.Single(Directory.GetFiles(Path.Combine(program, "out")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "out")));
    }

    [Fact]
    public void An_absolute_path_is_left_exactly_as_written()
    {
        // A program that computed a full path has already said where it means, and anchoring it again
        // would produce something like C:\program\C:\real\file.txt.
        string target = Path.Combine(_dir, "absolute.txt").Replace("\\", "\\\\");
        new Interp { Ticks = 1, BaseDirectory = Path.Combine(_dir, "program") }.Run(Module(
            "bundle W by me {\n  mark #S\n" +
            "  shard Boot { run once { emit *Vein.Files.Io.@WriteFile " +
            $"{{ path: \"{target}\", text: \"x\", append: false, tag: #S }} }} }}\n}}"),
            new StringReader(""), new StringWriter());

        Assert.True(File.Exists(Path.Combine(_dir, "absolute.txt")));
    }

    [Fact]
    public void With_no_base_set_the_behaviour_is_what_it_always_was()
    {
        // Purely additive for an embedded caller that has not opted in: unset means the process
        // directory, exactly as before.
        Directory.SetCurrentDirectory(_dir);

        new Interp { Ticks = 1 }.Run(Module(Writer), new StringReader(""), new StringWriter());

        Assert.True(File.Exists(Path.Combine(_dir, "out", "save.txt")));
    }

    [Fact]
    public void A_relative_read_finds_what_a_relative_write_left()
    {
        // The pair has to agree, or a program can write a save it cannot load.
        string program = Path.Combine(_dir, "program");
        Directory.SetCurrentDirectory(_dir);

        var sw = new StringWriter();
        new Interp { Ticks = 2, BaseDirectory = program }.Run(Module(
            "bundle RW by me {\n  mark #S\n" +
            "  shard Boot { run once { emit *Vein.Files.Io.@WriteFile " +
            "{ path: \"data/x.txt\", text: \"round-trips\", append: false, tag: #S } } }\n" +
            "  shard Back { hear *Vein.Files.Io.@FileWritten as w { emit *Vein.Files.Io.@ReadFile " +
            "{ path: \"data/x.txt\", tag: #S } } }\n" +
            "  shard Show { hear *Vein.Files.Io.@FileLoaded as l { emit *Vein.Console.Io.@Print { text: l.text } } }\n}"),
            new StringReader(""), sw);

        Assert.Contains("round-trips", sw.ToString());
    }
}
