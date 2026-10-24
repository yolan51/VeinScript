using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// The data the Workbench's Flow tab draws and `veinc flow` prints. Each test pins an edge the runtime
// actually delivers — the map is only worth looking at if a wire on it is a wire at runtime.
public class EventFlowTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with stdlib/ not found");
    }

    private static EventFlow Flow(params string[] path) =>
        EventFlow.For(Path.Combine(new[] { RepoRoot(), "samples" }.Concat(path).ToArray()));

    [Fact]
    public void A_bare_emit_meets_a_qualified_hear_in_another_bundle()
    {
        // Store writes `emit @Ordered`, Audit writes `hear *you.Store.Orders.@Ordered`. By the name as
        // written those are two strings; by routing key they are one event — which is what AppComposition
        // got wrong and the reason this reads the lowered app.
        var flow = Flow("app_capabilities", "shop.app.vein");
        var ordered = flow.Event("you.Store.Ordered");

        Assert.NotNull(ordered);
        Assert.Contains(ordered!.Emitters, e => e.Shard == "start");
        Assert.Contains(ordered.Hearers, h => h.Bundle == "Audit" && h.Shard == "Ledger");
        Assert.Contains(ordered.Hearers, h => h.Bundle == "Store" && h.Shard == "Fulfilment");
    }

    [Fact]
    public void Two_vendors_events_that_share_a_word_stay_two()
    {
        var flow = Flow("bundle_collisions", "bundle_collisions.app.vein");
        Assert.NotNull(flow.Event("acme.Radio.Dispatch"));
        Assert.NotNull(flow.Event("zenith.Courier.Dispatch"));
    }

    [Fact]
    public void The_sample_has_seven_bundles_and_its_two_deliberate_dead_ends()
    {
        var flow = Flow("event_flow", "event_flow.app.vein");

        Assert.Empty(flow.Problems);
        Assert.Equal(new[] { "Game", "Combat", "Loot", "Inventory", "Quests", "Hud", "Telemetry" }, flow.Bundles.Select(b => b.Name));
        Assert.True(flow.Bundles[0].IsPrincipal);
        Assert.Equal("you.Game.Begin", flow.Start?.Event);

        // Emitted, heard by nobody — and heard, emitted by nobody.
        Assert.True(flow.Event("you.Telemetry.Logged")!.Unheard);
        Assert.True(flow.Event("you.Combat.Healed")!.NeverEmitted);
        Assert.Equal(2, flow.Events.Count(e => e.Unheard || e.NeverEmitted));
    }

    [Fact]
    public void Each_end_knows_where_it_is_and_who_it_admits()
    {
        var flow = Flow("event_flow", "event_flow.app.vein");
        var damaged = flow.Event("you.Combat.Damaged")!;

        var from = Assert.Single(damaged.Emitters);
        Assert.Equal(("Combat", "Resolver"), (from.Bundle, from.Shard));
        Assert.Equal("Combat.vein", Path.GetFileName(from.At.File));
        Assert.Contains("emit @Damaged", File.ReadAllLines(from.At.File)[from.At.Line - 1]);

        Assert.Equal(new[] { "Game.Director", "Hud.Screen", "Telemetry.Recorder" },
                     damaged.Hearers.Select(h => h.Bundle + "." + h.Shard).OrderBy(x => x));
        Assert.Equal("audience !Resolver", damaged.Hearers.Single(h => h.Bundle == "Hud").Detail);
    }

    [Fact]
    public void What_an_SF_emits_its_caller_emits()
    {
        // Hud prints through `do show(…)`; the @Print inside the SF belongs to every unit that calls it.
        var flow = Flow("event_flow", "event_flow.app.vein");
        var screen = flow.Shards.Single(s => s.Bundle == "Hud" && s.Name == "Screen");

        var once = screen.Units.Single(u => u.Trigger == "once");
        Assert.False(once.IsHear);
        var print = Assert.Single(once.Emits);
        Assert.Equal("Print", print.Event);
        Assert.Equal("show()", print.Via);
        Assert.True(flow.Event("Print")!.IsHost);
    }

    [Fact]
    public void A_bundle_whose_needs_are_installed_links_them_like_run_does()
    {
        // kitdemo's game names no manifest: `need "kit.Movement"` links the kit, so its Drift shard is on
        // the map — the same set of shards `veinc run game.vein` runs.
        var flow = Flow("kitdemo", "game.vein");
        Assert.Contains(flow.Shards, s => s.Bundle == "Movement" && s.Name == "Drift");
        Assert.True(flow.Bundles.Single(b => b.Name == "Movement").Needed);
    }
}
