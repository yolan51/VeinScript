using Vein.Hosting;

namespace Vein.Coop;

/// What a co-op session shares and how often. The defaults suit a small action game; a game changes them
/// in `vein.settings` through net.Coop's `Share` settings (FromSettings), or a host passes its own.
public sealed record VeinCoopOptions
{
    /// What every mirror carries: its look and its place. `$Seat` says whose an identity is.
    public static readonly IReadOnlyList<string> Looks =
        new[] { "Position", "Rotation", "Scale", "Model", "Image", "Text", "Animation", "Tint", "Flip", "Seat" };

    /// Shapes sent for every `#Synced` identity: `Looks` plus any a game adds (a `$Life` bar, a `$Run` score).
    /// A shape the rules simulate with — `$Bullet`, `$Monster` — stays OFF this list: a mirror without it is
    /// left alone by every shard, which is what keeps the client from moving what the host moves.
    public IReadOnlyList<string> Shapes { get; init; } = Looks;

    /// Events the host's rules emit that every client raises too, by bare name — `Hud`, `Scored`.
    public IReadOnlyList<string> Events { get; init; } = Array.Empty<string>();

    /// Marks each machine decides for itself, never sent: whose camera follows what.
    public IReadOnlyList<string> LocalMarks { get; init; } = new[] { "CameraTarget" };

    /// Times a second the host sends what changed and where everything is.
    public double Rate { get; init; } = 20;

    /// Seconds a client shows the world behind the host, so there are always two positions to move between.
    public double Delay { get; init; } = 0.1;

    /// Players in all, the host included.
    public int MaxPlayers { get; init; } = 4;

    /// The options a game set through net.Coop's `Share` settings (`[net.Coop]` in vein.settings), over these
    /// defaults: `Share.shapes` and `Share.events` are comma-separated names, `shapes` ADDED to `Looks`.
    public static VeinCoopOptions FromSettings(VeinGame game)
    {
        var options = new VeinCoopOptions();
        foreach (var s in game.Settings.Where(s => s.Section == "net.Coop"))
        {
            object? value = s.Live ?? s.Saved ?? s.Default;
            string text = (value?.ToString() ?? "").Trim().Trim('"');
            switch (s.Name)
            {
                case "Share.shapes": options = options with { Shapes = Looks.Concat(Names(text)).Distinct().ToList() }; break;
                case "Share.events": options = options with { Events = Names(text) }; break;
                case "Share.local": options = options with { LocalMarks = Names(text) }; break;
                case "Share.rate" when Number(value) is > 0 and var r: options = options with { Rate = r }; break;
                case "Share.delay" when Number(value) is >= 0 and var d: options = options with { Delay = d }; break;
            }
        }
        return options;
    }

    private static IReadOnlyList<string> Names(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.TrimStart('$', '@', '#')).ToList();

    private static double? Number(object? v) => v switch
    {
        double d => d,
        long l => l,
        string s when double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };
}
