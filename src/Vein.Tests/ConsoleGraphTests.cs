using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Vein.Compiler.Tooling;
using Xunit;

namespace Vein.Tests;

// A console address is an identity (`#Server`), not text. The runtime never checks one — ConsoleBus
// concatenates it into an OS pipe name and a miss is silently swallowed — so ConsoleGraph pairs every
// `@Send { to }` with a spawn site and Lower warns (VS0212) on the ones that resolve to nothing.
[Collection(ConsoleRuntime.Name)]
public class ConsoleGraphTests
{
    private static CompilationResult Compile(string src) =>
        new VeinCompilerService().Compile(new CompileRequest("t.vein", src));

    /// A bundle whose Boot shard runs `body`.
    private static string Boot(string body) =>
        "bundle B by me { start @Boot { } event @Boot { } " +
        "shard M { hear @Boot as b { " + body + " } } }";

    private static IEnumerable<Diagnostic> Vs0212(CompilationResult r) =>
        r.Diagnostics.Where(d => d.Code == "VS0212");

    [Fact]
    public void Console_spawn_accepts_a_mark_address()
    {
        // A mark in value position evaluates to its own name, so #Server reaches the launcher as "Server"
        // — byte-identical to the string literal it replaces.
        var r = Compile(Boot("emit *Vein.Console.Io.@Console { name: #Server, firsttext: \"ready\" }"));
        Assert.True(r.Success);

        (string Name, string First)? got = null;
        ConsoleLauncher.Hook = (n, f) => got = (n, f);
        try { new Interp().Run(r.Modules[0], new StringReader(""), new StringWriter()); }
        finally { ConsoleLauncher.Hook = null; }

        Assert.Equal(("Server", "ready"), got);
    }

    [Fact]
    public void Bring_console_builder_accepts_a_mark_address()
    {
        var r = Compile(Boot("bring *Vein.Console.Io.&Console(#Server, \"ready\")"));
        Assert.True(r.Success);

        (string Name, string First)? got = null;
        ConsoleLauncher.Hook = (n, f) => got = (n, f);
        try { new Interp().Run(r.Modules[0], new StringReader(""), new StringWriter()); }
        finally { ConsoleLauncher.Hook = null; }

        Assert.Equal(("Server", "ready"), got);
        Assert.Empty(Vs0212(r));            // `bring &Console` counts as a spawn site
    }

    [Fact]
    public void Unspawned_console_address_warns()
    {
        var r = Compile(Boot(
            "emit *Vein.Console.Io.@Console { name: #Server, firsttext: \"ready\" } " +
            "emit *Vein.Console.Io.@Send { to: #Sever, text: \"hi\" }"));

        var d = Assert.Single(Vs0212(r));
        Assert.Equal(Severity.Warning, d.Severity);
        Assert.Contains("#Sever", d.Message);
        Assert.Contains("#Server", d.Message);      // the known list helps you spot the typo
        Assert.True(r.Success);                      // a warning must not fail the compile
    }

    [Fact]
    public void String_and_mark_spawn_sites_both_resolve()
    {
        // Strings still evaluate identically, so a not-yet-migrated spawn must satisfy a mark address
        // and vice versa — no false warnings on mixed code.
        Assert.Empty(Vs0212(Compile(Boot(
            "emit *Vein.Console.Io.@Console { name: \"Server\", firsttext: \"r\" } " +
            "emit *Vein.Console.Io.@Send { to: #Server, text: \"hi\" }"))));

        Assert.Empty(Vs0212(Compile(Boot(
            "emit *Vein.Console.Io.@Console { name: #Server, firsttext: \"r\" } " +
            "emit *Vein.Console.Io.@Send { to: \"Server\", text: \"hi\" }"))));
    }

    [Fact]
    public void Reserved_main_needs_no_spawn_site()
    {
        Assert.Empty(Vs0212(Compile(Boot("emit *Vein.Console.Io.@Send { to: #Main, text: \"hi\" }"))));
    }

    [Fact]
    public void Spawn_in_one_shard_covers_an_address_in_another()
    {
        // The real shape of console_chat.vein: a Launcher spawns, a separate Chat shard addresses.
        var r = Compile(
            "bundle B by me { start @Boot { } event @Boot { } " +
            "shard Launcher { hear @Boot as b { emit *Vein.Console.Io.@Console { name: #Server, firsttext: \"r\" } } } " +
            "shard Chat { hear *Vein.Console.Io.@Input as i { emit *Vein.Console.Io.@Send { to: #Server, text: i.text } } } }");

        Assert.True(r.Success);
        Assert.Empty(Vs0212(r));
    }

    [Fact]
    public void Non_literal_address_is_not_guessed_at()
    {
        // `to: i.text` is not statically knowable — skip it rather than warn on something we cannot resolve.
        var r = Compile(
            "bundle B by me { start @Boot { } event @Boot { } " +
            "shard Chat { hear *Vein.Console.Io.@Input as i { emit *Vein.Console.Io.@Send { to: i.text, text: i.text } } } }");

        Assert.Empty(Vs0212(r));
    }

    [Fact]
    public void Graph_records_spawns_and_addresses()
    {
        var ast = Compile(Boot(
            "emit *Vein.Console.Io.@Console { name: #Server, firsttext: \"r\" } " +
            "emit *Vein.Console.Io.@Send { to: #Server, text: \"hi\" }")).Ast;

        var g = ConsoleGraph.Analyze(ast!);
        Assert.NotNull(g);
        Assert.Equal("Server", Assert.Single(g!.Spawns).Address);
        Assert.Equal("Server", Assert.Single(g.Addresses).Target);
        Assert.Equal(new[] { "Main", "Server" }, g.Known);
    }

    [Fact]
    public void Mark_typed_field_zero_fills_to_the_root_address()
    {
        // `?` on a Mark field has no meaningful empty value — "" would be a pipe named "vein.console.".
        // The event is declared locally because `?` fills from the module's own types, and a cross-bundle
        // `*Vein.Console.Io.@Console` is not linked in (see Interp's fill path).
        var r = Compile(
            "bundle B by me { start @Boot { } event @Boot { } " +
            "event @Console { name: Mark, firsttext: string } " +
            "shard M { hear @Boot as b { emit @Console ? } } }");
        Assert.True(r.Success);

        (string Name, string First)? got = null;
        ConsoleLauncher.Hook = (n, f) => got = (n, f);
        try { new Interp().Run(r.Modules[0], new StringReader(""), new StringWriter()); }
        finally { ConsoleLauncher.Hook = null; }

        Assert.NotNull(got);
        Assert.Equal("Main", got!.Value.Name);   // not "" — that would be the pipe `vein.console.`
    }
}
