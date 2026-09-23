using Avalonia;
using Vein.Compiler.Tooling;
using Velopack;

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
        // FIRST, AND BEFORE ANYTHING DRAWS. The installer re-runs this same executable with hook
        // arguments — right after installing, before uninstalling, on each update — and this call is
        // what answers them: it creates the Start Menu shortcut, removes it again, and then exits
        // without ever reaching Avalonia. Start the UI first and a person installing the app watches
        // a window open and close for no reason they can see, four times.
        //
        // It is also the reason the Workbench references Velopack at all. `vpk pack` refuses to build
        // a package whose entry point does not call it, and it is right to: the hooks are the install.
        VelopackApp.Build().Run();

        Launch = WorkbenchLaunch.Parse(args);
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
