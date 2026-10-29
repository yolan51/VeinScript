using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vein.Compiler.Ir;

namespace Vein.Cloud;

/// The sibling tools an editor's DNA and Chain tabs drive — `veindna` (VeinDNA) and VeinChain's node and
/// wallet — found and run the same way from every editor (the Workbench's tabs, VeinGodot's). EXTERNAL ON
/// PURPOSE: each lives in its own repository and is run as a process or asked over HTTP, so no editor takes
/// a build dependency on it (two compilers in one process for VeinDNA; key handling for VeinChain).
///
/// Found by walking up from `seeds` — the Workbench's build folder, a Godot project's folder — to a folder
/// that has the repository beside it with its tool built: Desktop\VeinsScriptFirst\…\bin and Desktop\VeinGodot
/// both find Desktop\VeinDNA. An environment variable names it directly (VEIN_DNA, VEIN_CHAIN, VEIN_CHAIN_NET,
/// VEIN_CHAIN_HTTP).
public static class VeinTools
{
    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    /// The first `<ancestor>/<repo>/<relative>` that exists, walking up from each seed.
    public static string? Beside(string repo, string relative, IEnumerable<string?> seeds)
    {
        foreach (string? seed in seeds)
        {
            if (string.IsNullOrEmpty(seed)) continue;
            DirectoryInfo? d;
            try { d = new DirectoryInfo(Path.GetFullPath(seed)); } catch { continue; }
            for (; d is not null; d = d.Parent)
            {
                string candidate = Path.Combine(d.FullName, repo, relative);
                if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    // ---- VeinDNA ----------------------------------------------------------------------------------------

    /// `veindna`: VEIN_DNA, else the VeinDNA repository's build beside a seed (Release first).
    public static string? DnaTool(params string?[] seeds)
    {
        if (Env("VEIN_DNA") is { } env && File.Exists(env)) return env;
        var from = seeds.Append(AppContext.BaseDirectory).ToList();
        foreach (string config in new[] { "Release", "Debug" })
            if (Beside("VeinDNA", Path.Combine("src", "Vein.Dna.Cli", "bin", config, "net8.0", Exe("veindna")), from) is { } tool) return tool;
        return null;
    }

    /// `veindna relatives <file> <inputs> --json --top 20`: the bundle's closest relatives, as JSON, or why not.
    /// `inputs` is `shardstore:` (the live catalogue), folders, or both.
    public static (string? Json, string Error) DnaRelatives(string tool, string file, IEnumerable<string> inputs)
    {
        var (code, output, error) = Run(tool, new[] { "relatives", file }.Concat(inputs).Concat(new[] { "--json", "--top", "20" }), 180_000);
        if (code == -2) return (null, "took longer than 3 minutes");
        return code == 0 ? (output, "") : (null, error.Trim().Split('\n').LastOrDefault() is { Length: > 0 } last ? last : $"exit {code}");
    }

    // ---- VeinChain ---------------------------------------------------------------------------------------

    /// Where a chain is: the repository, the network folder (keys/ and the nodes' data), the wallet tool, and the
    /// node's HTTP gateway. Repo and net are null when not found.
    public sealed record Chain(string? Repo, string? Net, string? Wallet, string Http)
    {
        /// Why this chain cannot be used, or null when it can.
        public string? Missing => Repo is null
            ? "VeinChain not found — put it beside this repository (../VeinChain), or set VEIN_CHAIN."
            : Net is null ? $"no network yet — run {Path.Combine(Repo, "demos", "four-nodes", "start.ps1")} (it makes one), or set VEIN_CHAIN_NET."
            : Wallet is null ? "veinchain-wallet not found — dotnet build the VeinChain repository." : null;
    }

    public static Chain LocateChain(params string?[] seeds)
    {
        string? repo = Env("VEIN_CHAIN") ?? Beside("VeinChain", "", seeds.Append(AppContext.BaseDirectory));
        repo = repo?.TrimEnd('\\', '/');
        string? net = Env("VEIN_CHAIN_NET");
        string? current = repo is null ? null : Path.Combine(repo, "demos", "four-nodes", "data", "current.txt");
        if (net is null && current is not null && File.Exists(current) && File.ReadAllText(current).Trim() is { Length: > 0 } cur && Directory.Exists(cur))
            net = cur;
        string? wallet = null;
        if (repo is not null)
            foreach (string config in new[] { "Release", "Debug" })
            {
                string w = Path.Combine(repo, "src", "VeinChain.Wallet", "bin", config, "net8.0", Exe("veinchain-wallet"));
                if (File.Exists(w)) { wallet = w; break; }
            }
        return new Chain(repo, net, wallet, (Env("VEIN_CHAIN_HTTP") ?? "http://localhost:8545").TrimEnd('/'));
    }

    /// The accounts a network holds keys for (keys/<name>.key), by name.
    public static IReadOnlyList<string> ChainAccounts(string net)
    {
        string keys = Path.Combine(net, "keys");
        return Directory.Exists(keys)
            ? Directory.GetFiles(keys, "*.key").Select(f => Path.GetFileNameWithoutExtension(f)).OrderBy(n => n, StringComparer.Ordinal).ToList()
            : Array.Empty<string>();
    }

    /// An account's key pair as its key file holds it, or null.
    public static string? ChainKeys(string net, string? who)
    {
        if (who is null) return null;
        string file = Path.Combine(net, "keys", who + ".key");
        try { return File.Exists(file) ? File.ReadAllText(file).Trim() : null; } catch { return null; }
    }

    /// An account's public key, or null.
    public static string? ChainKey(string net, string? who) => ChainKeys(net, who) is { } keys ? VeinCrypto.PublicKey(keys) : null;

    /// One question to the node's gateway (POST /rpc) — `status`, `account`, `rewards`, `versions` — or null
    /// when no node answers. A client per question, held by nothing after it: these are a person's clicks, and
    /// a host that unloads this assembly (Godot's editor) must find no pool still alive.
    public static async Task<JsonElement?> ChainRpc(string http, object question)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.PostAsync(http + "/rpc", new StringContent(JsonSerializer.Serialize(question), Encoding.UTF8, "application/json")).ConfigureAwait(false);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;
        }
        catch { return null; }
    }

    /// `bundle X by author` in a file → "author.X", the package name it has on a chain.
    public static string? PackageOf(string? file)
    {
        if (file is null || !File.Exists(file)) return null;
        var m = Regex.Match(File.ReadAllText(file), @"(?m)^\s*bundle\s+([A-Za-z][A-Za-z0-9_]*)\s+by\s+([A-Za-z][A-Za-z0-9_]*)");
        return m.Success ? m.Groups[2].Value + "." + m.Groups[1].Value : null;
    }

    /// `veinchain-wallet <net> <args>`: the wallet holds the keys and signs, so an editor never builds a chain
    /// transaction itself. The exit code and everything it printed.
    public static (int Code, string Output) Wallet(Chain chain, params string[] args)
    {
        if (chain.Wallet is null || chain.Net is null) return (1, chain.Missing ?? "no wallet");
        var (code, output, error) = Run(chain.Wallet, new[] { chain.Net }.Concat(args), 120_000);
        return (code, (output + error).Trim());
    }

    /// The proof ShardStore checks when a chain account is linked to a ShardStore user: the key's owner signs
    /// a link naming the user and the time, so it cannot be made for a key you do not hold, nor replayed.
    public static async Task LinkAsync(CloudSession session, Chain chain, string who, CancellationToken ct = default)
    {
        if (chain.Net is null || ChainKey(chain.Net, who) is not { } key || ChainKeys(chain.Net, who) is not { } keys)
            throw new CloudException(0, $"no key file for {who}");
        long nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string signature = VeinCrypto.Sign($"vein-link-v1\nuser={session.UserId}\nnonce={nonce}\n", keys);
        await VeinCloudClient.PostAsync("chainLink", new { chain_key = key, chain_identity = LinkName(session), nonce, signature }, session.Token, ct).ConfigureAwait(false);
    }

    /// The name a ShardStore user has on a chain.
    public static string LinkName(CloudSession session) => "shardstore:" + (session.Handle.Length > 0 ? session.Handle : session.UserId);

    /// VEIN as people read it: units are millionths.
    public static string Vein(long units) => (units / 1_000_000.0).ToString("#,0.######", System.Globalization.CultureInfo.InvariantCulture) + " VEIN";

    /// A key short enough to read: its start and its end.
    public static string Short(string key) => key.Length > 24 ? key[..14] + "…" + key[^6..] : key;

    /// Run a tool and wait: exit code (-2 when it took longer than `timeoutMs`, -1 when it could not start),
    /// what it printed, and what it complained.
    private static (int Code, string Output, string Error) Run(string tool, IEnumerable<string> args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-2, "", ""); }
            return (p.ExitCode, stdout.Result, stderr.Result);
        }
        catch (Exception ex) { return (-1, "", ex.Message); }
    }
}
