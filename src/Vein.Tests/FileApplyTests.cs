using Vein.Cloud;
using Xunit;

namespace Vein.Tests;

// Turning a `file=` block from a remote reply into a write.
//
// THE PATH IS THE PART THAT IS NOT TRUSTED. The text of a suggestion is read by a person before it
// lands and shown as a diff; the path would otherwise go straight to a file API that is perfectly
// happy with `..\..\Windows\System32`. So these are mostly refusal tests, and the refusals are the
// feature.
public class FileApplyTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vein-apply-" + Guid.NewGuid().ToString("N")[..8]);

    public FileApplyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir we made */ }
    }

    private string Write(string rel, string text)
    {
        string full = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    // ---- refusals ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("../escape.vein")]
    [InlineData("../../escape.vein")]
    [InlineData("shards/../../escape.vein")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/System32/drivers/etc/hosts")]
    [InlineData(@"\\server\share\x.vein")]
    public void A_path_that_leaves_the_project_is_refused(string path)
    {
        var plan = FileApply.Resolve(_root, path, "bundle X by you {}");

        Assert.Equal(ApplyKind.Refused, plan.Kind);
        Assert.False(plan.CanApply);
        Assert.NotNull(plan.Reason);
    }

    [Theory]
    [InlineData("bin/x.vein")]
    [InlineData("obj/Debug/x.vein")]
    [InlineData(".git/config.txt")]
    public void A_path_inside_build_output_is_refused(string path) =>
        Assert.Equal(ApplyKind.Refused, FileApply.Resolve(_root, path, "x").Kind);

    [Theory]
    [InlineData("run.exe")]
    [InlineData("project.csproj")]
    [InlineData("script.ps1")]
    [InlineData("noextension")]
    public void Only_source_and_prose_can_be_written(string path)
    {
        // An assistant that can write a .csproj or a .ps1 is a different and much larger trust
        // decision than one that writes the files this IDE edits.
        var plan = FileApply.Resolve(_root, path, "x");
        Assert.Equal(ApplyKind.Refused, plan.Kind);
    }

    [Fact]
    public void With_no_project_open_nothing_can_be_written()
    {
        // "Relative to the project" has no meaning without one, and picking a base directory to guess
        // against is exactly how a write lands somewhere surprising.
        Assert.Equal(ApplyKind.Refused, FileApply.Resolve(null, "a.vein", "x").Kind);
        Assert.Equal(ApplyKind.Refused, FileApply.Resolve("", "a.vein", "x").Kind);
    }

    [Fact]
    public void An_empty_path_is_refused_rather_than_treated_as_the_root() =>
        Assert.Equal(ApplyKind.Refused, FileApply.Resolve(_root, "", "x").Kind);

    // ---- what is allowed --------------------------------------------------------------------------

    [Theory]
    [InlineData("Boot.vein")]
    [InlineData("shards/Boot.vein")]
    [InlineData("publicators/ui/Panels.vein")]
    [InlineData("README.md")]
    [InlineData("notes.txt")]
    public void A_relative_source_path_inside_the_project_is_a_create(string path)
    {
        var plan = FileApply.Resolve(_root, path, "bundle X by you {}");

        Assert.Equal(ApplyKind.Create, plan.Kind);
        Assert.True(plan.CanApply);
        Assert.Null(plan.Reason);
        Assert.StartsWith(Path.GetFullPath(_root), plan.FullPath);
    }

    [Fact]
    public void A_backslash_path_is_normalised_rather_than_refused()
    {
        // The service has no idea which platform it is talking to. `shards\Boot.vein` means the file
        // everyone can see it means.
        var plan = FileApply.Resolve(_root, @"shards\Boot.vein", "x");

        Assert.Equal(ApplyKind.Create, plan.Kind);
        Assert.Equal("shards/Boot.vein", plan.RelativePath);
    }

    [Fact]
    public void An_existing_file_with_different_content_is_a_replace()
    {
        Write("Boot.vein", "bundle Old by you {}\n");
        var plan = FileApply.Resolve(_root, "Boot.vein", "bundle New by you {}\n");

        Assert.Equal(ApplyKind.Replace, plan.Kind);
        Assert.Equal("bundle Old by you {}\n", plan.OldText);
        Assert.True(plan.CanApply);
    }

    [Fact]
    public void An_identical_file_is_unchanged_and_offers_nothing()
    {
        Write("Boot.vein", "bundle Same by you {}\n");
        var plan = FileApply.Resolve(_root, "Boot.vein", "bundle Same by you {}\n");

        Assert.Equal(ApplyKind.Unchanged, plan.Kind);
        Assert.False(plan.CanApply);
        Assert.Empty(plan.Diff);
    }

    [Fact]
    public void A_file_differing_only_in_line_endings_is_unchanged()
    {
        // Replies come back with Unix endings and Windows files have CRLF, so without this every
        // suggestion would present as a whole-file rewrite on the platform this ships to most.
        Write("Boot.vein", "bundle A by you {\r\n}\r\n");
        Assert.Equal(ApplyKind.Unchanged, FileApply.Resolve(_root, "Boot.vein", "bundle A by you {\n}\n").Kind);
    }

    // ---- diff -------------------------------------------------------------------------------------

    [Fact]
    public void The_diff_marks_what_changes_and_keeps_what_does_not()
    {
        var diff = FileApply.Diff("one\ntwo\nthree\n", "one\ntwo point five\nthree\n");

        Assert.Contains(diff, d => d.Kind == '-' && d.Text == "two");
        Assert.Contains(diff, d => d.Kind == '+' && d.Text == "two point five");
        Assert.Contains(diff, d => d.Kind == ' ' && d.Text == "one");
        Assert.Contains(diff, d => d.Kind == ' ' && d.Text == "three");
    }

    [Fact]
    public void Creating_a_file_shows_every_line_as_added()
    {
        var plan = FileApply.Resolve(_root, "New.vein", "a\nb\n");
        Assert.All(plan.Diff.Where(d => d.Text.Length > 0), d => Assert.Equal('+', d.Kind));
    }

    [Fact]
    public void The_counts_describe_the_diff()
    {
        Write("C.vein", "keep\ndrop\n");
        var plan = FileApply.Resolve(_root, "C.vein", "keep\nadd one\nadd two\n");
        var (added, removed) = plan.Counts;

        Assert.Equal(2, added);
        Assert.Equal(1, removed);
    }

    // ---- committing -------------------------------------------------------------------------------

    [Fact]
    public void Commit_writes_the_file_and_creates_its_folder()
    {
        var plan = FileApply.Resolve(_root, "shards/Deep/Boot.vein", "bundle X by you {}\n");
        FileApply.Commit(plan);

        Assert.True(File.Exists(plan.FullPath));
        Assert.Equal("bundle X by you {}\n", File.ReadAllText(plan.FullPath));
    }

    [Fact]
    public void Commit_refuses_a_plan_it_did_not_approve()
    {
        // An ApplyPlan is a value that could have been built anywhere. The cost of trusting one that
        // was not approved is somebody's source file, so the check is repeated at the write.
        var refused = FileApply.Resolve(_root, "../escape.vein", "x");
        Assert.Throws<InvalidOperationException>(() => FileApply.Commit(refused));
    }
}
