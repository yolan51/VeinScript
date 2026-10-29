using Vein.Cloud;
using Vein.Compiler.Ir;
using Xunit;

namespace Vein.Tests;

// VeinTools finds and drives the sibling tools the DNA and Chain tabs use (veindna, VeinChain's node and
// wallet) the same way for every editor — the Workbench from its build folder, VeinGodot from a game's.
// These run on a made-up tree of folders in temp; no tool runs and no network is asked.
public class VeinToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vein-tools-" + Guid.NewGuid().ToString("N"));

    public VeinToolsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder left behind is harmless */ }
    }

    private string Make(string relative, string text = "")
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void A_repository_beside_any_ancestor_of_a_seed_is_found()
    {
        string tool = Make(Path.Combine("VeinDNA", "src", "Vein.Dna.Cli", "bin", "Release", "net8.0", "veindna.exe"));
        string game = Path.Combine(_root, "VeinGodot", "demo3d");
        string workbench = Path.Combine(_root, "VeinsScriptFirst", "src", "Vein.Workbench", "bin", "Debug", "net8.0");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(workbench);
        string relative = Path.Combine("src", "Vein.Dna.Cli", "bin", "Release", "net8.0", "veindna.exe");
        Assert.Equal(tool, VeinTools.Beside("VeinDNA", relative, new[] { game }));
        Assert.Equal(tool, VeinTools.Beside("VeinDNA", relative, new[] { null, workbench }));
        Assert.Null(VeinTools.Beside("VeinChain", "", new[] { game }));
    }

    [Fact]
    public void A_chain_is_its_repository_its_current_network_and_its_built_wallet()
    {
        if (Environment.GetEnvironmentVariable("VEIN_CHAIN") is { Length: > 0 }) return;   // a real chain is named: not this test's
        string net = Path.Combine(_root, "VeinChain", "demos", "four-nodes", "data", "net-1");
        Directory.CreateDirectory(net);
        Make(Path.Combine("VeinChain", "demos", "four-nodes", "data", "current.txt"), net);
        string game = Path.Combine(_root, "VeinGodot");
        Directory.CreateDirectory(game);

        var unbuilt = VeinTools.LocateChain(game);
        Assert.Equal(Path.Combine(_root, "VeinChain"), unbuilt.Repo);
        Assert.Equal(net, unbuilt.Net);
        Assert.Null(unbuilt.Wallet);
        Assert.Contains("dotnet build", unbuilt.Missing);

        Make(Path.Combine("VeinChain", "src", "VeinChain.Wallet", "bin", "Debug", "net8.0", OperatingSystem.IsWindows() ? "veinchain-wallet.exe" : "veinchain-wallet"));
        var built = VeinTools.LocateChain(game);
        Assert.NotNull(built.Wallet);
        Assert.Null(built.Missing);
    }

    [Fact]
    public void Accounts_are_the_network_s_key_files_and_a_key_is_their_public_half()
    {
        string net = Path.Combine(_root, "net");
        string keys = VeinCrypto.NewKeys();
        Make(Path.Combine("net", "keys", "bob.key"), keys);
        Make(Path.Combine("net", "keys", "alice.key"), VeinCrypto.NewKeys());
        Assert.Equal(new[] { "alice", "bob" }, VeinTools.ChainAccounts(net));
        Assert.Equal(VeinCrypto.PublicKey(keys), VeinTools.ChainKey(net, "bob"));
        Assert.Null(VeinTools.ChainKey(net, "carol"));
        Assert.Empty(VeinTools.ChainAccounts(Path.Combine(_root, "nowhere")));
    }

    [Fact]
    public void A_file_s_package_is_author_dot_bundle()
    {
        Assert.Equal("me.Shop", VeinTools.PackageOf(Make("shop.vein", "// a shop\nbundle Shop by me {\n}\n")));
        Assert.Null(VeinTools.PackageOf(Make("loose.vein", "shard S { }")));
        Assert.Null(VeinTools.PackageOf(null));
    }

    [Fact]
    public void A_tool_that_cannot_start_says_why()
    {
        var (json, error) = VeinTools.DnaRelatives(Path.Combine(_root, "missing.exe"), "x.vein", new[] { "shardstore:" });
        Assert.Null(json);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Units_read_as_vein_and_long_keys_are_shortened()
    {
        Assert.Equal("1.5 VEIN", VeinTools.Vein(1_500_000));
        Assert.Equal("abcdefghijklmn…uvwxyz", VeinTools.Short("abcdefghijklmnopqrstuvwxyz"));
    }
}
