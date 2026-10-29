using Vein.Compiler.Service;
using Vein.Hosting;
using Xunit;

namespace Vein.Tests;

// stdlib/Editing.vein: a level kept in a file beside the program (Level ▸ LevelFile, copied with `from`), and
// what an editor's viewport says when it moves one of its things (Place ▸ @Placed). The host only reports;
// the copied shard moves the thing and writes the file. Driven here as a host drives it (VeinGame.Emit), in a
// temp folder — nothing walks up from there to this repo's stdlib, so the embedded copy is what compiles.
[Collection(ConsoleRuntime.Name)]
public class EditingLibraryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-editing-" + Guid.NewGuid().ToString("N"));

    public EditingLibraryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp folder left behind is harmless */ }
    }

    private const string Level = """
        [{"text":"pillar 1","x":-9,"y":0,"z":-9,"source":"pillar.tscn"},
         {"text":"pillar 2","x":9,"y":0,"z":-9,"source":"pillar.tscn"}]
        """;

    private static string Program(string use = "") => $$"""
        bundle Arena by me {
            need "Vein.Editing"
            need "Vein.Transform"
            need "Vein.Graphics"
            need "Vein.Core"
            shard Level from *Vein.Editing.Level.!LevelFile { tune file = "arena.level.json"{{use}} }
            shard Hero { run once {
                let hero = spawn()
                attach $Position to hero { x: 0.0, y: 0.0, z: 0.0 }
                attach $Model to hero { source: "hero.tscn" }
            } }
        }
        """;

    private VeinGame Open(string use = "", string? filesRoot = null)
    {
        string path = Path.Combine(_dir, "arena.vein");
        File.WriteAllText(path, Program(use));
        File.WriteAllText(Path.Combine(_dir, "arena.level.json"), Level);
        var game = VeinGame.Open(path, VeinRuntime.Interpreter);
        if (filesRoot is not null) game.FilesRoot = filesRoot;
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
        return game;
    }

    private static List<VeinEntity> Placed(VeinGame game) => game.Entities.Where(e => e.Carries("Placed")).OrderBy(e => e.Id).ToList();

    [Fact]
    public void The_level_s_things_are_woven_at_boot_and_a_thing_spawned_in_code_is_not_one()
    {
        var game = Open();
        var placed = Placed(game);
        Assert.Equal(2, placed.Count);
        Assert.Equal(-9.0, placed[0].Number("Position", "x"));
        Assert.Equal("pillar 2", placed[1].Shapes["Name"]["text"]);
        Assert.Equal(3, game.Models.Count);   // the hero is drawn, and is not the level's
    }

    [Fact]
    public void A_move_from_the_editor_moves_the_thing_and_writes_the_file()
    {
        var game = Open();
        long first = Placed(game)[0].Id;
        Assert.True(game.Emit("Placed", new Dictionary<string, object?> { ["target"] = first, ["x"] = 4.5, ["y"] = 0.0, ["z"] = -2.0 }));
        Assert.Equal(4.5, Placed(game)[0].Number("Position", "x"));
        string saved = File.ReadAllText(Path.Combine(_dir, "arena.level.json"));
        Assert.Contains("\"x\":4.5", saved);
        Assert.Contains("\"z\":-2", saved);
        Assert.Contains("pillar 2", saved);
        Assert.DoesNotContain("hero", saved);   // only the level's things are its file
    }

    [Fact]
    public void A_level_that_may_not_move_ignores_the_editor()
    {
        var game = Open(use: "\n        tune movable = false");
        long first = Placed(game)[0].Id;
        game.Emit("Placed", new Dictionary<string, object?> { ["target"] = first, ["x"] = 4.5, ["y"] = 0.0, ["z"] = 0.0 });
        Assert.Equal(-9.0, Placed(game)[0].Number("Position", "x"));
        Assert.Equal(Level, File.ReadAllText(Path.Combine(_dir, "arena.level.json")));
    }

    [Fact]
    public void A_game_whose_files_land_elsewhere_still_reads_its_level_from_beside_its_rules()
    {
        string saves = Path.Combine(_dir, "saves");
        Directory.CreateDirectory(saves);
        var game = Open(filesRoot: saves);
        Assert.Equal(2, Placed(game).Count);
    }

    [Fact]
    public void A_loom_whose_shapes_share_a_field_name_is_an_error()
    {
        var result = new VeinCompilerService().Compile(new CompileRequest("t.vein", """
            bundle T by me {
                shape $Where { x: float, y: float }
                shape $Turn { x: float }
                loom<Json> Thing { $Where $Turn  mark #Thing }
                mark #Thing
            }
            """));
        Assert.Contains(result.Diagnostics, d => d.Code == "VS0389" && d.Message.Contains("`x`"));
    }
}
