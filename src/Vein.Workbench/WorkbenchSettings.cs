using System.Text.Json;

namespace Vein.Workbench;

// What the Workbench remembers between launches. Nothing did, before this: every start opened the
// default project's first file and dropped whatever you had been reading. On a second launch that is
// the first thing you notice, and it costs a JSON file to fix.
//
// Best-effort by design. A settings file that is missing, unreadable or written by a newer version must
// never stop the IDE opening — losing your layout is a nuisance, refusing to start over it is a bug. So
// every path here catches and carries on with defaults.
internal sealed class WorkbenchSettings
{
    public string? RootFolder { get; set; }

    /// Files that were open, in tab order.
    public List<string> OpenFiles { get; set; } = new();

    /// Which of them was in front.
    public string? ActiveFile { get; set; }

    /// Most-recent-first, capped. Enough to reach last week's project, short enough to scan.
    public List<string> RecentFolders { get; set; } = new();

    public bool AutoBuild { get; set; } = true;
    public double FontSize { get; set; } = 14;

    /// Where the Lounge and the Assistant sit: Bottom, Right, Window or Off.
    ///
    /// The default is a bottom tab rather than Off, because a feature nobody can find is not a
    /// feature — and it costs nothing, since both panels are inert until you sign in and make no
    /// request at all before then. `Off` removes the tab entirely, for anyone who wants it gone.
    public string LoungeDock { get; set; } = "Bottom";
    public string AssistantDock { get; set; } = "Bottom";

    /// Where each floating panel was last left, as `x,y,w,h`. The point of floating one is usually to
    /// park it on a second monitor, and a window that re-centres every launch has to be dragged back
    /// every launch.
    public string? LoungeWindow { get; set; }
    public string? AssistantWindow { get; set; }

    public const int MaxRecent = 8;

    private static string Path0 => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VeinScript", "workbench.json");

    public static WorkbenchSettings Load()
    {
        try
        {
            if (File.Exists(Path0))
                return JsonSerializer.Deserialize<WorkbenchSettings>(File.ReadAllText(Path0)) ?? new WorkbenchSettings();
        }
        catch { /* unreadable or from a newer version — defaults are a fine answer */ }
        return new WorkbenchSettings();
    }

    public void Save()
    {
        try
        {
            string dir = Path.GetDirectoryName(Path0)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path0, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* a read-only profile must not take the IDE down on exit */ }
    }

    /// Record a folder as most-recently-used, without duplicates.
    public void Remember(string folder)
    {
        RecentFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        RecentFolders.Insert(0, folder);
        if (RecentFolders.Count > MaxRecent) RecentFolders.RemoveRange(MaxRecent, RecentFolders.Count - MaxRecent);
    }
}
