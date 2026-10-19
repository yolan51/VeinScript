using Vein.Compiler.Ir;
using Xunit;

namespace Vein.Tests;

/// What a spawned console window runs. It re-runs the program that spawned it, so the arguments have to
/// name that program however it was started.
public class ConsoleLauncherTests
{
    [Fact]
    public void A_built_program_is_its_own_executable_so_its_first_argument_is_dropped()
    {
        // `mesh.exe` → the window runs `mesh.exe` again, with the same arguments.
        var args = ConsoleLauncher.RelaunchArguments(@"C:\games\mesh.exe", new[] { @"C:\games\mesh.exe", "--fast" });
        Assert.Equal(new[] { "--fast" }, args);
    }

    [Fact]
    public void Under_the_dotnet_host_the_programs_dll_is_kept()
    {
        // `dotnet veinc run mesh.vein`: the executable is dotnet.exe and the first argument is veinc.dll. Dropped,
        // every window ran `dotnet run mesh.vein` — the SDK's own `run` — and closed at once (demos/mesh-chat).
        var args = ConsoleLauncher.RelaunchArguments(@"C:\Program Files\dotnet\dotnet.exe",
            new[] { @"C:\Users\yo\.nuget\packages\vein.cli\tools\veinc.dll", "run", "demos/mesh-chat/mesh.vein" });
        Assert.Equal(new[] { @"C:\Users\yo\.nuget\packages\vein.cli\tools\veinc.dll", "run", "demos/mesh-chat/mesh.vein" }, args);
    }

    [Fact]
    public void The_dotnet_host_is_recognised_on_unix_too()
    {
        var args = ConsoleLauncher.RelaunchArguments("/usr/share/dotnet/dotnet", new[] { "/opt/veinc.dll", "run", "a.vein" });
        Assert.Equal(new[] { "/opt/veinc.dll", "run", "a.vein" }, args);
    }
}
