using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Vein.Compiler.Tooling;

namespace Vein.Workbench;

// The drawing behind the Flow tab: bundles as boxes, their shards inside, one row per unit of work (a
// `hear` or a schedule), and each event as a pill wired from the rows that emit it to the rows that hear
// it. Shards never call each other, so these wires ARE the program's control flow.
//
// THREE LEVELS, for a project of 100 bundles of 1000 shards each:
//   - a bundle COLLAPSED is one small box ("312 shards · in 4 · out 7"), and two collapsed bundles are joined
//     by one LINK carrying how many events cross between them — not by a wire per event;
//   - a bundle OPEN lays its shards out in columns; with many shards each one is itself collapsed to a line;
//   - a shard OPEN shows its rows, one per `hear` and schedule, wired by event pills as before.
// Double-click a bundle's or a shard's title to open or close it. A big program starts collapsed; bundles can
// be hidden (the panel's list), and nothing off screen is drawn.
//
// Laid out left to right by how far an event travels from the principal — longest path, with the edges
// that close a cycle set aside first. Then it is the reader's: drag a bundle by its title, drag a pill, pan
// the empty floor, wheel to zoom. Hover says who emits and who hears; a click pins that highlight.
internal sealed class FlowCanvas : Control
{
    // ---- geometry, in world units --------------------------------------------------------------
    private const double BoxW = 250, BundleHead = 30, ShardHead = 22, RowH = 19, Pad = 8, ShardGap = 6;
    private const double ColGap = 200, BundleGap = 36, PillH = 22, PillGap = 24;

    /// Past these, a program opens collapsed — every bundle a box, every link a count.
    private const int CollapseBundles = 6, CollapseShards = 120;
    /// An open bundle with more shards than this shows each shard as one line until it is opened too.
    private const int CollapseShardsInBundle = 12;

    private static readonly Typeface Ui = new("Inter, Segoe UI");
    private static readonly Typeface Mono = new("Cascadia Code, Consolas, monospace");

    private static readonly Color[] Hues =
    {
        Color.Parse("#4FC3F7"), Color.Parse("#9575CD"), Color.Parse("#F06292"), Color.Parse("#64B5F6"),
        Color.Parse("#4DD0E1"), Color.Parse("#BA68C8"), Color.Parse("#7986CB"), Color.Parse("#FFB74D"),
        Color.Parse("#AED581"), Color.Parse("#4DB6AC"), Color.Parse("#E57373"), Color.Parse("#90CAF9"),
    };
    private static readonly Color Defect = Color.Parse("#E0575B");

    // ---- model --------------------------------------------------------------------------------
    private sealed class BundleBox
    {
        public required FlowBundle Bundle;
        /// The one shard this box shows, when bundles are split into their shards (Explode); null: the whole bundle.
        public string? Part;
        /// What a drag is remembered by: the bundle, or the bundle and its shard.
        public string Key => Part is null ? Bundle.Name : Bundle.Name + "." + Part;
        /// One summary row instead of its shards; its links to other collapsed bundles are counts.
        public bool Collapsed;
        public Point Pos;
        public double H;
        public double W = BoxW;
        public int Col;
        public readonly List<ShardBox> Shards = new();
        public Rect Bounds => new(Pos, new Size(W, H));
        public Rect Head => new(Pos, new Size(W, BundleHead));
    }

    private sealed class ShardBox
    {
        public required BundleBox Owner;
        public required string Name;
        public string Kind = "shard";                       // shard · boot · summary (a collapsed bundle's one row)
        public string Carries = "";
        public FlowPlace? At;
        public double Top;                                  // relative to the bundle
        public double X;                                    // its column, relative to the bundle
        /// One row standing for all of its units, until it is opened.
        public bool Collapsed;
        public int Units;
        public readonly List<UnitRow> Rows = new();
        public double HeadH => Kind is "boot" or "summary" ? 2 : ShardHead;   // these need no title of their own
        public double H => HeadH + Rows.Count * RowH + 4;
        public Rect Bounds => new(Owner.Pos.X + X + Pad, Owner.Pos.Y + Top, BoxW - 2 * Pad, H);
        public Rect Head => new(Bounds.X, Bounds.Y, Bounds.Width, HeadH);
    }

    private sealed class UnitRow
    {
        public required ShardBox Shard;
        public required string Label;
        public string? Hears;                               // event key, for a hear row
        /// Every event a collapsed row stands for hearing.
        public HashSet<string>? HearsAll;
        public string Audience = "";
        public List<FlowEmit> Emits = new();
        public FlowPlace? At;
        public int Index;
        public bool HearsEvent(string key) => Hears == key || HearsAll?.Contains(key) == true;
        public Rect Bounds => new(Shard.Bounds.X, Shard.Bounds.Y + Shard.HeadH + Index * RowH, Shard.Bounds.Width, RowH);
        public Point Right => new(Shard.Bounds.Right + Pad, Bounds.Center.Y);
        public Point Left => new(Shard.Bounds.Left - Pad, Bounds.Center.Y);
    }

    private sealed class Pill
    {
        public required FlowEvent Event;
        public required Color Hue;
        public readonly List<UnitRow> From = new();
        public readonly List<UnitRow> To = new();
        public Vector Manual;
        public Point Pos;
        public double W;
        public Rect Bounds => new(Pos, new Size(W, PillH));
        public bool Defect => Event.Unheard || Event.NeverEmitted;
    }

    /// Two collapsed bundles and every event that crosses from one to the other — one curve, one count.
    private sealed class Link
    {
        public required BundleBox From;
        public required BundleBox To;
        public readonly List<FlowEvent> Events = new();
        public Rect Badge;
    }

    private EventFlow? _flow;
    private readonly List<BundleBox> _bundles = new();
    private readonly List<Pill> _pills = new();
    private readonly List<Link> _links = new();
    private readonly Dictionary<string, Point> _moved = new(StringComparer.Ordinal);       // bundle → where the reader put it
    private readonly Dictionary<string, Vector> _pillMoved = new(StringComparer.Ordinal);  // event → nudge

    /// Bundle names the reader collapsed or opened (by name: a re-read keeps them), and the shards they opened.
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _openShards = new(StringComparer.Ordinal);            // "Bundle.Shard"
    /// Bundles not drawn at all — the panel's list unchecks them.
    private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);

    // ---- view ---------------------------------------------------------------------------------
    private double _scale = 1;
    private Vector _offset = new(20, 20);
    private bool _fitted;

    private object? _hover;      // Pill, UnitRow, ShardBox, BundleBox or Link
    private object? _pinned;
    private string _filter = "";
    private bool _showStdlib;
    private bool _explode;

    private Point _press;
    private bool _dragging, _moved_;
    private object? _dragTarget;
    private Point _dragStartWorld;
    private Point _dragOrigin;
    private Vector _dragOffsetOrigin;
    private Point _mouse;

    private double _dash;
    private readonly DispatcherTimer _anim;

    public Action<string, int>? Navigate { get; set; }

    /// Fired with a one-line description of what the pointer is over, for the panel's status strip.
    public Action<string>? Status { get; set; }

    /// Fired after a new program is shown, so the panel can list its bundles.
    public Action? BundlesChanged { get; set; }

    public FlowCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        // Only while something is lit: the moving dashes are what says which way an event travels.
        _anim = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Render, (_, _) =>
        {
            _dash = (_dash + 0.6) % 1000;
            InvalidateVisual();
        });
    }

    public bool ShowStdlib
    {
        get => _showStdlib;
        set { _showStdlib = value; Rebuild(keepView: true); }
    }

    /// Each shard its own box, laid out by the same event flow — for a few bundles, read shard by shard.
    public bool Explode
    {
        get => _explode;
        set { _explode = value; Rebuild(keepView: false); }
    }

    public string Filter
    {
        get => _filter;
        set { _filter = value.Trim(); Animate(); InvalidateVisual(); }
    }

    // ---- the bundles the panel lists, hides, collapses and finds ---------------------------------

    public sealed record BundleInfo(string Name, int Shards, bool Library, bool Hidden, bool Collapsed);

    /// Every bundle of the program shown, in load order.
    public IReadOnlyList<BundleInfo> AllBundles =>
        _flow is null ? Array.Empty<BundleInfo>()
        : _flow.Bundles.Select(b => new BundleInfo(b.Name, b.Shards.Count, b.IsLibrary, _hidden.Contains(b.Name), _collapsed.Contains(b.Name))).ToList();

    public void SetHidden(string bundle, bool hidden)
    {
        if (hidden ? _hidden.Add(bundle) : _hidden.Remove(bundle)) Rebuild(keepView: true);
    }

    public void ShowOnly(IEnumerable<string> bundles)
    {
        var keep = bundles.ToHashSet(StringComparer.Ordinal);
        _hidden.Clear();
        if (_flow is not null) foreach (var b in _flow.Bundles) if (!keep.Contains(b.Name)) _hidden.Add(b.Name);
        Rebuild(keepView: false);
    }

    /// Every bundle an event links to one already shown, shown too — "what does this talk to?".
    public void AddNeighbours()
    {
        if (_flow is null) return;
        var shown = _flow.Bundles.Select(b => b.Name).Where(n => !_hidden.Contains(n)).ToHashSet(StringComparer.Ordinal);
        foreach (var e in _flow.Events.Where(e => Shown(e.Key)))
        {
            var ends = e.Emitters.Select(x => x.Bundle).Concat(e.Hearers.Select(x => x.Bundle)).Distinct().ToList();
            if (ends.Any(shown.Contains)) foreach (var b in ends) _hidden.Remove(b);
        }
        Rebuild(keepView: false);
    }

    public void CollapseAll(bool collapse)
    {
        if (_flow is null) return;
        _collapsed.Clear();
        if (collapse) foreach (var b in _flow.Bundles) _collapsed.Add(b.Name);
        Rebuild(keepView: false);
    }

    /// Centre the view on a bundle and pin it — the list's click.
    public void Find(string bundle)
    {
        var box = _bundles.FirstOrDefault(b => b.Bundle.Name == bundle);
        if (box is null) return;
        _scale = Math.Max(_scale, 0.6);
        _offset = new Vector(Bounds.Width / 2 - box.Bounds.Center.X * _scale, Bounds.Height / 2 - box.Bounds.Center.Y * _scale);
        _pinned = box;
        Animate();
        InvalidateVisual();
    }

    public void Show(EventFlow flow)
    {
        bool first = _flow is null || _flow.Name != flow.Name;
        _flow = flow;
        if (first)
        {
            _moved.Clear(); _pillMoved.Clear(); _fitted = false; _pinned = null;
            _hidden.Clear(); _openShards.Clear(); _collapsed.Clear();
            // A big program opens as its bundles and their links; a small one opens whole.
            if (flow.Bundles.Count > CollapseBundles || flow.Shards.Count() > CollapseShards)
                foreach (var b in flow.Bundles) _collapsed.Add(b.Name);
        }
        Rebuild(keepView: !first);
        BundlesChanged?.Invoke();
    }

    public void ResetLayout()
    {
        _moved.Clear();
        _pillMoved.Clear();
        Rebuild(keepView: false);
    }

    public void Fit()
    {
        if (_bundles.Count == 0 || Bounds.Width < 10) return;
        var world = _bundles.Select(b => b.Bounds).Concat(_pills.Select(p => p.Bounds)).Aggregate((a, b) => a.Union(b)).Inflate(30);
        _scale = Math.Clamp(Math.Min(Bounds.Width / world.Width, Bounds.Height / world.Height), 0.05, 1.4);
        _offset = new Vector(
            (Bounds.Width - world.Width * _scale) / 2 - world.X * _scale,
            (Bounds.Height - world.Height * _scale) / 2 - world.Y * _scale);
        _fitted = true;
        InvalidateVisual();
    }

    // ---- building and laying out ---------------------------------------------------------------

    private bool Shown(string key) => _showStdlib || key.Contains('.');

    private void Rebuild(bool keepView)
    {
        _bundles.Clear();
        _pills.Clear();
        _links.Clear();
        _hover = null;
        if (_pinned is not null) _pinned = null;
        if (_flow is null) { InvalidateVisual(); return; }

        var rows = new List<UnitRow>();
        // The standard library's bundles are drawn only with its events: a box with no wires says nothing.
        foreach (var b in _flow.Bundles.Where(b => (_showStdlib || !b.IsLibrary) && !_hidden.Contains(b.Name)))
        {
            if (_explode) BuildSplit(b, rows);
            else if (_collapsed.Contains(b.Name)) BuildCollapsed(b, rows);
            else BuildOpen(b, rows);
        }

        // An event becomes a pill when at least one of its ends is in an open bundle; between collapsed
        // bundles only, it is counted on the link that joins them — a thousand wires would say nothing.
        var links = new Dictionary<(BundleBox, BundleBox), Link>();
        var byHear = new Dictionary<string, List<UnitRow>>(StringComparer.Ordinal);
        var byEmit = new Dictionary<string, List<UnitRow>>(StringComparer.Ordinal);
        foreach (var r in rows)
        {
            foreach (var k in r.HearsAll ?? (r.Hears is null ? Enumerable.Empty<string>() : new[] { r.Hears }))
                (byHear.TryGetValue(k, out var l) ? l : byHear[k] = new()).Add(r);
            foreach (var k in r.Emits.Select(x => x.Event).Distinct())
                (byEmit.TryGetValue(k, out var l) ? l : byEmit[k] = new()).Add(r);
        }
        foreach (var e in _flow.Events.Where(e => Shown(e.Key)))
        {
            var from = byEmit.GetValueOrDefault(e.Key) ?? new List<UnitRow>();
            var to = byHear.GetValueOrDefault(e.Key) ?? new List<UnitRow>();
            if (from.Count + to.Count == 0) continue;   // every end hidden
            bool allCollapsed = from.Concat(to).All(r => r.Shard.Owner.Collapsed);
            if (allCollapsed)
            {
                foreach (var f in from.Select(r => r.Shard.Owner).Distinct())
                    foreach (var t in to.Select(r => r.Shard.Owner).Distinct())
                    {
                        if (f == t) continue;   // inside one collapsed bundle: its own business
                        if (!links.TryGetValue((f, t), out var link)) links[(f, t)] = link = new Link { From = f, To = t };
                        link.Events.Add(e);
                    }
                continue;
            }
            var pill = new Pill { Event = e, Hue = e.Unheard || e.NeverEmitted ? Defect : HueOf(e.Key) };
            pill.From.AddRange(from);
            pill.To.AddRange(to);
            pill.W = Measure("@" + e.Name, 12, Mono) + 22;
            if (_pillMoved.TryGetValue(e.Key, out var nudge)) pill.Manual = nudge;
            _pills.Add(pill);
        }
        _links.AddRange(links.Values);

        Layout();
        if (!keepView || !_fitted) Dispatcher.UIThread.Post(Fit, DispatcherPriority.Background);
        InvalidateVisual();
    }

    private UnitRow Row(ShardBox shard, FlowUnit u) => new()
    {
        Shard = shard, Index = shard.Rows.Count, At = u.At, Audience = u.Audience, Emits = u.Emits.ToList(),
        Hears = u.IsHear ? u.Trigger : null,
        Label = u.IsHear ? "hear @" + Compiler.Ir.Interp.EventName(u.Trigger) : "◷ " + ScheduleLabel(u.Trigger),
    };

    /// One row standing for many units: everything they emit, everything they hear.
    private static UnitRow Summary(ShardBox shard, string label, IEnumerable<FlowUnit> units, FlowPlace? at)
    {
        var list = units.ToList();
        return new UnitRow
        {
            Shard = shard, Index = shard.Rows.Count, At = at, Label = label,
            Emits = list.SelectMany(u => u.Emits).ToList(),
            HearsAll = list.Where(u => u.IsHear).Select(u => u.Trigger).ToHashSet(StringComparer.Ordinal),
            Audience = list.Any(u => u.Audience.Length > 0) ? "some hears have an audience" : "",
        };
    }

    private ShardBox? Boot(BundleBox box, FlowBundle b, ref double y, List<UnitRow> rows)
    {
        // The boot event, as a row of its own at the top of the principal: it is the one emit no shard makes,
        // and without it the map has no visible beginning.
        if (!b.IsPrincipal || _flow!.Start is not { } start) return null;
        var boot = new ShardBox { Owner = box, Name = "start", Kind = "boot", At = start.At, Top = y };
        var row = new UnitRow
        {
            Shard = boot, Label = "▶ start @" + Compiler.Ir.Interp.EventName(start.Event), At = start.At,
            Emits = new List<FlowEmit> { new(start.Event, start.At, null) },
        };
        boot.Rows.Add(row);
        rows.Add(row);
        box.Shards.Add(boot);
        y += boot.H + ShardGap;
        return boot;
    }

    private void BuildCollapsed(FlowBundle b, List<UnitRow> rows)
    {
        var box = new BundleBox { Bundle = b, Collapsed = true };
        double y = BundleHead;
        Boot(box, b, ref y, rows);
        var summary = new ShardBox { Owner = box, Name = b.Name, Kind = "summary", Top = y, Units = b.Shards.Sum(s => s.Units.Count) };
        var row = Summary(summary, $"{b.Shards.Count} shard{(b.Shards.Count == 1 ? "" : "s")} · {summary.Units} units", b.Shards.SelectMany(s => s.Units), new FlowPlace(b.File, 1));
        summary.Rows.Add(row);
        rows.Add(row);
        box.Shards.Add(summary);
        y += summary.H + ShardGap;
        box.H = Math.Max(y + Pad - ShardGap, BundleHead + 24);
        _bundles.Add(box);
    }

    private void BuildOpen(FlowBundle b, List<UnitRow> rows)
    {
        var box = new BundleBox { Bundle = b };
        double top = BundleHead;
        Boot(box, b, ref top, rows);

        // Many shards: columns, each shard one line until opened — a thousand shards in a block, not a tower.
        bool many = b.Shards.Count > CollapseShardsInBundle;
        int cols = many ? Math.Clamp((int)Math.Ceiling(Math.Sqrt(b.Shards.Count / 3.0)), 2, 12) : 1;
        var colY = Enumerable.Repeat(top, cols).ToArray();
        foreach (var s in b.Shards)
        {
            int c = Array.IndexOf(colY, colY.Min());
            var shard = new ShardBox { Owner = box, Name = s.Name, Kind = s.Kind, Carries = s.Carries, At = s.At, Top = colY[c], X = c * BoxW, Units = s.Units.Count };
            if (many && !_openShards.Contains(b.Name + "." + s.Name))
            {
                shard.Collapsed = true;
                var row = Summary(shard, $"{s.Units.Count} unit{(s.Units.Count == 1 ? "" : "s")}", s.Units, s.At);
                shard.Rows.Add(row);
                rows.Add(row);
            }
            else
                foreach (var u in s.Units)
                {
                    var row = Row(shard, u);
                    shard.Rows.Add(row);
                    rows.Add(row);
                }
            box.Shards.Add(shard);
            colY[c] += shard.H + ShardGap;
        }
        box.W = cols * BoxW;
        box.H = Math.Max(colY.Max() + Pad - ShardGap, BundleHead + 24);
        _bundles.Add(box);
    }

    /// Split: every shard (and the boot row) a box of its own, headed "Bundle › Shard".
    private void BuildSplit(FlowBundle b, List<UnitRow> rows)
    {
        var boot = new BundleBox { Bundle = b, Part = "start" };
        double y = BundleHead;
        if (Boot(boot, b, ref y, rows) is not null) { boot.H = Math.Max(y + Pad - ShardGap, BundleHead + 24); _bundles.Add(boot); }
        foreach (var s in b.Shards)
        {
            var box = new BundleBox { Bundle = b, Part = s.Name };
            var shard = new ShardBox { Owner = box, Name = s.Name, Kind = s.Kind, Carries = s.Carries, At = s.At, Top = BundleHead, Units = s.Units.Count };
            foreach (var u in s.Units)
            {
                var row = Row(shard, u);
                shard.Rows.Add(row);
                rows.Add(row);
            }
            box.Shards.Add(shard);
            box.H = Math.Max(BundleHead + shard.H + Pad, BundleHead + 24);
            _bundles.Add(box);
        }
        if (b.Shards.Count == 0 && boot.Shards.Count == 0) { boot.Part = null; boot.H = BundleHead + 24; _bundles.Add(boot); }
    }

    private static string ScheduleLabel(string trigger) => trigger switch
    {
        "once" => "run once",
        "tick" => "each tick",
        "frame" => "each frame",
        "every" => "every …",
        "runtime" => "raised by the runtime",
        "host" => "answered by the host",
        _ => trigger,
    };

    private void Layout()
    {
        // Bundle-level edges: an event emitted in A and heard in B — by pill or by link.
        var index = _bundles.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);
        var succ = _bundles.Select(_ => new HashSet<int>()).ToArray();
        foreach (var p in _pills)
        {
            var froms = p.From.Select(f => index[f.Shard.Owner]).Distinct().ToList();
            var tos = p.To.Select(t => index[t.Shard.Owner]).Distinct().ToList();
            foreach (int a in froms) foreach (int b in tos) if (a != b) succ[a].Add(b);
        }
        foreach (var l in _links) succ[index[l.From]].Add(index[l.To]);

        // Set aside the edges that close a cycle (DFS from the principal first, then the rest in load
        // order), then layer by longest path over what is left.
        var state = new int[_bundles.Count];
        var dag = _bundles.Select(_ => new List<int>()).ToArray();
        void Dfs(int v)
        {
            state[v] = 1;
            foreach (int w in succ[v].OrderBy(w => w))
            {
                if (state[w] == 1) continue;           // back edge — the loop's return wire
                dag[v].Add(w);
                if (state[w] == 0) Dfs(w);
            }
            state[v] = 2;
        }
        for (int i = 0; i < _bundles.Count; i++) if (state[i] == 0) Dfs(i);

        // Longest path, in topological order: one pass over a DAG of thousands of boxes, not a relaxation loop.
        var col = new int[_bundles.Count];
        var indeg = new int[_bundles.Count];
        foreach (var outs in dag) foreach (int w in outs) indeg[w]++;
        var ready = new Queue<int>(Enumerable.Range(0, _bundles.Count).Where(i => indeg[i] == 0));
        while (ready.Count > 0)
        {
            int v = ready.Dequeue();
            foreach (int w in dag[v])
            {
                col[w] = Math.Max(col[w], col[v] + 1);
                if (--indeg[w] == 0) ready.Enqueue(w);
            }
        }

        // A column is as wide as its widest box — an open bundle in columns is wider than one shard.
        int columns = _bundles.Count == 0 ? 0 : col.Max() + 1;
        var colW = new double[columns];
        for (int i = 0; i < _bundles.Count; i++) colW[col[i]] = Math.Max(colW[col[i]], _bundles[i].W);
        var colX = new double[columns];
        for (int c = 1; c < columns; c++) colX[c] = colX[c - 1] + colW[c - 1] + ColGap;

        // Within a column, order by where the bundles feeding it sit — fewer crossings for one pass of work.
        var preds = _bundles.Select(_ => new List<int>()).ToArray();
        for (int v = 0; v < _bundles.Count; v++) foreach (int w in dag[v]) preds[w].Add(v);
        var centre = new double[_bundles.Count];
        foreach (var group in Enumerable.Range(0, _bundles.Count).GroupBy(i => col[i]).OrderBy(g => g.Key))
        {
            double y = 0;
            foreach (int i in group.OrderBy(i => preds[i].Count == 0 ? i * 0.001 : preds[i].Average(p => centre[p])).ThenBy(i => i))
            {
                var box = _bundles[i];
                box.Col = col[i];
                box.Pos = new Point(colX[col[i]], y);
                centre[i] = y + box.H / 2;
                y += box.H + BundleGap;
            }
        }

        foreach (var box in _bundles)
            if (_moved.TryGetValue(box.Key, out var at)) box.Pos = at;

        PlacePills();
        PlaceLinks();
    }

    /// A pill sits just outside the bundle that emits it — an event LEAVES a bundle — or, when nothing
    /// emits it, just before the first bundle that hears it. Then pills in the same gutter are pushed
    /// apart so none covers another: sorted once, each compared only with the last one in its gutter.
    private void PlacePills()
    {
        foreach (var p in _pills)
        {
            if (p.From.Count > 0)
            {
                var a = p.From[0];
                p.Pos = new Point(a.Shard.Owner.Bounds.Right + PillGap, a.Bounds.Center.Y - PillH / 2);
            }
            else if (p.To.Count > 0)
            {
                var a = p.To[0];
                p.Pos = new Point(a.Shard.Owner.Pos.X - PillGap - p.W, a.Bounds.Center.Y - PillH / 2);
            }
            p.Pos += p.Manual;
        }

        var lowest = new Dictionary<long, double>();
        foreach (var p in _pills.OrderBy(p => p.Pos.Y))
        {
            long gutter = (long)Math.Floor(p.Pos.X / 60);
            double floor = Math.Max(lowest.GetValueOrDefault(gutter, double.MinValue), lowest.GetValueOrDefault(gutter - 1, double.MinValue));
            if (p.Pos.Y < floor) p.Pos = p.Pos.WithY(floor);
            lowest[gutter] = p.Pos.Y + PillH + 3;
        }
    }

    /// A link's badge, half way along it.
    private void PlaceLinks()
    {
        foreach (var l in _links)
        {
            var (a, _, b, _) = LinkEnds(l);
            string label = LinkLabel(l);
            double w = Measure(label, 11, Ui) + 16;
            var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            l.Badge = new Rect(mid.X - w / 2, mid.Y - 10, w, 20);
        }
    }

    private static string LinkLabel(Link l) => l.Events.Count == 1 ? "@" + l.Events[0].Name : $"{l.Events.Count} events";

    /// Out of the side of A that faces B, into the side of B that faces A.
    private static (Point A, int AOut, Point B, int BIn) LinkEnds(Link l)
    {
        var fa = l.From.Bounds;
        var fb = l.To.Bounds;
        bool forward = fb.Center.X >= fa.Center.X;
        return forward
            ? (new Point(fa.Right, fa.Y + 18), +1, new Point(fb.Left, fb.Y + 18), +1)
            : (new Point(fa.Left, fa.Y + 18), -1, new Point(fb.Right, fb.Y + 18), -1);
    }

    private static Color HueOf(string key)
    {
        uint h = 2166136261;
        foreach (char c in key) h = (h ^ c) * 16777619;
        return Hues[h % (uint)Hues.Length];
    }

    // ---- what is lit ---------------------------------------------------------------------------

    private sealed record LitSet(HashSet<Pill> Pills, HashSet<UnitRow> Rows, HashSet<BundleBox> Boxes, HashSet<Link> Links);

    /// The pills, rows, boxes and links the current focus touches — hover first, then a pinned click, then
    /// the filter. Null means nothing is focused and everything draws at full strength.
    private LitSet? Lit()
    {
        var focus = _hover ?? _pinned;
        var pills = new HashSet<Pill>();
        var rows = new HashSet<UnitRow>();
        var boxes = new HashSet<BundleBox>();
        var links = new HashSet<Link>();

        void LightPill(Pill p) { pills.Add(p); rows.UnionWith(p.From); rows.UnionWith(p.To); }
        void LightRow(UnitRow r)
        {
            rows.Add(r);
            foreach (var p in _pills.Where(p => p.From.Contains(r) || p.To.Contains(r))) pills.Add(p);
        }
        void LightLink(Link l) { links.Add(l); boxes.Add(l.From); boxes.Add(l.To); }

        switch (focus)
        {
            case Pill p: LightPill(p); break;
            case UnitRow r: LightRow(r); break;
            case ShardBox s: foreach (var r in s.Rows) LightRow(r); break;
            case Link l: LightLink(l); break;
            case BundleBox b:
                boxes.Add(b);
                // What goes IN and OUT of the bundle: events crossing its boundary, either way.
                foreach (var p in _pills)
                {
                    bool emitsHere = p.From.Any(r => r.Shard.Owner == b), hearsHere = p.To.Any(r => r.Shard.Owner == b);
                    if (emitsHere || hearsHere)
                    {
                        pills.Add(p);
                        rows.UnionWith(p.From.Where(r => r.Shard.Owner == b || hearsHere));
                        rows.UnionWith(p.To.Where(r => r.Shard.Owner == b || emitsHere));
                    }
                }
                foreach (var l in _links.Where(l => l.From == b || l.To == b)) LightLink(l);
                break;
            default:
                if (_filter.Length == 0) return null;
                foreach (var p in _pills.Where(p => Has(p.Event.Name) || Has(p.Event.Key))) LightPill(p);
                foreach (var r in _bundles.SelectMany(b => b.Shards).SelectMany(s => s.Rows)
                                          .Where(r => Has(r.Shard.Name) || Has(r.Shard.Owner.Bundle.Name)))
                    LightRow(r);
                foreach (var b in _bundles.Where(b => Has(b.Bundle.Name))) boxes.Add(b);
                foreach (var l in _links.Where(l => l.Events.Any(e => Has(e.Name)))) LightLink(l);
                break;
        }
        foreach (var r in rows) boxes.Add(r.Shard.Owner);
        return new LitSet(pills, rows, boxes, links);

        bool Has(string s) => s.Contains(_filter.TrimStart('@'), StringComparison.OrdinalIgnoreCase);
    }

    private void Animate()
    {
        bool on = _hover is not null || _pinned is not null || _filter.Length > 0;
        if (on && !_anim.IsEnabled) _anim.Start();
        if (!on && _anim.IsEnabled) _anim.Stop();
    }

    // ---- drawing -------------------------------------------------------------------------------

    // Dimming is an alpha on every brush, not `PushOpacity`: a layer per box is slower, and under the
    // Skia backend a nested layer inside the view transform drew its contents somewhere else entirely.
    private double _alpha = 1;
    private IDisposable Fade(double a)
    {
        double was = _alpha;
        _alpha = was * a;
        return new Restore(() => _alpha = was);
    }
    private sealed class Restore(Action undo) : IDisposable { public void Dispose() => undo(); }
    private IBrush Br(Color c, double a = 1) => new SolidColorBrush(c, a * _alpha);
    private IBrush Br(IBrush b) => b is ISolidColorBrush s ? new SolidColorBrush(s.Color, s.Opacity * _alpha) : b;

    /// What part of the world is on screen — nothing outside it is drawn.
    private Rect View => new Rect(-_offset.X / _scale, -_offset.Y / _scale, Bounds.Width / _scale, Bounds.Height / _scale).Inflate(40 / _scale);

    /// Far enough out, the words are too small to read and cost the most to draw: boxes and wires only.
    private bool Words => _scale >= 0.3;

    public override void Render(DrawingContext ctx)
    {
        var ground = Palette.Sunken;
        ctx.FillRectangle(ground, new Rect(Bounds.Size));

        if (_flow is null || _bundles.Count == 0)
        {
            Text(ctx, _flow is null ? "Open a program to see its event flow."
                : _hidden.Count > 0 ? "Every bundle is hidden — tick some in the list on the left." : "No shards — nothing emits or hears anything here.",
                new Point(16, 16), 13, Palette.Muted, Ui);
            return;
        }

        var lit = Lit();
        var view = View;

        using (ctx.PushTransform(Matrix.CreateScale(_scale, _scale) * Matrix.CreateTranslation(_offset.X, _offset.Y)))
        {
            DrawGrid(ctx);
            foreach (var l in _links) DrawLink(ctx, l, lit, view, badge: false);
            foreach (var b in _bundles) if (b.Bounds.Intersects(view)) DrawBundle(ctx, b, lit, view);
            foreach (var p in _pills) DrawWires(ctx, p, lit, view);
            foreach (var p in _pills) if (p.Bounds.Intersects(view)) DrawPill(ctx, p, lit);
            foreach (var l in _links) DrawLink(ctx, l, lit, view, badge: true);
        }

        if ((_hover ?? _pinned) is { } focus) DrawTip(ctx, focus);
    }

    private void DrawGrid(DrawingContext ctx)
    {
        if (_scale < 0.15) return;
        var view = View;
        var pen = new Pen(Br(Color.FromArgb(18, 120, 150, 255)), 1 / _scale);
        const double step = 40;
        for (double x = Math.Floor(view.X / step) * step; x < view.Right; x += step) ctx.DrawLine(pen, new Point(x, view.Y), new Point(x, view.Bottom));
        for (double y = Math.Floor(view.Y / step) * step; y < view.Bottom; y += step) ctx.DrawLine(pen, new Point(view.X, y), new Point(view.Right, y));
    }

    private void DrawBundle(DrawingContext ctx, BundleBox b, LitSet? lit, Rect view)
    {
        bool dim = lit is { } l && !l.Boxes.Contains(b) && _hover != b && _pinned != b;
        using var _ = Fade(dim ? 0.35 : 1);

        var accent = b.Bundle.IsPrincipal ? Color.Parse("#7C9CFF") : b.Bundle.Needed ? Color.Parse("#5E6B85") : Color.Parse("#6A5ACD");
        bool focused = _hover == b || _pinned == b;
        // A soft halo, then the box — the "glow" of the reference picture, kept quiet enough to read on.
        ctx.DrawRectangle(null, new Pen(Br(accent, focused ? 0.45 : 0.18), 6), b.Bounds.Inflate(2), 10, 10);
        ctx.DrawRectangle(Br(Color.Parse("#161B29")),
            new Pen(Br(accent), focused ? 2 : 1.2, b.Bundle.Needed ? new DashStyle(new double[] { 4, 3 }, 0) : null),
            b.Bounds, 8, 8);
        ctx.DrawRectangle(Br(accent, 0.22), null, b.Head, 8, 8);

        if (Words || b.Collapsed)
        {
            string mark = b.Collapsed ? "▸ " : b.Part is null && !_explode ? "▾ " : b.Bundle.IsPrincipal ? "★ " : "■ ";
            // Zoomed far out a collapsed bundle's name grows with the zoom, so a map of a hundred stays legible.
            double size = Words ? 13 : Math.Min(11 / _scale, 40);
            Text(ctx, mark + b.Bundle.Name + (b.Part is null ? "" : " › " + b.Part), b.Pos + new Point(10, 7), size, Palette.Ink, Ui, FontWeight.SemiBold);
            if (Words && b.Bundle.Author is { } author)
                TextRight(ctx, author, new Point(b.Pos.X + b.W - 10, b.Pos.Y + 9), 11, Palette.Muted, Ui);
        }

        foreach (var s in b.Shards) if (s.Bounds.Intersects(view)) DrawShard(ctx, s, lit, view);
    }

    private void DrawShard(DrawingContext ctx, ShardBox s, LitSet? lit, Rect view)
    {
        bool focused = _hover == s || _pinned == s;
        var border = s.Kind == "boot" ? Color.Parse("#7C9CFF") : Color.Parse("#3A4462");
        ctx.DrawRectangle(Br(Color.Parse("#1D2336")), new Pen(Br(border), focused ? 1.8 : 1), s.Bounds, 5, 5);
        if (!Words) return;
        if (s.Kind is not ("boot" or "summary"))
        {
            Text(ctx, (s.Collapsed ? "▸ " : s.Owner.Shards.Count > CollapseShardsInBundle ? "▾ " : "") + s.Name,
                 new Point(s.Bounds.X + 8, s.Bounds.Y + 4), 12, Palette.Ink, Mono, FontWeight.SemiBold);
            string tag = s.Kind == "shard" ? s.Carries : (s.Kind + " " + s.Carries).Trim();
            if (tag.Length > 0) TextRight(ctx, tag, new Point(s.Bounds.Right - 8, s.Bounds.Y + 5), 10, Palette.Muted, Mono);
        }

        foreach (var r in s.Rows)
        {
            var rb = r.Bounds;
            if (!rb.Intersects(view)) continue;
            bool rowLit = lit?.Rows.Contains(r) == true || _hover == r || _pinned == r;
            bool dead = r.Hears is { } k && _flow!.Event(k) is { NeverEmitted: true };
            if (rowLit) ctx.FillRectangle(Br(Color.FromArgb(40, 124, 156, 255)), rb.Deflate(new Thickness(2, 1)));

            // A dot in the event's colour: the row's colour IS the wire it hangs on.
            var dot = r.Hears is { } key ? (dead ? Defect : HueOf(key)) : Color.Parse("#8892A8");
            if (s.Kind == "boot") dot = Color.Parse("#7C9CFF");
            ctx.DrawEllipse(Br(dot), null, new Point(rb.X + 10, rb.Center.Y), 3, 3);
            Text(ctx, r.Label, new Point(rb.X + 18, rb.Y + 2), 11.5, dead ? Br(Defect) : Palette.Ink, Mono);

            int outs = r.Emits.Select(e => e.Event).Distinct().Count(Shown);
            if (outs > 0) TextRight(ctx, "→ " + outs, new Point(rb.Right - 6, rb.Y + 3), 10, Palette.Muted, Mono);
            if (r.Audience.Length > 0) TextRight(ctx, "⛉", new Point(rb.Right - (outs > 0 ? 34 : 6), rb.Y + 2), 11, Palette.Muted, Ui);
        }
    }

    private void DrawWires(DrawingContext ctx, Pill p, LitSet? lit, Rect view)
    {
        bool on = lit?.Pills.Contains(p) ?? true;
        bool hot = lit?.Pills.Contains(p) == true;
        double alpha = on ? 1 : 0.12;
        var pb = p.Bounds;

        foreach (var f in p.From)
        {
            // Out of whichever side of the bundle faces the pill — a pill the reader dragged to the left
            // of its emitter is still wired without crossing the box.
            bool right = pb.Center.X >= f.Shard.Owner.Bounds.Center.X;
            var a = right ? f.Right : f.Left;
            var b = right ? new Point(pb.Left, pb.Center.Y) : new Point(pb.Right, pb.Center.Y);
            if (!new Rect(a, b).Normalize().Inflate(60).Intersects(view)) continue;
            Wire(ctx, a, right ? +1 : -1, b, right ? +1 : -1, p.Hue, alpha, hot, arrow: false);
        }

        foreach (var t in p.To)
        {
            // Enter the row from the side that faces the pill, so a wire back into the same bundle (or
            // into an earlier column) does not cut across the box.
            bool fromLeft = t.Left.X > pb.Center.X;
            var a = fromLeft ? new Point(pb.Right, pb.Center.Y) : new Point(pb.Left, pb.Center.Y);
            var b = fromLeft ? t.Left : t.Right;
            if (!new Rect(a, b).Normalize().Inflate(60).Intersects(view)) continue;
            Wire(ctx, a, fromLeft ? +1 : -1, b, fromLeft ? +1 : -1, p.Hue, alpha, hot, arrow: true);
        }

        // Emitted and heard by nobody: a stub that ends in nothing, which is exactly what happens.
        if (p.Event.Unheard && pb.Intersects(view))
        {
            var a = new Point(pb.Right, pb.Center.Y);
            ctx.DrawLine(new Pen(Br(Defect, alpha), 1.6, new DashStyle(new double[] { 2, 2 }, 0)), a, a + new Point(36, 0));
            Text(ctx, "✕", a + new Point(38, -8), 12, Br(Defect, alpha), Ui);
        }
    }

    /// Two collapsed bundles: one curve, as thick as the traffic, and a badge saying what crosses.
    private void DrawLink(DrawingContext ctx, Link l, LitSet? lit, Rect view, bool badge)
    {
        var (a, aOut, b, bIn) = LinkEnds(l);
        if (!new Rect(a, b).Normalize().Inflate(80).Intersects(view)) return;
        bool on = lit is null || lit.Links.Contains(l);
        bool hot = lit?.Links.Contains(l) == true || _hover == l || _pinned == l;
        var hue = l.Events.Count == 1 ? HueOf(l.Events[0].Key) : Color.Parse("#9AA8D6");
        if (!badge)
        {
            double width = Math.Min(1.2 + Math.Log2(l.Events.Count + 1), 6);
            Wire(ctx, a, aOut, b, bIn, hue, on ? 1 : 0.12, hot, arrow: true, width);
            return;
        }
        if (!Words) return;
        using var _ = Fade(on ? 1 : 0.3);
        ctx.DrawRectangle(Br(Color.Parse("#141826")), new Pen(Br(hue), hot ? 2 : 1.2), l.Badge, 10, 10);
        Text(ctx, LinkLabel(l), new Point(l.Badge.X + 8, l.Badge.Y + 3), 11, Palette.Ink, Ui);
    }

    /// A curve leaving `a` heading `aOut` (+1 right, −1 left) and arriving at `b` heading `bIn`.
    private void Wire(DrawingContext ctx, Point a, int aOut, Point b, int bIn, Color hue, double alpha, bool hot, bool arrow, double width = 1.4)
    {
        double dx = Math.Max(40, Math.Abs(b.X - a.X) * 0.5);
        var c1 = new Point(a.X + dx * aOut, a.Y);
        var c2 = new Point(b.X - dx * bIn, b.Y);

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(a, false);
            g.CubicBezierTo(c1, c2, b);
            g.EndFigure(false);
        }

        ctx.DrawGeometry(null, new Pen(Br(hue, 0.16 * alpha), (hot ? 9 : 6) + width - 1.4), geo);
        ctx.DrawGeometry(null, new Pen(Br(hue, 0.95 * alpha), hot ? width + 0.8 : width), geo);
        // Moving pulses on a lit wire: the direction an event travels, without an arrow on every curve.
        if (hot)
            ctx.DrawGeometry(null, new Pen(Br(Colors.White, 0.85), 2.2,
                new DashStyle(new double[] { 0.5, 6 }, -_dash), PenLineCap.Round), geo);

        if (arrow)
        {
            double dir = bIn;
            var tip = b;
            var arrowGeo = new StreamGeometry();
            using (var g = arrowGeo.Open())
            {
                g.BeginFigure(tip, true);
                g.LineTo(tip + new Point(-7 * dir, -4));
                g.LineTo(tip + new Point(-7 * dir, 4));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(Br(hue, alpha), null, arrowGeo);
        }
    }

    private void DrawPill(DrawingContext ctx, Pill p, LitSet? lit)
    {
        bool on = lit?.Pills.Contains(p) ?? true;
        bool focused = _hover == p || _pinned == p;
        using var _ = Fade(on ? 1 : 0.25);
        var r = p.Bounds;
        ctx.DrawRectangle(null, new Pen(Br(p.Hue, focused ? 0.5 : 0.22), 6), r.Inflate(1.5), PillH / 2, PillH / 2);
        ctx.DrawRectangle(Br(Color.Parse("#141826")), new Pen(Br(p.Hue), focused ? 2 : 1.3), r, PillH / 2, PillH / 2);
        if (Words) Text(ctx, "@" + p.Event.Name, new Point(r.X + 11, r.Y + 3), 12, Br(p.Defect ? Defect : Colors.White), Mono);
    }

    /// The hover card: who emits, who hears, where — the question the picture exists to answer.
    private void DrawTip(DrawingContext ctx, object focus)
    {
        var lines = TipLines(focus);
        if (lines.Count == 0) return;

        var formatted = lines.Select(l => Format(l.Text, l.Head ? 12.5 : 11.5, l.Head ? Ui : Mono,
                                                 l.Bad ? Br(Defect) : l.Head ? Palette.Ink : Palette.Muted,
                                                 l.Head ? FontWeight.SemiBold : FontWeight.Normal)).ToList();
        double w = formatted.Max(f => f.Width) + 20, h = formatted.Sum(f => f.Height + 2) + 14;

        var at = focus == _pinned && _hover is null ? new Point(Bounds.Width - w - 12, 12) : _mouse + new Point(16, 16);
        if (at.X + w > Bounds.Width - 4) at = at.WithX(Math.Max(4, _mouse.X - w - 12));
        if (at.Y + h > Bounds.Height - 4) at = at.WithY(Math.Max(4, Bounds.Height - h - 4));

        var box = new Rect(at, new Size(w, h));
        ctx.DrawRectangle(Br(Color.Parse("#EE10131C")), new Pen(Br(Color.Parse("#3A4462")), 1), box, 6, 6);
        double y = at.Y + 7;
        foreach (var f in formatted)
        {
            ctx.DrawText(f, new Point(at.X + 10, y));
            y += f.Height + 2;
        }
    }

    private readonly record struct TipLine(string Text, bool Head = false, bool Bad = false);

    private List<TipLine> TipLines(object focus)
    {
        var lines = new List<TipLine>();
        string Where(FlowPlace? at) => at is null || at.File.Length == 0 ? "" : $"{Path.GetFileName(at.File)}:{at.Line}";
        string Who(UnitRow r) => r.Shard.Kind switch
        {
            "boot" => r.Shard.Owner.Bundle.Name + " · start",
            "summary" => r.Shard.Owner.Bundle.Name,
            _ => r.Shard.Owner.Bundle.Name + "." + r.Shard.Name,
        };
        const int Most = 14;   // a card listing a thousand ends says nothing either

        switch (focus)
        {
            case Pill p:
                var e = p.Event;
                lines.Add(new("@" + e.Name + (e.Owner is null ? "   (standard library)" : "   " + e.Owner), Head: true));
                lines.Add(new("emitted by"));
                if (p.From.Count == 0) lines.Add(new(e.IsRuntime ? "  the runtime" : "  nobody — these handlers never run", Bad: e.NeverEmitted));
                foreach (var f in p.From.Take(Most))
                    foreach (var x in f.Emits.Where(x => x.Event == e.Key).DistinctBy(x => (x.At.Line, x.Via)).Take(3))
                        lines.Add(new($"  {Who(f)} — {f.Label}{(x.Via is null ? "" : "  via " + x.Via)}   {Where(x.At)}"));
                if (p.From.Count > Most) lines.Add(new($"  … and {p.From.Count - Most} more"));
                lines.Add(new("heard by"));
                if (p.To.Count == 0) lines.Add(new(e.IsHost ? "  the host" : "  nobody — this event goes nowhere", Bad: e.Unheard));
                foreach (var t in p.To.Take(Most))
                    lines.Add(new($"  {Who(t)}{(t.Audience.Length > 0 ? "  [" + t.Audience + "]" : "")}   {Where(t.At)}"));
                if (p.To.Count > Most) lines.Add(new($"  … and {p.To.Count - Most} more"));
                lines.Add(new("double-click to open the " + (p.From.Count > 0 ? "first emit" : "first hear")));
                break;

            case Link l:
                lines.Add(new($"{l.From.Bundle.Name} → {l.To.Bundle.Name}   {LinkLabel(l)}", Head: true));
                foreach (var ev in l.Events.Take(Most))
                {
                    string from = string.Join(", ", ev.Emitters.Where(x => x.Bundle == l.From.Bundle.Name).Select(x => x.Shard).Distinct().Take(3));
                    string to = string.Join(", ", ev.Hearers.Where(x => x.Bundle == l.To.Bundle.Name).Select(x => x.Shard).Distinct().Take(3));
                    lines.Add(new($"  @{ev.Name}   {from} → {to}"));
                }
                if (l.Events.Count > Most) lines.Add(new($"  … and {l.Events.Count - Most} more"));
                lines.Add(new("double-click a bundle's title to open it"));
                break;

            case UnitRow r:
                lines.Add(new($"{Who(r)} — {r.Label}", Head: true));
                if (r.Audience.Length > 0) lines.Add(new("  " + r.Audience));
                if (r.Hears is { } k && _flow!.Event(k) is { } he)
                    lines.Add(he.Emitters.Count == 0
                        ? new(he.IsRuntime ? "  raised by the runtime" : "  nothing emits this — the handler never runs", Bad: he.NeverEmitted)
                        : new("  from " + string.Join(", ", he.Emitters.Take(Most).Select(x => x.Bundle + "." + x.Shard))));
                if (r.HearsAll is { Count: > 0 } all)
                    lines.Add(new("  hears " + string.Join(", ", all.Take(Most).Select(x => "@" + Compiler.Ir.Interp.EventName(x))) + (all.Count > Most ? " …" : "")));
                var outs = r.Emits.Where(x => Shown(x.Event)).GroupBy(x => x.Event).ToList();
                if (outs.Count > 0)
                {
                    lines.Add(new("emits"));
                    foreach (var g in outs.Take(Most))
                    {
                        var ev = _flow!.Event(g.Key);
                        string to = ev is null || ev.Hearers.Count == 0 ? "nobody" : string.Join(", ", ev.Hearers.Take(6).Select(x => x.Bundle + "." + x.Shard));
                        lines.Add(new($"  @{Compiler.Ir.Interp.EventName(g.Key)} → {to}   {Where(g.First().At)}", Bad: ev?.Unheard == true));
                    }
                    if (outs.Count > Most) lines.Add(new($"  … and {outs.Count - Most} more"));
                }
                lines.Add(new(r.Shard.Kind == "summary" ? "double-click the title to open the bundle"
                            : r.Shard.Collapsed ? "double-click the shard's title to open it" : $"{Where(r.At)} · double-click to open"));
                break;

            case ShardBox s:
                lines.Add(new($"{s.Owner.Bundle.Name}.{s.Name}{(s.Carries.Length > 0 ? "  " + s.Carries : "")}", Head: true));
                lines.Add(new($"  {s.Units} unit(s) · emits " +
                              s.Rows.SelectMany(r => r.Emits).Select(x => x.Event).Where(Shown).Distinct().Count()));
                lines.Add(new(s.Collapsed ? "double-click the title to open it" : $"{Where(s.At)} · double-click a row to open it"));
                break;

            case BundleBox b:
                lines.Add(new($"{(b.Bundle.Author is null ? "" : b.Bundle.Author + ".")}{b.Bundle.Name}   " +
                              (b.Bundle.IsPrincipal ? "principal — boots the app" : b.Bundle.Needed ? "linked through a need" : "capability"), Head: true));
                lines.Add(new($"  {b.Bundle.Shards.Count} shard(s) · {b.Bundle.Shards.Sum(s => s.Units.Count)} unit(s)"));
                var ins = _pills.Where(p => p.To.Any(r => r.Shard.Owner == b) && p.From.Any(r => r.Shard.Owner != b)).Select(p => p.Event)
                    .Concat(_links.Where(l => l.To == b).SelectMany(l => l.Events)).DistinctBy(x => x.Key).ToList();
                var exits = _pills.Where(p => p.From.Any(r => r.Shard.Owner == b) && (p.To.Count == 0 || p.To.Any(r => r.Shard.Owner != b))).Select(p => p.Event)
                    .Concat(_links.Where(l => l.From == b).SelectMany(l => l.Events)).DistinctBy(x => x.Key).ToList();
                if (ins.Count > 0) lines.Add(new($"in   {string.Join(", ", ins.Take(Most).Select(x => "@" + x.Name))}{(ins.Count > Most ? " …" : "")}"));
                if (exits.Count > 0) lines.Add(new($"out  {string.Join(", ", exits.Take(Most).Select(x => "@" + x.Name))}{(exits.Count > Most ? " …" : "")}"));
                if (ins.Count + exits.Count == 0) lines.Add(new("  nothing crosses this bundle"));
                lines.Add(new(b.Collapsed ? "double-click the title to open it" : "double-click the title to collapse it"));
                break;
        }
        return lines;
    }

    // ---- input ---------------------------------------------------------------------------------

    private Point ToWorld(Point screen) => new((screen.X - _offset.X) / _scale, (screen.Y - _offset.Y) / _scale);

    private object? HitTest(Point screen)
    {
        var w = ToWorld(screen);
        foreach (var l in _links) if (Words && l.Badge.Contains(w)) return l;
        foreach (var p in Enumerable.Reverse(_pills)) if (p.Bounds.Contains(w)) return p;
        foreach (var b in _bundles)
        {
            if (!b.Bounds.Contains(w)) continue;
            if (b.Head.Contains(w)) return b;
            foreach (var s in b.Shards)
            {
                if (!s.Bounds.Contains(w)) continue;
                if (s.Head.Contains(w) && s.Kind is not ("boot" or "summary")) return s;
                foreach (var r in s.Rows) if (r.Bounds.Contains(w)) return r;
                return s;
            }
            return b;
        }
        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var pt = e.GetCurrentPoint(this);
        _press = pt.Position;
        _moved_ = false;

        if (e.ClickCount == 2 && HitTest(_press) is { } hit)
        {
            Activate(hit);
            e.Handled = true;
            return;
        }

        _dragging = pt.Properties.IsLeftButtonPressed || pt.Properties.IsMiddleButtonPressed;
        var target = HitTest(_press);
        // A bundle moves by its title or its body; a pill moves itself; everything else pans the floor.
        _dragTarget = target switch
        {
            Pill p => p,
            BundleBox b => b,
            ShardBox s => s.Owner,
            UnitRow r => r.Shard.Owner,
            _ => null,
        };
        if (pt.Properties.IsMiddleButtonPressed) _dragTarget = null;
        _dragStartWorld = ToWorld(_press);
        _dragOrigin = _dragTarget is BundleBox bb ? bb.Pos : default;
        _dragOffsetOrigin = _dragTarget is Pill pp ? pp.Manual : _offset;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _mouse = e.GetPosition(this);

        if (_dragging)
        {
            if (!_moved_ && (Math.Abs(_mouse.X - _press.X) > 3 || Math.Abs(_mouse.Y - _press.Y) > 3)) _moved_ = true;
            if (_moved_)
            {
                var delta = ToWorld(_mouse) - _dragStartWorld;
                switch (_dragTarget)
                {
                    case BundleBox b:
                        b.Pos = _dragOrigin + delta;
                        _moved[b.Key] = b.Pos;
                        PlacePills();
                        PlaceLinks();
                        break;
                    case Pill p:
                        p.Manual = _dragOffsetOrigin + delta;
                        _pillMoved[p.Event.Key] = p.Manual;
                        PlacePills();
                        break;
                    default:
                        _offset = _dragOffsetOrigin + (_mouse - _press);
                        break;
                }
                InvalidateVisual();
                return;
            }
        }

        var hit = HitTest(_mouse);
        if (!ReferenceEquals(hit, _hover))
        {
            _hover = hit;
            Cursor = hit is null ? Cursor.Default : new Cursor(StandardCursorType.Hand);
            Status?.Invoke(StatusOf(hit));
            Animate();
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging && !_moved_)
        {
            // A click without a drag pins (or unpins) what it landed on; on the floor it clears the pin.
            var hit = HitTest(e.GetPosition(this));
            _pinned = hit is null || ReferenceEquals(hit, _pinned) ? null : hit;
            Animate();
            InvalidateVisual();
        }
        _dragging = false;
        _dragTarget = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        Animate();
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var at = e.GetPosition(this);
        var before = ToWorld(at);
        _scale = Math.Clamp(_scale * (e.Delta.Y > 0 ? 1.12 : 1 / 1.12), 0.05, 3);
        _offset = new Vector(at.X - before.X * _scale, at.Y - before.Y * _scale);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { _pinned = null; Animate(); InvalidateVisual(); }
        if (e.Key == Key.F) Fit();
    }

    /// Double-click: a bundle's or a shard's title opens or closes it, in place; anything else opens its source.
    private void Activate(object hit)
    {
        if (!_explode)
        {
            switch (hit)
            {
                case BundleBox b:
                    if (!_collapsed.Remove(b.Bundle.Name)) _collapsed.Add(b.Bundle.Name);
                    Reopen(b.Bundle.Name);
                    return;
                case UnitRow { Shard.Kind: "summary" } r:
                    _collapsed.Remove(r.Shard.Owner.Bundle.Name);
                    Reopen(r.Shard.Owner.Bundle.Name);
                    return;
            }
            ShardBox? shard = hit switch
            {
                ShardBox s when s.Kind is not ("boot" or "summary") && s.Owner.Bundle.Shards.Count > CollapseShardsInBundle => s,
                UnitRow { Shard.Collapsed: true } r => r.Shard,
                _ => null,
            };
            if (shard is not null)
            {
                string key = shard.Owner.Bundle.Name + "." + shard.Name;
                if (!_openShards.Remove(key)) _openShards.Add(key);
                Reopen(shard.Owner.Bundle.Name);
                return;
            }
        }
        Open(hit);
    }

    /// Rebuild with the view kept on the bundle that changed size, so it opens where the reader was looking.
    private void Reopen(string bundle)
    {
        var before = _bundles.FirstOrDefault(b => b.Bundle.Name == bundle)?.Pos;
        Rebuild(keepView: true);
        var after = _bundles.FirstOrDefault(b => b.Bundle.Name == bundle)?.Pos;
        if (before is { } a && after is { } z) _offset += (a - z) * _scale;
        _pinned = _bundles.FirstOrDefault(b => b.Bundle.Name == bundle);
        Animate();
        InvalidateVisual();
        BundlesChanged?.Invoke();
    }

    private void Open(object hit)
    {
        FlowPlace? at = hit switch
        {
            UnitRow r => r.At,
            ShardBox s => s.At,
            Pill p => p.From.Count > 0
                ? p.From[0].Emits.FirstOrDefault(x => x.Event == p.Event.Key)?.At ?? p.From[0].At
                : p.To.FirstOrDefault()?.At,
            BundleBox b => new FlowPlace(b.Bundle.File, 1),
            _ => null,
        };
        if (at is { File.Length: > 0 }) Navigate?.Invoke(at.File, at.Line);
    }

    private static string StatusOf(object? hit) => hit switch
    {
        Pill p => $"@{p.Event.Name} — {p.From.Count} emitter(s), {p.To.Count} hearer(s)",
        Link l => $"{l.From.Bundle.Name} → {l.To.Bundle.Name} — {LinkLabel(l)}",
        UnitRow r => $"{r.Shard.Owner.Bundle.Name}.{r.Shard.Name} — {r.Label}",
        ShardBox s => $"{s.Owner.Bundle.Name}.{s.Name}{(s.Collapsed ? " — double-click to open" : "")}",
        BundleBox b => $"{b.Bundle.Name} — {(b.Collapsed ? "double-click to open" : "double-click to collapse")}",
        _ => "",
    };

    // ---- text ----------------------------------------------------------------------------------

    private static FormattedText Format(string text, double size, Typeface face, IBrush brush, FontWeight weight = FontWeight.Normal) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(face.FontFamily, FontStyle.Normal, weight), size, brush);

    private static double Measure(string text, double size, Typeface face) => Format(text, size, face, Brushes.White).Width;

    private void Text(DrawingContext ctx, string text, Point at, double size, IBrush brush, Typeface face, FontWeight weight = FontWeight.Normal) =>
        ctx.DrawText(Format(text, size, face, Br(brush), weight), at);

    private void TextRight(DrawingContext ctx, string text, Point rightTop, double size, IBrush brush, Typeface face)
    {
        var f = Format(text, size, face, Br(brush));
        ctx.DrawText(f, new Point(rightTop.X - f.Width, rightTop.Y));
    }
}
