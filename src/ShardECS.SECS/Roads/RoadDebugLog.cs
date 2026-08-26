namespace ShardECS.SECS.Roads;

/// <summary>
/// Records every node visited during a Road traversal and prints a formatted
/// tree showing what was TAKEN, what went to ELSE, and what was SKIPPED entirely.
/// </summary>
public sealed class RoadDebugLog
{
    private readonly List<RoadDebugStep> _steps = [];

    internal void RecordTaken  (string label, int depth) => Add(label, depth, open: true,  reached: true,  tookElse: false);
    internal void RecordElse   (string label, int depth) => Add(label, depth, open: false, reached: true,  tookElse: true);
    internal void RecordSkipped(string label, int depth) => Add(label, depth, open: false, reached: false, tookElse: false);

    private void Add(string label, int depth, bool open, bool reached, bool tookElse)
        => _steps.Add(new RoadDebugStep(label, depth, open, reached, tookElse));

    /// <summary>Prints the traversal tree to <see cref="Console"/>.</summary>
    public void Print(int entity)
    {
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════════════════════════");
        Console.WriteLine($"║  ROAD DEBUG  —  Entity {entity}");
        Console.WriteLine("╠══════════════════════════════════════════════════════════════════");

        foreach (var step in _steps)
        {
            string indent    = new string(' ', step.Depth * 4);
            string connector = step.Depth > 0 ? "└─ " : "";
            string gate      = step.Open    ? "[OPEN]  " : "[CLOSED]";

            string status;
            if (step.Open)          status = "→ TAKEN";
            else if (step.TookElse) status = "→ ELSE ";
            else                    status = "  SKIPPED";

            // Truncate very long expression labels for readability
            string lbl = step.Label.Length > 48
                ? step.Label[..45] + "..."
                : step.Label;

            Console.WriteLine($"║  {indent}{connector}{lbl,-48} {gate}  {status}");
        }

        Console.WriteLine("╚══════════════════════════════════════════════════════════════════");
        Console.WriteLine();
    }
}

internal sealed class RoadDebugStep
{
    public string Label    { get; }
    public int    Depth    { get; }
    public bool   Open     { get; }
    public bool   Reached  { get; }
    public bool   TookElse { get; }

    public RoadDebugStep(string label, int depth, bool open, bool reached, bool tookElse)
    {
        Label    = label;
        Depth    = depth;
        Open     = open;
        Reached  = reached;
        TookElse = tookElse;
    }
}
