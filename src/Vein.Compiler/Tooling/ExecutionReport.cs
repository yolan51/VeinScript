using System.Text;

namespace Vein.Compiler.Tooling;

// Presentation for ExecutionModel: the glyph vocabulary, the `veinc exec` text layout, and the --json shape.
// Split from the model because the text layout is CLI-only while the model is also read by the Workbench,
// which draws its own controls from the same records.
//
// Unicode is the default; --ascii swaps the glyph table and nothing else, so the two renderings stay
// line-for-line comparable.
public static class ExecutionReport
{
    // Separators are part of the table too, so --ascii output is guaranteed pure ASCII end to end.
    public sealed record Glyphs(
        string Event, string Reactive, string Scheduled, string Frame, string Continuous,
        string Parallel, string Synchronized, string Conflict, string Ordered, string Deferred, char Bar,
        string Dash, string Dot, string Cross, string Ellipsis);

    public static readonly Glyphs Unicode =
        new("◆", "◉", "◷", "▣", "∞", "⚡", "🔒", "!", "→", "~", '█', "—", "·", "✕", "…");

    // `%%` reads as a mutex and, unlike `#` or `*`, is not a VeinScript sigil.
    public static readonly Glyphs Ascii =
        new("<>", "()", "/\\", "[]", "oo", "||", "%%", "!", "->", "~", '#', "-", ".", "x", "...");

    public static string ClassGlyph(ExecClass c, Glyphs g) => c switch
    {
        ExecClass.Event => g.Event,
        ExecClass.Reactive => g.Reactive,
        ExecClass.Scheduled => g.Scheduled,
        ExecClass.Frame => g.Frame,
        _ => g.Continuous
    };

    /// "▣ ⚡ →" — the class glyph followed by every modifier that applies.
    public static string Badge(ExecUnit u, Glyphs g)
    {
        var parts = new List<string> { ClassGlyph(u.Class, g) };
        if (u.Has(ExecModifier.Parallel)) parts.Add(g.Parallel);
        if (u.Has(ExecModifier.Synchronized)) parts.Add(g.Synchronized);
        if (u.Has(ExecModifier.Conflict)) parts.Add(g.Conflict);
        if (u.Has(ExecModifier.Ordered)) parts.Add(g.Ordered);
        if (u.Has(ExecModifier.Deferred)) parts.Add(g.Deferred);
        return string.Join(" ", parts);
    }

    private static readonly (ExecClass Class, string Name)[] Order =
    {
        (ExecClass.Event, "Event"), (ExecClass.Reactive, "Reactive"), (ExecClass.Scheduled, "Scheduled"),
        (ExecClass.Frame, "Frame"), (ExecClass.Continuous, "Continuous")
    };

    public static string Render(ExecutionModel m, bool ascii = false)
    {
        var g = ascii ? Ascii : Unicode;
        var sb = new StringBuilder();
        var t = m.Totals;

        sb.AppendLine($"Execution model {g.Dash} bundle {m.Bundle}");
        sb.AppendLine($"  {t.Units} unit(s) {g.Dot} {t.WaveCount} wave(s)");
        sb.AppendLine();

        if (t.Units == 0)
        {
            sb.AppendLine("  No trigger blocks — nothing in this bundle runs.");
            return sb.ToString();
        }

        int max = Order.Max(o => t.ByClass[o.Class]);
        foreach (var (cls, name) in Order)
        {
            int n = t.ByClass[cls];
            string bar = n == 0 ? "" : new string(g.Bar, Math.Max(1, (int)Math.Round(n / (double)max * 12)));
            sb.AppendLine($"  {ClassGlyph(cls, g),-2} {name,-11} {n,3}  {bar}".TrimEnd());
        }
        sb.AppendLine();
        sb.AppendLine($"  parallel opportunities {t.ParallelOpportunities,4}       dependency barriers {t.DependencyBarriers,4}");
        sb.AppendLine($"  units in conflict      {t.ConflictingUnits,4}       always running      {t.AlwaysRunning,4}");
        sb.AppendLine($"  ordered edges          {t.OrderedEdges,4}       cycles              {t.CycleCount,4}");
        sb.AppendLine();

        foreach (var o in m.Owners)
        {
            string kind = o.Kind switch { OwnerKind.Shard => "shard", OwnerKind.ShardView => "ShardView", _ => "bridge" };
            string rollup = ClassGlyph(o.Class, g) + (o.Mixed ? "+" : "");
            sb.AppendLine($"{kind} {o.Name}".PadRight(60) + rollup);

            foreach (var u in m.UnitsOf(o.Name))
            {
                sb.AppendLine($"  {Badge(u, g),-14} {u.Trigger}".PadRight(58) + $"wave {u.Wave}"
                              + (u.InCycle ? "  (in cycle)" : ""));
                // A fold only governs writes, so annotating it on a read would be noise.
                Line("match", u.Matches.Select(r => r.Resource));
                Line("reads", u.Reads.Select(r => r.Resource));
                Line("writes", u.Writes.Select(r => r.Display));
                Line("payload", u.PayloadReads.Select(r => r.Resource));
                Line("emits", u.Emits.Select(e => "@" + e));
                Line("brings", u.Brings);

                void Line(string label, IEnumerable<string> items)
                {
                    var list = items.ToList();
                    if (list.Count > 0) sb.AppendLine($"      {label,-8} {string.Join("  ", list)}");
                }
            }
            sb.AppendLine();
        }

        if (m.Conflicts.Count > 0)
        {
            sb.AppendLine("conflicts");
            foreach (var c in m.Conflicts)
                sb.AppendLine($"  {(c.Resolvable ? g.Synchronized : g.Conflict),-2} {Label(m, c.A, g)} {g.Cross} {Label(m, c.B, g)}"
                              + $"   {c.Resource.Resource}   ({c.Why})");
            sb.AppendLine();
        }

        if (m.Cycles.Count > 0)
        {
            sb.AppendLine("cycles");
            foreach (var c in m.Cycles)
                sb.AppendLine("  " + string.Join($" {g.Ordered} ", c.Select(id => Label(m, id, g)))
                              + $" {g.Ordered} {g.Ellipsis}");
            sb.AppendLine();
        }

        sb.AppendLine("waves");
        for (int w = 0; w < m.Waves.Count; w++)
            sb.AppendLine($"  {w}   {string.Join("   ", m.Waves[w].Select(id => Label(m, id, g)))}");

        return sb.ToString();
    }

    private static string Label(ExecutionModel m, string id, Glyphs g) =>
        m.Unit(id) is { } u ? $"{u.Owner}{g.Dot}{u.Trigger}" : id;

    /// The --json shape, in the style of `veinc symbols`.
    public static object Json(ExecutionModel m) => new
    {
        bundle = m.Bundle,
        units = m.Units.Select(u => new
        {
            id = u.Id,
            owner = u.Owner,
            ownerKind = u.Kind.ToString(),
            @class = u.Class.ToString(),
            trigger = u.Trigger,
            triggerKey = u.TriggerKey,
            phaseRank = u.PhaseRank,
            intervalSeconds = u.IntervalSeconds,
            hearEvent = u.HearEvent,
            wave = u.Wave,
            inCycle = u.InCycle,
            modifiers = Flags(u.Modifiers),
            matches = u.Matches.Select(Ref),
            reads = u.Reads.Select(Ref),
            writes = u.Writes.Select(Ref),
            payloadReads = u.PayloadReads.Select(Ref),
            emits = u.Emits,
            brings = u.Brings
        }),
        edges = m.Edges.Select(e => new { from = e.From, to = e.To, kind = e.Kind.ToString(), reason = e.Reason }),
        conflicts = m.Conflicts.Select(c => new
        {
            a = c.A, b = c.B, resource = Ref(c.Resource), resolvable = c.Resolvable, why = c.Why
        }),
        owners = m.Owners.Select(o => new
        {
            name = o.Name, kind = o.Kind.ToString(), @class = o.Class.ToString(), mixed = o.Mixed,
            units = o.UnitIds, firstWave = o.FirstWave, lastWave = o.LastWave
        }),
        waves = m.Waves,
        cycles = m.Cycles,
        totals = new
        {
            units = m.Totals.Units,
            byClass = m.Totals.ByClass.ToDictionary(k => k.Key.ToString(), k => k.Value),
            parallelOpportunities = m.Totals.ParallelOpportunities,
            orderedEdges = m.Totals.OrderedEdges,
            dependencyBarriers = m.Totals.DependencyBarriers,
            conflictingUnits = m.Totals.ConflictingUnits,
            conflictPairs = m.Conflicts.Count,
            alwaysRunning = m.Totals.AlwaysRunning,
            waveCount = m.Totals.WaveCount,
            cycleCount = m.Totals.CycleCount
        }
    };

    private static object Ref(StateRef r) => new
    {
        @ref = r.Resource, kind = r.Kind.ToString(), fold = r.Fold, remove = r.Remove
    };

    private static string[] Flags(ExecModifier m) =>
        Enum.GetValues<ExecModifier>()
            .Where(f => f != ExecModifier.None && (m & f) != 0)
            .Select(f => f.ToString())
            .ToArray();
}
