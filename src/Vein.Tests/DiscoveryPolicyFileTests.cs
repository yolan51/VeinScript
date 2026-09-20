using Vein.Compiler.Project;
using Xunit;

namespace Vein.Tests;

// `vein.discovery` as a FILE — locating it, re-reading it, and surviving a bad one.
//
// `ProjectTests` covers `Parse` and `IsDiscoverable`, which is the language of the file. This covers
// the part an editor leans on: which file is found, and whether an edit to it is noticed. That second
// question stopped being academic when `$`/`#` completion began reading the policy on every keystroke —
// a policy you cannot change without restarting the editor is a policy nobody will edit.
public class DiscoveryPolicyFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-disc-" + Guid.NewGuid().ToString("N"));

    public DiscoveryPolicyFileTests() => Directory.CreateDirectory(Path.Combine(_dir, "project"));

    public void Dispose()
    {
        // Only ever the temp directory this test just created.
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string relative, string text)
    {
        string p = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text);
        return p;
    }

    [Fact]
    public void An_edit_to_the_policy_is_picked_up_without_a_restart()
    {
        // Cached on the path alone, this returned the stale copy forever: you would add an `expose`
        // line, type `$`, and see the old list until the process died.
        string dir = Path.Combine(_dir, "project");
        string file = Path.Combine(dir, "vein.discovery");

        File.WriteAllText(file, "silent all\n");
        Assert.False(DiscoveryPolicy.Load(dir).IsDiscoverable("kit", "Movement", "Drive"));

        File.WriteAllText(file, "silent all\nexpose kit.Movement\n");
        Assert.True(DiscoveryPolicy.Load(dir).IsDiscoverable("kit", "Movement", "Drive"));

        // And back again, so it is a re-read rather than a one-way latch.
        File.WriteAllText(file, "silent all\n");
        Assert.False(DiscoveryPolicy.Load(dir).IsDiscoverable("kit", "Movement", "Drive"));
    }

    [Fact]
    public void The_nearest_policy_wins()
    {
        // What `samples/kitdemo` relies on: `samples/vein.discovery` says `silent all`, and the project
        // one directory down overrides it. Without this a sample folder could not have its own.
        Write("vein.discovery", "silent all\n");
        Write("project/vein.discovery", "silent all\nexpose kit.Movement\n");

        Assert.True(DiscoveryPolicy.Load(Path.Combine(_dir, "project")).IsDiscoverable("kit", "Movement", "Drive"));
    }

    [Fact]
    public void A_parent_policy_applies_when_a_project_has_none_of_its_own()
    {
        Write("vein.discovery", "silent all\nexpose Vein.Console\n");

        var p = DiscoveryPolicy.Load(Path.Combine(_dir, "project"));

        Assert.True(p.IsDiscoverable("Vein", "Console", "Io"));
        Assert.False(p.IsDiscoverable("kit", "Movement", "Drive"));
    }

    [Fact]
    public void A_locked_policy_keeps_answering_from_the_last_good_read()
    {
        // Completion reads this on every keystroke, so it meets the file mid-save routinely. The stamp
        // is unchanged while an editor holds it open, so the cached answer stands — no exception, and
        // no flicker to a different list. A file whose timestamp HAS moved but cannot be read falls
        // back to permissive instead, because treating an unreadable policy as `silent all` would empty
        // every list in the editor and look exactly like the feature being broken.
        string dir = Path.Combine(_dir, "project");
        Write("project/vein.discovery", "silent all\n");
        Assert.False(DiscoveryPolicy.Load(dir).IsDiscoverable("kit", "Movement", "Drive"));

        using var held = File.Open(Path.Combine(dir, "vein.discovery"), FileMode.Open, FileAccess.Write, FileShare.None);
        Assert.False(DiscoveryPolicy.Load(dir).IsDiscoverable("kit", "Movement", "Drive"));
    }

    [Fact]
    public void No_file_anywhere_is_permissive()
    {
        Assert.True(DiscoveryPolicy.Load(Path.Combine(_dir, "project")).IsDiscoverable("anyone", "Any", "Pub"));
    }
}
