using Vein.Compiler.Parsing;

namespace Vein.Compiler.Project;

/// A program that opens WINDOWS: one of its bundles needs the stdlib's Vein.Desktop. The Workbench's ▶ runs
/// such a program with its windows on screen (DesktopRunner); `veinc run` runs the same rules with nothing
/// drawn, and says so rather than leaving a person waiting for a window that never comes.
public static class DesktopProgram
{
    public const string Bundle = "Vein.Desktop";

    public static bool Uses(CompilationUnit? unit) =>
        unit is not null && unit.Bundles.Any(b => b.Members.OfType<NeedDecl>().Any(n => n.Key == Bundle));

    public const string HeadlessNote =
        "note: this program opens windows (Vein.Desktop). `veinc run` runs its rules with no window drawn — " +
        "open it in the Workbench and press ▶ to see them.";
}
