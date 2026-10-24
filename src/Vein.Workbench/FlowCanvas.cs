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
// Laid out left to right by how far an event travels from the principal — longest path, with the edges
// that close a cycle set aside first, so the turn loop in samples/event_flow reads as one wire running
// back rather than as a layout that gave up. Then it is the reader's: drag a bundle by its title, drag a
// pill, pan the empty floor, wheel to zoom. Hover says who emits and who hears; a click pins that
// highlight so it survives moving to the editor; a double-click opens the source.
internal sealed class FlowCanvas : Control
{
    // ---- geometry, in world units --------------------------------------------------------------
    private const double BoxW = 250, BundleHead = 30, ShardHead = 22, RowH = 19, Pad = 8, ShardGap = 6;
    private const double ColGap = 200, BundleGap = 36, PillH = 22, PillGap = 24;

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
        public Point Pos;
        public double H;
        public int Col;
        public readonly List<ShardBox> Shards = new();
        public Rect Bounds => new(Pos, new Size(BoxW, H));
        public Rect Head => new(Pos, new Size(BoxW, BundleHead));
    }

    private sealed class ShardBox
    {
        public required BundleBox Owner;
        public required string Name;
        public string Kind = "shard";
        public string Carries = "";
        public FlowPlace? At;
        public double Top;                                  // relative to the bundle
        public readonly List<UnitRow> Rows = new();
        public double HeadH => Kind == "boot" ? 2 : ShardHead;   // the boot row needs no title of its own
        public double H => HeadH + Rows.Count * RowH + 4;
        public Rect Bounds => new(Owner.Pos.X + Pad, Owner.Pos.Y + Top, BoxW - 2 * Pad, H);
        public Rect Head => new(Bounds.X, Bounds.Y, Bounds.Width, HeadH);
    }

    private sealed class UnitRow
    {
        public required ShardBox Shard;
        public required string Label;
        public string? Hears;                               // event key, for a hear row
        public string Audience = "";
        public List<FlowEmit> Emits = new();
        public FlowPlace? At;
        public int Index;
        public Rect Bounds => new(Shard.Bounds.X, Shard.Bounds.Y + Shard.HeadH + Index * RowH, Shard.Bounds.Width, RowH);
        public Point Right => new(Shard.Owner.Pos.X + BoxW, Bounds.Center.Y);
        public Point Left => new(Shard.Owner.Pos.X, Bounds.Center.Y);
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

    private EventFlow? _flow;
    private readonly List<BundleBox> _bundles = new();
    private readonly List<Pill> _pills = new();
    private readonly Dictionary<string, Point> _moved = new(StringComparer.Ordinal);       // bundle → where the reader put it
    private readonly Dictionary<string, Vector> _pillMoved = new(StringComparer.Ordinal);  // event → nudge

    // ---- view ---------------------------------------------------------------------------------
    private double _scale = 1;
    private Vector _offset = new(20, 20);
    private bool _fitted;

    private object? _hover;      // Pill, UnitRow, ShardBox or BundleBox
    private object? _pinned;
    private string _filter = "";
    private bool _showStdlib;

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

    public string Filter
    {
        get => _filter;
        set { _filter = value.Trim(); Animate(); InvalidateVisual(); }
    }

    public void Show(EventFlow flow)
    {
        bool first = _flow is null || _flow.Name != flow.Name;
        _flow = flow;
        if (first) { _moved.Clear(); _pillMoved.Clear(); _fitted = false; _pinned = null; }
        Rebuild(keepView: !first);
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
        _scale = Math.Clamp(Math.Min(Bounds.Width / world.Width, Bounds.Height / world.Height), 0.25, 1.4);
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
        _hover = null;
        if (_pinned is not null) _pinned = null;
        if (_flow is null) { InvalidateVisual(); return; }

        var rows = new List<UnitRow>();
        foreach (var b in _flow.Bundles)
        {
            var box = new BundleBox { Bundle = b };
            double y = BundleHead;

            // The boot event, as a row of its own at the top of the principal: it is the one emit no
            // shard makes, and without it the map has no visible beginning.
            if (b.IsPrincipal && _flow.Start is { } start)
            {
                var boot = new ShardBox { Owner = box, Name = "start", Kind = "boot", At = start.At, Top = y };
                var row = new UnitRow
                {
                    Shard = boot, Label = "▶ start @" + Compiler.Ir.Interp.EventName(start.Event), At = start.At,
                    Emits = new List<FlowEmit> { new(start.Event, start.At, null) },
                };
                boot.Rows.Add(row);
                box.Shards.Add(boot);
                rows.Add(row);
                y += boot.H + ShardGap;
            }

            foreach (var s in b.Shards)
            {
                var shard = new ShardBox { Owner = box, Name = s.Name, Kind = s.Kind, Carries = s.Carries, At = s.At, Top = y };
                foreach (var u in s.Units)
                {
                    var row = new UnitRow
                    {
                        Shard = shard, Index = shard.Rows.Count, At = u.At, Audience = u.Audience, Emits = u.Emits.ToList(),
                        Hears = u.IsHear ? u.Trigger : null,
                        Label = u.IsHear ? "hear @" + Compiler.Ir.Interp.EventName(u.Trigger) : "◷ " + ScheduleLabel(u.Trigger),
                    };
                    shard.Rows.Add(row);
                    rows.Add(row);
                }
                box.Shards.Add(shard);
                y += shard.H + ShardGap;
            }
            box.H = Math.Max(y + Pad - ShardGap, BundleHead + 24);
            _bundles.Add(box);
        }

        foreach (var e in _flow.Events.Where(e => Shown(e.Key)))
        {
            var pill = new Pill { Event = e, Hue = e.Unheard || e.NeverEmitted ? Defect : HueOf(e.Key) };
            pill.From.AddRange(rows.Where(r => r.Emits.Any(x => x.Event == e.Key)));
            pill.To.AddRange(rows.Where(r => r.Hears == e.Key));
            pill.W = Measure("@" + e.Name, 12, Mono) + 22;
            if (_pillMoved.TryGetValue(e.Key, out var nudge)) pill.Manual = nudge;
            _pills.Add(pill);
        }

        Layout();
        if (!keepView || !_fitted) Dispatcher.UIThread.Post(Fit, DispatcherPriority.Background);
        InvalidateVisual();
    }

    private static string ScheduleLabel(string trigger) => trigger switch
    {
        "once" => "run once",
        "tick" => "each tick",
        "frame" => "each frame",
        "every" => "every …",
        _ => trigger,
    };

    private void Layout()
    {
        // Bundle-level edges: an event emitted in A and heard in B.
        var index = _bundles.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);
        var succ = _bundles.Select(_ => new HashSet<int>()).ToArray();
        foreach (var p in _pills)
            foreach (var f in p.From)
                foreach (var t in p.To)
                {
                    int a = index[f.Shard.Owner], b = index[t.Shard.Owner];
                    if (a != b) succ[a].Add(b);
                }

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

        var col = new int[_bundles.Count];
        bool changed = true;
        for (int pass = 0; changed && pass < _bundles.Count + 1; pass++)
        {
            changed = false;
            for (int v = 0; v < _bundles.Count; v++)
                foreach (int w in dag[v])
                    if (col[w] < col[v] + 1) { col[w] = col[v] + 1; changed = true; }
        }

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
                box.Pos = new Point(col[i] * (BoxW + ColGap), y);
                centre[i] = y + box.H / 2;
                y += box.H + BundleGap;
            }
        }

        foreach (var box in _bundles)
            if (_moved.TryGetValue(box.Bundle.Name, out var at)) box.Pos = at;

        PlacePills();
    }

    /// A pill sits just outside the bundle that emits it — an event LEAVES a bundle — or, when nothing
    /// emits it, just before the first bundle that hears it. Then pills in the same gutter are pushed
    /// apart so none covers another.
    private void PlacePills()
    {
        foreach (var p in _pills)
        {
            if (p.From.Count > 0)
            {
                var a = p.From[0];
                p.Pos = new Point(a.Shard.Owner.Pos.X + BoxW + PillGap, a.Bounds.Center.Y - PillH / 2);
            }
            else if (p.To.Count > 0)
            {
                var a = p.To[0];
                p.Pos = new Point(a.Shard.Owner.Pos.X - PillGap - p.W, a.Bounds.Center.Y - PillH / 2);
            }
            p.Pos += p.Manual;
        }

        var ordered = _pills.OrderBy(p => p.Pos.Y).ToList();
        for (int i = 0; i < ordered.Count; i++)
            for (int j = 0; j < i; j++)
            {
                var a = ordered[j].Bounds.Inflate(new Thickness(0, 3));
                var b = ordered[i].Bounds;
                if (a.Intersects(b)) ordered[i].Pos = new Point(b.X, a.Bottom + 1);
            }
    }

    private static Color HueOf(string key)
    {
        uint h = 2166136261;
        foreach (char c in key) h = (h ^ c) * 16777619;
        return Hues[h % (uint)Hues.Length];
    }

    // ---- what is lit ---------------------------------------------------------------------------

    /// The pills and rows the current focus touches — hover first, then a pinned click, then the filter.
    /// Null means nothing is focused and everything draws at full strength.
    private (HashSet<Pill> Pills, HashSet<UnitRow> Rows)? Lit()
    {
        var focus = _hover ?? _pinned;
        var pills = new HashSet<Pill>();
        var rows = new HashSet<UnitRow>();

        void LightPill(Pill p) { pills.Add(p); rows.UnionWith(p.From); rows.UnionWith(p.To); }
        void LightRow(UnitRow r)
        {
            rows.Add(r);
            foreach (var p in _pills.Where(p => p.From.Contains(r) || p.To.Contains(r))) { pills.Add(p); }
        }

        switch (focus)
        {
            case Pill p: LightPill(p); break;
            case UnitRow r: LightRow(r); break;
            case ShardBox s: foreach (var r in s.Rows) LightRow(r); break;
            case BundleBox b:
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
                break;
            default:
                if (_filter.Length == 0) return null;
                foreach (var p in _pills.Where(p => Has(p.Event.Name) || Has(p.Event.Key))) LightPill(p);
                foreach (var r in _bundles.SelectMany(b => b.Shards).SelectMany(s => s.Rows)
                                          .Where(r => Has(r.Shard.Name) || Has(r.Shard.Owner.Bundle.Name)))
                    LightRow(r);
                break;
        }
        return (pills, rows);

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

    public override void Render(DrawingContext ctx)
    {
        var ground = Palette.Sunken;
        ctx.FillRectangle(ground, new Rect(Bounds.Size));

        if (_flow is null || _bundles.Count == 0)
        {
            Text(ctx, _flow is null ? "Open a program to see its event flow." : "No shards — nothing emits or hears anything here.",
                new Point(16, 16), 13, Palette.Muted, Ui);
            return;
        }

        var lit = Lit();

        using (ctx.PushTransform(Matrix.CreateScale(_scale, _scale) * Matrix.CreateTranslation(_offset.X, _offset.Y)))
        {
            DrawGrid(ctx);
            foreach (var b in _bundles) DrawBundle(ctx, b, lit);
            foreach (var p in _pills) DrawWires(ctx, p, lit);
            foreach (var p in _pills) DrawPill(ctx, p, lit);
        }

        if ((_hover ?? _pinned) is { } focus) DrawTip(ctx, focus);
    }

    private void DrawGrid(DrawingContext ctx)
    {
        var view = new Rect(-_offset.X / _scale, -_offset.Y / _scale, Bounds.Width / _scale, Bounds.Height / _scale);
        var pen = new Pen(Br(Color.FromArgb(18, 120, 150, 255)), 1 / _scale);
        const double step = 40;
        for (double x = Math.Floor(view.X / step) * step; x < view.Right; x += step) ctx.DrawLine(pen, new Point(x, view.Y), new Point(x, view.Bottom));
        for (double y = Math.Floor(view.Y / step) * step; y < view.Bottom; y += step) ctx.DrawLine(pen, new Point(view.X, y), new Point(view.Right, y));
    }

    private void DrawBundle(DrawingContext ctx, BundleBox b, (HashSet<Pill> Pills, HashSet<UnitRow> Rows)? lit)
    {
        bool dim = lit is { } l && !b.Shards.SelectMany(s => s.Rows).Any(l.Rows.Contains) && _hover != b && _pinned != b;
        using var _ = Fade(dim ? 0.35 : 1);

        var accent = b.Bundle.IsPrincipal ? Color.Parse("#7C9CFF") : b.Bundle.Needed ? Color.Parse("#5E6B85") : Color.Parse("#6A5ACD");
        bool focused = _hover == b || _pinned == b;
        // A soft halo, then the box — the "glow" of the reference picture, kept quiet enough to read on.
        ctx.DrawRectangle(null, new Pen(Br(accent, focused ? 0.45 : 0.18), 6), b.Bounds.Inflate(2), 10, 10);
        ctx.DrawRectangle(Br(Color.Parse("#161B29")),
            new Pen(Br(accent), focused ? 2 : 1.2, b.Bundle.Needed ? new DashStyle(new double[] { 4, 3 }, 0) : null),
            b.Bounds, 8, 8);
        ctx.DrawRectangle(Br(accent, 0.22), null, new Rect(b.Pos, new Size(BoxW, BundleHead)), 8, 8);

        string mark = b.Bundle.IsPrincipal ? "★ " : b.Bundle.Needed ? "◇ " : "■ ";
        Text(ctx, mark + b.Bundle.Name, b.Pos + new Point(10, 7), 13, Palette.Ink, Ui, FontWeight.SemiBold);
        if (b.Bundle.Author is { } author)
            TextRight(ctx, author, new Point(b.Pos.X + BoxW - 10, b.Pos.Y + 9), 11, Palette.Muted, Ui);

        foreach (var s in b.Shards) DrawShard(ctx, s, lit);
    }

    private void DrawShard(DrawingContext ctx, ShardBox s, (HashSet<Pill> Pills, HashSet<UnitRow> Rows)? lit)
    {
        bool focused = _hover == s || _pinned == s;
        var border = s.Kind == "boot" ? Color.Parse("#7C9CFF") : Color.Parse("#3A4462");
        ctx.DrawRectangle(Br(Color.Parse("#1D2336")), new Pen(Br(border), focused ? 1.8 : 1), s.Bounds, 5, 5);
        if (s.Kind != "boot")
        {
            Text(ctx, s.Name, new Point(s.Bounds.X + 8, s.Bounds.Y + 4), 12, Palette.Ink, Mono, FontWeight.SemiBold);
            string tag = s.Kind == "shard" ? s.Carries : (s.Kind + " " + s.Carries).Trim();
            if (tag.Length > 0) TextRight(ctx, tag, new Point(s.Bounds.Right - 8, s.Bounds.Y + 5), 10, Palette.Muted, Mono);
        }

        foreach (var r in s.Rows)
        {
            var rb = r.Bounds;
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

    private void DrawWires(DrawingContext ctx, Pill p, (HashSet<Pill> Pills, HashSet<UnitRow> Rows)? lit)
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
            Wire(ctx, right ? f.Right : f.Left, right ? +1 : -1,
                 right ? new Point(pb.Left, pb.Center.Y) : new Point(pb.Right, pb.Center.Y), right ? +1 : -1,
                 p.Hue, alpha, hot, arrow: false);
        }

        foreach (var t in p.To)
        {
            // Enter the row from the side that faces the pill, so a wire back into the same bundle (or
            // into an earlier column) does not cut across the box.
            bool fromLeft = t.Left.X > pb.Center.X;
            Wire(ctx, fromLeft ? new Point(pb.Right, pb.Center.Y) : new Point(pb.Left, pb.Center.Y), fromLeft ? +1 : -1,
                 fromLeft ? t.Left : t.Right, fromLeft ? +1 : -1,
                 p.Hue, alpha, hot, arrow: true);
        }

        // Emitted and heard by nobody: a stub that ends in nothing, which is exactly what happens.
        if (p.Event.Unheard)
        {
            var a = new Point(pb.Right, pb.Center.Y);
            ctx.DrawLine(new Pen(Br(Defect, alpha), 1.6, new DashStyle(new double[] { 2, 2 }, 0)), a, a + new Point(36, 0));
            Text(ctx, "✕", a + new Point(38, -8), 12, Br(Defect, alpha), Ui);
        }
    }

    /// A curve leaving `a` heading `aOut` (+1 right, −1 left) and arriving at `b` heading `bIn`.
    private void Wire(DrawingContext ctx, Point a, int aOut, Point b, int bIn, Color hue, double alpha, bool hot, bool arrow)
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

        ctx.DrawGeometry(null, new Pen(Br(hue, 0.16 * alpha), hot ? 9 : 6), geo);
        ctx.DrawGeometry(null, new Pen(Br(hue, 0.95 * alpha), hot ? 2.2 : 1.4), geo);
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

    private void DrawPill(DrawingContext ctx, Pill p, (HashSet<Pill> Pills, HashSet<UnitRow> Rows)? lit)
    {
        bool on = lit?.Pills.Contains(p) ?? true;
        bool focused = _hover == p || _pinned == p;
        using var _ = Fade(on ? 1 : 0.25);
        var r = p.Bounds;
        ctx.DrawRectangle(null, new Pen(Br(p.Hue, focused ? 0.5 : 0.22), 6), r.Inflate(1.5), PillH / 2, PillH / 2);
        ctx.DrawRectangle(Br(Color.Parse("#141826")), new Pen(Br(p.Hue), focused ? 2 : 1.3), r, PillH / 2, PillH / 2);
        Text(ctx, "@" + p.Event.Name, new Point(r.X + 11, r.Y + 3), 12, Br(p.Defect ? Defect : Colors.White), Mono);
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
        string Who(UnitRow r) => r.Shard.Kind == "boot" ? r.Shard.Owner.Bundle.Name + " · start" : r.Shard.Owner.Bundle.Name + "." + r.Shard.Name;

        switch (focus)
        {
            case Pill p:
                var e = p.Event;
                lines.Add(new("@" + e.Name + (e.Owner is null ? "   (standard library)" : "   " + e.Owner), Head: true));
                lines.Add(new("emitted by"));
                if (p.From.Count == 0) lines.Add(new(e.IsRuntime ? "  the runtime" : "  nobody — these handlers never run", Bad: e.NeverEmitted));
                foreach (var f in p.From)
                    foreach (var x in f.Emits.Where(x => x.Event == e.Key).DistinctBy(x => (x.At.Line, x.Via)))
                        lines.Add(new($"  {Who(f)} — {f.Label}{(x.Via is null ? "" : "  via " + x.Via)}   {Where(x.At)}"));
                lines.Add(new("heard by"));
                if (p.To.Count == 0) lines.Add(new(e.IsHost ? "  the host" : "  nobody — this event goes nowhere", Bad: e.Unheard));
                foreach (var t in p.To)
                    lines.Add(new($"  {Who(t)}{(t.Audience.Length > 0 ? "  [" + t.Audience + "]" : "")}   {Where(t.At)}"));
                lines.Add(new("double-click to open the " + (p.From.Count > 0 ? "first emit" : "first hear")));
                break;

            case UnitRow r:
                lines.Add(new($"{Who(r)} — {r.Label}", Head: true));
                if (r.Audience.Length > 0) lines.Add(new("  " + r.Audience));
                if (r.Hears is { } k && _flow!.Event(k) is { } he)
                    lines.Add(he.Emitters.Count == 0
                        ? new(he.IsRuntime ? "  raised by the runtime" : "  nothing emits this — the handler never runs", Bad: he.NeverEmitted)
                        : new("  from " + string.Join(", ", he.Emitters.Select(x => x.Bundle + "." + x.Shard))));
                var outs = r.Emits.Where(x => Shown(x.Event)).GroupBy(x => x.Event).ToList();
                if (outs.Count > 0)
                {
                    lines.Add(new("emits"));
                    foreach (var g in outs)
                    {
                        var ev = _flow!.Event(g.Key);
                        string to = ev is null || ev.Hearers.Count == 0 ? "nobody" : string.Join(", ", ev.Hearers.Select(x => x.Bundle + "." + x.Shard));
                        lines.Add(new($"  @{Compiler.Ir.Interp.EventName(g.Key)} → {to}   {Where(g.First().At)}", Bad: ev?.Unheard == true));
                    }
                }
                lines.Add(new($"{Where(r.At)} · double-click to open"));
                break;

            case ShardBox s:
                lines.Add(new($"{s.Owner.Bundle.Name}.{s.Name}{(s.Carries.Length > 0 ? "  " + s.Carries : "")}", Head: true));
                lines.Add(new($"  hears {s.Rows.Count(r => r.Hears is not null)} · schedules {s.Rows.Count(r => r.Hears is null)} · emits " +
                              s.Rows.SelectMany(r => r.Emits).Select(x => x.Event).Where(Shown).Distinct().Count()));
                lines.Add(new($"{Where(s.At)} · double-click to open"));
                break;

            case BundleBox b:
                lines.Add(new($"{(b.Bundle.Author is null ? "" : b.Bundle.Author + ".")}{b.Bundle.Name}   " +
                              (b.Bundle.IsPrincipal ? "principal — boots the app" : b.Bundle.Needed ? "linked through a need" : "capability"), Head: true));
                var ins = _pills.Where(p => p.To.Any(r => r.Shard.Owner == b) && p.From.Any(r => r.Shard.Owner != b)).ToList();
                var exits = _pills.Where(p => p.From.Any(r => r.Shard.Owner == b) && (p.To.Count == 0 || p.To.Any(r => r.Shard.Owner != b))).ToList();
                var inside = _pills.Where(p => p.From.Count > 0 && p.From.All(r => r.Shard.Owner == b) && p.To.Count > 0 && p.To.All(r => r.Shard.Owner == b)).ToList();
                if (ins.Count > 0) lines.Add(new("in"));
                foreach (var p in ins)
                    lines.Add(new($"  @{p.Event.Name}  ← {string.Join(", ", p.From.Where(r => r.Shard.Owner != b).Select(r => r.Shard.Owner.Bundle.Name).Distinct())}"));
                if (exits.Count > 0) lines.Add(new("out"));
                foreach (var p in exits)
                {
                    var to = p.To.Where(r => r.Shard.Owner != b).Select(r => r.Shard.Owner.Bundle.Name).Distinct().ToList();
                    lines.Add(new($"  @{p.Event.Name}  → {(to.Count == 0 ? "nobody" : string.Join(", ", to))}", Bad: p.Event.Unheard));
                }
                if (inside.Count > 0) lines.Add(new("inside  " + string.Join(", ", inside.Select(p => "@" + p.Event.Name))));
                if (ins.Count + exits.Count + inside.Count == 0) lines.Add(new("  nothing crosses this bundle"));
                break;
        }
        return lines;
    }

    // ---- input ---------------------------------------------------------------------------------

    private Point ToWorld(Point screen) => new((screen.X - _offset.X) / _scale, (screen.Y - _offset.Y) / _scale);

    private object? HitTest(Point screen)
    {
        var w = ToWorld(screen);
        foreach (var p in Enumerable.Reverse(_pills)) if (p.Bounds.Contains(w)) return p;
        foreach (var b in _bundles)
        {
            if (!b.Bounds.Contains(w)) continue;
            if (b.Head.Contains(w)) return b;
            foreach (var s in b.Shards)
            {
                foreach (var r in s.Rows) if (r.Bounds.Contains(w)) return r;
                if (s.Bounds.Contains(w)) return s;
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
            Open(hit);
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
                        _moved[b.Bundle.Name] = b.Pos;
                        PlacePills();
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
        _scale = Math.Clamp(_scale * (e.Delta.Y > 0 ? 1.12 : 1 / 1.12), 0.2, 3);
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
        UnitRow r => $"{r.Shard.Owner.Bundle.Name}.{r.Shard.Name} — {r.Label}",
        ShardBox s => $"{s.Owner.Bundle.Name}.{s.Name}",
        BundleBox b => $"{b.Bundle.Name} — what goes in and out",
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
