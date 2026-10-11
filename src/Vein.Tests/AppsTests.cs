using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// The real tools in `apps/` sit outside `samples/`, so SamplesTests never compiles them. Each one carries
// its own golden tests (`apps/<name>/tests/`, run by `veinc test`); this runs them with the suite, so a
// language change that breaks a real program fails here and not first in someone's shell.
public class AppsTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "stdlib"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root with stdlib/ not found");
    }

    public static IEnumerable<object[]> Apps() =>
        Directory.GetDirectories(Path.Combine(RepoRoot(), "apps"))
            .Where(d => Directory.Exists(Path.Combine(d, "tests")))
            .Select(d => new object[] { Path.GetFileName(d) });

    [Theory]
    [MemberData(nameof(Apps))]
    public void Every_app_passes_its_own_tests(string app)
    {
        string? program = TestRunner.FindProgram(Path.Combine(RepoRoot(), "apps", app));
        Assert.NotNull(program);

        var results = TestRunner.RunAll(program!);
        Assert.NotEmpty(results);
        Assert.True(results.All(r => r.Passed), TestRunner.Report(results));
    }
}
