using Vein.Compiler.Project;
using Xunit;

namespace Vein.Tests;

// Creating, renaming and deleting from the Project Explorer.
//
// These are the rules behind a right-click menu, and they live in the compiler so they can be tested
// without a window. They are deliberately STRICTER THAN THE FILESYSTEM: Windows will accept a file
// called `aux`, or one ending in a space, and both are then hard to open, rename or delete by ordinary
// means. An IDE that lets you make one has not been permissive, it has set a trap.
public class ProjectEditsTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vein-edits-" + Guid.NewGuid().ToString("N")[..8]);

    public ProjectEditsTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(Path.Combine(_root, "shards"));
        File.WriteAllText(Path.Combine(_root, "Combat.vein"), "bundle Combat by you {}");
        File.WriteAllText(Path.Combine(_root, "shards", "Boot.vein"), "shard Boot { }");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir we made */ }
    }

    private string P(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

    // ---- names -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a<b")]
    [InlineData("name.")]
    public void A_name_that_would_make_an_awkward_file_is_refused(string name) =>
        Assert.NotNull(ProjectEdits.NameProblem(name, EntryKind.File));

    [Fact]
    public void Surrounding_spaces_are_trimmed_rather_than_refused()
    {
        // Ordinary tolerance of how people type into a box. A name is judged once the padding is off,
        // which is different from a trailing DOT — that one the filesystem strips behind your back,
        // leaving a file whose name is not the one you asked for.
        Assert.Null(ProjectEdits.NameProblem("  Boot.vein  ", EntryKind.File));
        Assert.NotNull(ProjectEdits.NameProblem("Boot.", EntryKind.File));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con.vein")]
    [InlineData("NUL")]
    [InlineData("COM1.vein")]
    [InlineData("lpt9")]
    public void A_windows_device_name_is_refused(string name)
    {
        // `CON.vein` is not creatable and the error the OS gives for trying is not one anybody can act
        // on, so the refusal has to happen here where it can say why.
        Assert.NotNull(ProjectEdits.NameProblem(name, EntryKind.File));
    }

    [Theory]
    [InlineData("Boot.vein")]
    [InlineData("Boot")]
    [InlineData("my-file.vein")]
    [InlineData("my_file.md")]
    [InlineData("Panel2.vein")]
    public void An_ordinary_name_is_allowed(string name) =>
        Assert.Null(ProjectEdits.NameProblem(name, EntryKind.File));

    [Theory]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData(".git")]
    public void A_build_folder_name_is_refused(string name) =>
        Assert.NotNull(ProjectEdits.NameProblem(name, EntryKind.Folder));

    // ---- the .vein default -------------------------------------------------------------------------

    [Fact]
    public void A_typed_name_with_no_extension_becomes_a_vein_file()
    {
        // "New VeinScript File" then "Boot" is what people type, and `Boot` with no extension is not a
        // file this IDE can do anything with.
        Assert.Equal("Boot.vein", ProjectEdits.FileName("Boot"));
        Assert.Equal("Boot.vein", ProjectEdits.FileName("  Boot  "));
    }

    [Fact]
    public void An_extension_that_was_typed_is_kept()
    {
        // Somebody asking for notes.md means it.
        Assert.Equal("notes.md", ProjectEdits.FileName("notes.md"));
        Assert.Equal("Boot.vein", ProjectEdits.FileName("Boot.vein"));
    }

    // ---- creating ----------------------------------------------------------------------------------

    [Fact]
    public void A_new_file_beside_an_existing_one_is_allowed() =>
        Assert.Null(ProjectEdits.CreateProblem(_root, P("shards"), "Flee", EntryKind.File));

    [Fact]
    public void Creating_over_something_that_exists_is_refused()
    {
        Assert.NotNull(ProjectEdits.CreateProblem(_root, _root, "Combat.vein", EntryKind.File));
        Assert.NotNull(ProjectEdits.CreateProblem(_root, _root, "Combat", EntryKind.File));   // .vein added
        Assert.NotNull(ProjectEdits.CreateProblem(_root, _root, "shards", EntryKind.Folder));
    }

    [Fact]
    public void Creating_outside_the_project_is_refused()
    {
        string outside = Path.GetTempPath();
        Assert.NotNull(ProjectEdits.CreateProblem(_root, outside, "Escape", EntryKind.File));
    }

    [Fact]
    public void With_no_project_open_nothing_can_be_created() =>
        Assert.NotNull(ProjectEdits.CreateProblem(null, null, "Boot", EntryKind.File));

    // ---- renaming ----------------------------------------------------------------------------------

    [Fact]
    public void Renaming_a_file_to_a_free_name_is_allowed() =>
        Assert.Null(ProjectEdits.RenameProblem(_root, P("Combat.vein"), "Battle.vein"));

    [Fact]
    public void Renaming_onto_an_existing_name_is_refused() =>
        Assert.NotNull(ProjectEdits.RenameProblem(_root, P("shards", "Boot.vein"), "Boot.vein"));

    [Fact]
    public void A_case_only_rename_is_allowed()
    {
        // On Windows `Boot.vein` and `boot.vein` are the same file, so a plain "does it exist" check
        // refuses the one rename that is purely a case fix.
        Assert.Null(ProjectEdits.RenameProblem(_root, P("Combat.vein"), "combat.vein"));
    }

    [Fact]
    public void Renaming_something_that_is_gone_is_refused() =>
        Assert.NotNull(ProjectEdits.RenameProblem(_root, P("NotThere.vein"), "Other.vein"));

    [Fact]
    public void Renaming_a_folder_is_allowed() =>
        Assert.Null(ProjectEdits.RenameProblem(_root, P("shards"), "handlers"));

    [Fact]
    public void A_rename_adds_no_extension()
    {
        // Renaming Boot.vein to Boot is something somebody may mean, and quietly putting `.vein` back
        // would be the IDE overruling a deliberate act.
        Assert.Null(ProjectEdits.RenameProblem(_root, P("Combat.vein"), "Combat"));
    }

    // ---- deleting ----------------------------------------------------------------------------------

    [Fact]
    public void Deleting_a_project_file_is_allowed() =>
        Assert.Null(ProjectEdits.DeleteProblem(_root, P("Combat.vein")));

    [Fact]
    public void Deleting_a_project_folder_is_allowed() =>
        Assert.Null(ProjectEdits.DeleteProblem(_root, P("shards")));

    [Fact]
    public void The_project_folder_itself_cannot_be_deleted()
    {
        // It would take the project and the window with it.
        Assert.NotNull(ProjectEdits.DeleteProblem(_root, _root));
        Assert.NotNull(ProjectEdits.DeleteProblem(_root, _root + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Deleting_outside_the_project_is_refused()
    {
        // The stdlib branch is shown in the tree so it can be READ. On an installed copy it shares a
        // folder with the application, which makes this the difference between a tidy-up and a
        // broken install.
        Assert.NotNull(ProjectEdits.DeleteProblem(_root, Path.Combine(Path.GetTempPath(), "elsewhere.vein")));
        Assert.NotNull(ProjectEdits.DeleteProblem(_root, P("..", "escape.vein")));
    }

    [Fact]
    public void Deleting_inside_build_output_is_refused()
    {
        Directory.CreateDirectory(P("bin"));
        File.WriteAllText(P("bin", "x.vein"), "x");
        Assert.NotNull(ProjectEdits.DeleteProblem(_root, P("bin", "x.vein")));
    }

    [Fact]
    public void The_file_count_is_what_the_prompt_needs()
    {
        // "Delete this folder?" gets clicked past. "Delete shards/ and the 12 files in it?" gets read.
        Assert.Equal(1, ProjectEdits.FileCount(P("Combat.vein")));
        Assert.Equal(1, ProjectEdits.FileCount(P("shards")));
        Assert.Equal(0, ProjectEdits.FileCount(P("NotThere.vein")));
    }
}
