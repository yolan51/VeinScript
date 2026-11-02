using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

/// VS0411: a shape named like a standard one plus a copy word (`$Position2d`) is a component nothing reads.
public class LookalikeShapeTests
{
    private static IReadOnlyList<Diagnostic> Check(string body) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", "bundle T by me {\n" + body + "\n}")).Diagnostics;

    [Theory]
    [InlineData("Position2d", "Position")]
    [InlineData("Position3D", "Position")]
    [InlineData("Position2", "Position")]
    [InlineData("MyPosition", "Position")]
    [InlineData("LocalScale", "Scale")]
    [InlineData("VelocityData", "Velocity")]
    [InlineData("RectComponent", "Rect")]
    [InlineData("NameV2", "Name")]
    public void A_standard_name_plus_copy_words_is_a_lookalike(string name, string standard) =>
        Assert.Equal(standard, Vocabulary.LookalikeOf(name));

    [Theory]
    [InlineData("Position")]   // the standard itself: VS0220's business if the fields differ
    [InlineData("Player")]     // the letters of Layer, not the word
    [InlineData("PoolSize")]   // a real second word
    [InlineData("WorkPool")]
    [InlineData("TextStyle")]
    [InlineData("SpawnPosition")]
    [InlineData("Vec3")]
    public void A_different_thing_is_not(string name) =>
        Assert.Null(Vocabulary.LookalikeOf(name));

    [Fact]
    public void Declaring_one_warns_and_says_what_the_standard_is()
    {
        var d = Assert.Single(Check("shape $Position2d { x: float, y: float }"), x => x.Code == "VS0411");
        Assert.Contains("'$Position2d' looks like the standard '$Position'", d.Message);
        Assert.Contains("a 2D game uses $Position with z = 0", d.Message);
    }

    [Fact]
    public void Inside_a_publicator_too() =>
        Assert.Contains(Check("publicator Api {\n shared(\"x\")\n shape $MyVelocity { x: float }\n}"), x => x.Code == "VS0411");

    [Fact]
    public void Its_own_name_is_silent() =>
        Assert.DoesNotContain(Check("shape $SpawnPoint { x: float, y: float }"), x => x.Code == "VS0411");
}
