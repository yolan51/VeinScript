using Xunit;

namespace Vein.Tests;

// Console tests reach PROCESS-GLOBAL state — the VEIN_CONSOLE address that decides which console a run
// is, plus the ConsoleLauncher/ConsoleBus hooks that stand in for real windows and pipes. Two of them
// running side by side would read each other's setup, so one collection serialises them all.
[CollectionDefinition(Name, DisableParallelization = true)]
public class ConsoleRuntime
{
    public const string Name = "console runtime";

    /// The repo's samples/ folder, found by walking up from the test binary.
    public static string Samples()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "samples"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("repo root with samples/ not found"),
                            "samples");
    }
}
