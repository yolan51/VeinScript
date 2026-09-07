using Avalonia;
using Vein.Compiler.Tooling;

namespace Vein.Workbench;

internal static class Program
{
    /// What the command line asked for: `VeinScript-Workbench <path> [--line N]`.
    ///
    /// A STATIC because the window is built by Avalonia's lifetime, not by us — there is no constructor
    /// call to thread an argument through. Read once here, consumed once in MainWindow.
    ///
    /// The parsing itself lives in `WorkbenchLaunch` (Vein.Compiler/Tooling), where `Vein.Tests` can
    /// reach it. This file is the adapter, the same way `EditorCommands` is thin over `SourceEdits`.
    public static WorkbenchArgs Launch { get; private set; } = WorkbenchArgs.None;

    [STAThread]
    public static void Main(string[] args)
    {
        Launch = WorkbenchLaunch.Parse(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
