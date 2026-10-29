using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Vein.Compiler.Ir;

namespace Vein.Workbench;

/// The Chain tab: Vein Coin for the code you write, on a VeinChain network.
///
///   your account   its balance, what it has earned for useful VeinScript (chain.Rewards), its packages with the
///                  DNA the workers agreed on (a COPY is marked), and what it has locked behind packages;
///   this bundle    its package (`bundle X by author` → author.X), its verified versions, and the actions —
///                  register it, publish a version to be verified, endorse a package, link your ShardStore account.
///
/// READS over the node's HTTP gateway (`--http`, POST /rpc), WRITES through `veinchain-wallet` — the wallet holds
/// the keys and signs, so this panel never builds a chain transaction itself and the Workbench takes no build
/// dependency on VeinChain (the way the DNA tab runs `veindna`).
///
/// Found through: VEIN_CHAIN (the VeinChain repository, else `../VeinChain` beside this one); VEIN_CHAIN_NET (a
/// network folder, else the demo's current one, demos/four-nodes/data/current.txt); VEIN_CHAIN_HTTP (else
/// http://localhost:8545, the demo's first node).
internal sealed class ChainPanel : UserControl
{
    private static readonly IBrush Muted = new SolidColorBrush(Color.FromRgb(0x9A, 0xA2, 0xB2));
    private static readonly IBrush Good = new SolidColorBrush(Color.FromRgb(0x82, 0xE0, 0xAA));
    private static readonly IBrush Bad = new SolidColorBrush(Color.FromRgb(0xF4, 0x70, 0x67));
    private static readonly FontFamily Mono = new("Cascadia Code,Consolas,monospace");

    private readonly ComboBox _account = new() { FontSize = 12, MinWidth = 140 };
    private readonly Button _refresh = new() { Content = "Refresh", Padding = new Thickness(10, 2), FontSize = 12 };
    private readonly TextBlock _status = new() { Foreground = Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _left = new() { Spacing = 6, Margin = new Thickness(10, 6) };
    private readonly StackPanel _right = new() { Spacing = 6, Margin = new Thickness(10, 6) };
    private readonly TextBox _log = new() { IsReadOnly = true, FontFamily = Mono, FontSize = 11, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90 };

    private string? _file;
    private string? _repo, _net, _wallet, _http;
    private int _request;

    /// The signed-in ShardStore (base44) account, for "Link".
    public global::Vein.Cloud.CloudSession? Session { get; set; }

    public ChainPanel()
    {
        _refresh.Click += (_, _) => _ = RefreshAsync();
        _account.SelectionChanged += (_, _) => _ = RefreshAsync();
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 5),
            Children = { new TextBlock { Text = "VeinChain · account", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold }, _account, _refresh, _status },
        };
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,4,*") };
        var left = new ScrollViewer { Content = _left };
        var right = new ScrollViewer { Content = _right };
        Grid.SetColumn(left, 0); Grid.SetColumn(right, 2);
        var split = new GridSplitter { Background = Brushes.Transparent };
        Grid.SetColumn(split, 1);
        columns.Children.Add(left); columns.Children.Add(split); columns.Children.Add(right);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Avalonia.Controls.Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(columns);
        Content = root;
    }

    /// Called when the tab is selected (and after a save): the open file, and the network as found now.
    public void Update(string? file)
    {
        _file = file;
        Locate();
        if (_net is null || _wallet is null)
        {
            _status.Text = _repo is null
                ? "VeinChain not found — put it beside this repository (../VeinChain), or set VEIN_CHAIN."
                : $"no network yet — run {Path.Combine(_repo, "demos", "four-nodes", "start.ps1")} (it makes one), or set VEIN_CHAIN_NET.";
            return;
        }
        var keys = Directory.Exists(Path.Combine(_net, "keys"))
            ? Directory.GetFiles(Path.Combine(_net, "keys"), "*.key").Select(Path.GetFileNameWithoutExtension).OrderBy(n => n, StringComparer.Ordinal).ToList()
            : new List<string?>();
        string? was = _account.SelectedItem as string;
        _account.ItemsSource = keys;
        _account.SelectedItem = keys.Contains(was) ? was : keys.FirstOrDefault(k => k is "alice") ?? keys.FirstOrDefault();
        _ = RefreshAsync();
    }

    // Finding the chain, asking its node and running its wallet are Vein.Cloud's VeinTools — the same for
    // VeinGodot's Chain tab. This panel only draws.
    private void Locate()
    {
        var chain = global::Vein.Cloud.VeinTools.LocateChain(AppContext.BaseDirectory);
        (_repo, _net, _wallet, _http) = (chain.Repo, chain.Net, chain.Wallet, chain.Http);
    }

    private global::Vein.Cloud.VeinTools.Chain Chain => new(_repo, _net, _wallet, _http ?? "http://localhost:8545");

    // ---- reading ----------------------------------------------------------------------------------------

    private Task<JsonElement?> Rpc(object question) => global::Vein.Cloud.VeinTools.ChainRpc(Chain.Http, question);

    private string? KeyOf(string? who) => _net is null ? null : global::Vein.Cloud.VeinTools.ChainKey(_net, who);

    /// `bundle X by author` in the open file → "author.X".
    private string? PackageOfFile() => global::Vein.Cloud.VeinTools.PackageOf(_file);

    private async Task RefreshAsync()
    {
        if (_net is null) return;
        int request = ++_request;
        string? who = _account.SelectedItem as string, key = KeyOf(who);
        var status = await Rpc(new { method = "status" });
        if (request != _request) return;
        _left.Children.Clear();
        _right.Children.Clear();
        if (status is null)
        {
            _status.Text = $"no node answers at {_http} — start the network (demos/four-nodes/start.ps1; its first node serves HTTP).";
            RenderActions(null, who);
            return;
        }
        _status.Text = $"{_http} · {Path.GetFileName(_net)} · height {(status.Value.TryGetProperty("height", out var h) ? h.ToString() : "?")}";

        var account = key is null ? null : await Rpc(new { method = "account", key });
        var rewards = await Rpc(new { method = "rewards" });
        if (request != _request) return;

        Head(_left, who is null ? "No account" : $"Account {who}");
        if (key is not null) Line(_left, Short(key), Muted, mono: true);
        if (account is { } a && a.TryGetProperty("balance", out var bal)) Line(_left, "balance  " + Vein(bal.GetInt64()), null, size: 16);
        if (rewards is { } r)
        {
            var earning = r.GetProperty("earnings").EnumerateArray().FirstOrDefault(e => e.GetProperty("author").GetString() == key);
            Line(_left, earning.ValueKind == JsonValueKind.Object
                ? $"earned for useful VeinScript  {Vein(earning.GetProperty("units").GetInt64())} over {earning.GetProperty("epochs").GetInt64()} epoch(s)"
                : "earned for useful VeinScript  nothing yet", Good);
            if (r.TryGetProperty("pool", out var pool) && pool.ValueKind == JsonValueKind.Object)
                Line(_left, $"author pool {Vein(pool.GetProperty("units").GetInt64())} waiting · {pool.GetProperty("epochs").GetInt64()} epoch(s) settled", Muted);

            Head(_left, "Your packages (DNA agreed by the workers)");
            var mine = r.GetProperty("packages").EnumerateArray().Where(d => d.GetProperty("owner").GetString() == key).ToList();
            if (mine.Count == 0) Line(_left, "none verified yet — publish one from the right", Muted);
            foreach (var d in mine)
            {
                string copy = d.GetProperty("copyOf").GetString()!;
                bool counts = copy.Length == 0 && d.GetProperty("tests").GetInt64() > 0 && d.GetProperty("statements").GetInt64() >= 10;
                Line(_left, $"{d.GetProperty("package").GetString()} {d.GetProperty("version").GetString()}   genome {d.GetProperty("genome").GetString()}", null, mono: true);
                Line(_left, $"   {d.GetProperty("statements").GetInt64()} statements · {d.GetProperty("tests").GetInt64()} test(s)" +
                            (d.GetProperty("needs").GetString() is { Length: > 0 } n ? " · needs " + n : "") +
                            (copy.Length > 0 ? $" · COPY of {copy}: earns nothing" : counts ? " · can earn" : " · cannot earn yet (needs a passing test and 10 statements)"),
                     copy.Length > 0 ? Bad : counts ? Good : Muted);
            }

            Head(_left, "Locked behind packages");
            var locked = r.GetProperty("endorsements").EnumerateArray().Where(e => e.GetProperty("endorser").GetString() == key).ToList();
            if (locked.Count == 0) Line(_left, "nothing — endorse a package you rely on", Muted);
            foreach (var e in locked) Line(_left, $"{Vein(e.GetProperty("units").GetInt64())} behind {e.GetProperty("package").GetString()} since #{e.GetProperty("since").GetInt64()}", null);
        }
        RenderActions(rewards, who);
    }

    private async void RenderActionsVersions(string package)
    {
        var v = await Rpc(new { method = "versions", package });
        if (v is null || !v.Value.TryGetProperty("versions", out var list)) return;
        foreach (var x in list.EnumerateArray())
            Line(_right, $"verified {x.GetProperty("version").GetString()} at #{x.GetProperty("height")}   {x.GetProperty("result").GetString()![..Math.Min(22, x.GetProperty("result").GetString()!.Length)]}…", Good, mono: true, at: 2);
    }

    private void RenderActions(JsonElement? rewards, string? who)
    {
        string? package = PackageOfFile();
        Head(_right, package is null ? "This bundle" : "This bundle · " + package);
        if (package is null) { Line(_right, "open a .vein file with `bundle Name by author`", Muted); }
        else
        {
            RenderActionsVersions(package);
            var row = new WrapPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Action("Register package", () => Wallet("register", who!, "package", package)));
            row.Children.Add(Action("Publish to VeinChain…", () => _ = PublishAsync(who!, package)));
            row.Children.Add(Action("Endorse…", () => _ = EndorseAsync(who!, package)));
            _right.Children.Add(row);
            Line(_right, "Publish uploads this file (and <name>.tests.vein beside it, if there is one) and pays the validators to verify it; " +
                         "its `need`s are pinned to their latest verified versions. A package earns when it is verified, has a passing test, " +
                         "and someone endorses it or a package someone endorses needs it.", Muted, wrap: true);
        }

        Head(_right, "ShardStore account");
        Line(_right, Session is null ? "not signed in (Cloud ▸ Sign In)" : $"signed in as {Session.Display}" + (Session.Handle.Length > 0 ? $" (@{Session.Handle})" : ""), Muted);
        _right.Children.Add(Action("Link to this chain account", () => _ = LinkAsync(who!)));
        Line(_right, "Registers `shardstore:<handle>` on the chain under this key — proof only this key's owner could give — and tells ShardStore the key, so the site shows your earnings.", Muted, wrap: true);

        Head(_right, "Wallet output");
        _right.Children.Add(_log);
    }

    // ---- writing (through veinchain-wallet) ----------------------------------------------------------------

    private async Task PublishAsync(string who, string package)
    {
        if (_file is null) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var version = await PromptDialog.ShowAsync(owner, "Publish to VeinChain", $"Version of {package} to verify (e.g. 1.0):", "1.0");
        if (version is not { Length: > 0 }) return;
        string tests = Path.Combine(Path.GetDirectoryName(_file)!, Path.GetFileNameWithoutExtension(_file) + ".tests.vein");
        var args = new List<string> { "publish", who, package, version.Trim(), _file };
        if (File.Exists(tests)) args.Add(tests);
        Wallet(args.ToArray());
    }

    private async Task EndorseAsync(string who, string package)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var target = await PromptDialog.ShowAsync(owner, "Endorse a package", "Package to lock VEIN behind:", package);
        if (target is not { Length: > 0 }) return;
        var amount = await PromptDialog.ShowAsync(owner, "Endorse a package", $"VEIN to lock behind {target} (counts after a whole epoch; withdraw any time):", "1");
        if (amount is not { Length: > 0 }) return;
        Wallet("endorse", who, target.Trim(), amount.Trim());
    }

    private async Task LinkAsync(string who)
    {
        if (Session is not { } session) { _log.Text = "Sign in to ShardStore first (Cloud ▸ Sign In)."; return; }
        string name = global::Vein.Cloud.VeinTools.LinkName(session);
        string? key = KeyOf(who);
        if (key is null) { _log.Text = $"No key file for {who}."; return; }
        await WalletAsync("register", who, "author", name);
        try
        {
            // The proof ShardStore checks (VeinTools.LinkAsync; VeinDNA base44/functions/chainLink.ts).
            await global::Vein.Cloud.VeinTools.LinkAsync(session, Chain, who);
            _log.Text += $"\nShardStore now knows {name} is {Short(key)}.";
        }
        catch (Exception ex)
        {
            _log.Text += $"\nShardStore did not take the link ({ex.Message}). Add the chainLink function to the base44 app — VeinDNA's base44/PASTE-INTO-BASE44.md, step 6.";
        }
    }

    private void Wallet(params string[] args) => _ = WalletAsync(args);

    private async Task WalletAsync(params string[] args)
    {
        if (_wallet is null || _net is null) { _log.Text = "veinchain-wallet not found — dotnet build the VeinChain repository."; return; }
        if (args.Length > 1 && args[1] is null) { _log.Text = "pick an account first"; return; }
        _log.Text = "veinchain-wallet " + string.Join(" ", args) + " …";
        var chain = Chain;
        var (code, output) = await Task.Run(() => global::Vein.Cloud.VeinTools.Wallet(chain, args));
        _log.Text = $"veinchain-wallet {string.Join(" ", args)}\n{output}";
        await RefreshAsync();
    }

    // ---- drawing --------------------------------------------------------------------------------------------

    private static string Vein(long units) => global::Vein.Cloud.VeinTools.Vein(units);
    private static string Short(string key) => global::Vein.Cloud.VeinTools.Short(key);

    private static Button Action(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3), FontSize = 12, Margin = new Thickness(0, 0, 6, 6) };
        b.Click += (_, _) => click();
        return b;
    }

    private static void Head(StackPanel p, string text) =>
        p.Children.Add(new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) });

    private static void Line(StackPanel p, string text, IBrush? brush, bool mono = false, double size = 12, bool wrap = false, int at = -1)
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis };
        if (mono) t.FontFamily = Mono;
        if (brush is not null) t.Foreground = brush;
        if (at >= 0 && at <= p.Children.Count) p.Children.Insert(at, t); else p.Children.Add(t);
    }
}
