using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// samples/console_roles.vein is ONE program that behaves differently in each console it spawned, by
// switching on `here()`. These boot that real file once per role — with the launcher, the bus and the
// VEIN_CONSOLE address all faked — and check that each identity does its own job and nobody else's.
//
// The address is set through the real environment variable, because that IS the mechanism: the root
// process is the one nobody named. That makes these tests process-global, hence the shared collection.
[Collection(ConsoleRuntime.Name)]
public class ConsoleRolesTests
{
    private static string Source() => File.ReadAllText(Path.Combine(ConsoleRuntime.Samples(), "console_roles.vein"));

    /// Boot the sample as the console named `address` (null = the root, #Main), with nothing real
    /// attached: no window is spawned, no pipe is opened, and stdout is captured.
    private sealed class Session : IDisposable
    {
        public readonly List<string> Spawned = new();
        public readonly List<(string To, string Text)> Sent = new();
        public readonly StringWriter Out = new();
        public readonly Interp Interp = new();
        private readonly string? _previousAddress;

        /// `delivered: false` stands for every peer being closed — the send finds nothing listening.
        public Session(string? address, bool delivered = true)
        {
            _previousAddress = Environment.GetEnvironmentVariable(ConsoleLauncher.NameVar);
            Environment.SetEnvironmentVariable(ConsoleLauncher.NameVar, address);
            Environment.SetEnvironmentVariable(ConsoleLauncher.FirstVar, null);
            ConsoleLauncher.Hook = (name, _) => Spawned.Add(name);
            ConsoleBus.Hook = (to, _, text) => { Sent.Add((to, text)); return delivered; };

            var r = new VeinCompilerService().Compile(new CompileRequest("console_roles.vein", Source()));
            Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
            Interp.Run(r.Modules[0], new StringReader(""), Out);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(ConsoleLauncher.NameVar, _previousAddress);
            ConsoleLauncher.Hook = null;
            ConsoleBus.Hook = null;
        }
    }

    // ---- #Main: the launcher / driver --------------------------------------------------------

    [Fact]
    public void The_root_console_opens_the_three_workers()
    {
        using var s = new Session(null);
        Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, s.Spawned);
    }

    [Fact]
    public void The_root_console_pings_one_worker_per_turn_of_the_clock()
    {
        using var s = new Session(null);
        s.Interp.FireEvery();
        s.Interp.FireEvery();

        Assert.Equal(2, s.Sent.Count);
        Assert.All(s.Sent, x => Assert.Contains(x.To, new[] { "Alpha", "Beta", "Gamma" }));
        Assert.NotEqual(s.Sent[0].Text, s.Sent[1].Text);   // the round counter advances
    }

    [Fact]
    public void The_root_console_prints_what_it_is_sent_rather_than_replying()
    {
        using var s = new Session(null);
        s.Interp.Receive("Alpha", "a distinctive payload");

        Assert.Empty(s.Sent);
        Assert.Contains("a distinctive payload", s.Out.ToString());
        Assert.Contains("Alpha", s.Out.ToString());        // and it names who sent it
    }

    [Fact]
    public void Closing_the_workers_leaves_main_saying_so_rather_than_going_silent()
    {
        // The failure this sample used to have: with every worker closed, Main's only output was replies
        // it would now never receive, so a program that was still running looked exactly like a dead one.
        // Compared against a delivering run rather than against a fixed sentence, so rewording the
        // notice keeps this green — what must hold is that a lost send produces output at all.
        using var delivering = new Session(null);
        delivering.Interp.FireEvery();
        int quiet = delivering.Out.ToString().Length;

        using var lost = new Session(null, delivered: false);
        lost.Interp.FireEvery();

        Assert.True(lost.Out.ToString().Length > quiet, "an undeliverable send should say something");
        Assert.Contains(lost.Sent.Single().To, lost.Out.ToString());   // and name WHICH worker went away
    }

    // ---- the workers: same file, different behaviour ------------------------------------------

    [Fact]
    public void A_worker_does_not_spawn_more_consoles()
    {
        using var s = new Session("Alpha");
        Assert.Empty(s.Spawned);   // only #Main matches the `when` arm in `run once`
    }

    [Fact]
    public void An_unmatched_role_reaches_the_schedule_block_and_does_nothing()
    {
        // Deliberately NOT the sample: which roles the sample gives a `when` arm is the thing a person
        // edits while playing with it, so pinning that here would break the suite on every experiment.
        // What must hold is the mechanism — a role with no arm still runs the block and takes no action.
        string src = "bundle T by me {\n" +
                     "  shard Driver { every 2 { match here() {\n" +
                     "    when #Main { emit *Vein.Console.Io.@Send { to: #Alpha, text: \"ping\" } }\n" +
                     "  } } }\n}";
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", src));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));

        var sent = new List<string>();
        string? previous = Environment.GetEnvironmentVariable(ConsoleLauncher.NameVar);
        Environment.SetEnvironmentVariable(ConsoleLauncher.NameVar, "Alpha");
        ConsoleBus.Hook = (to, _, _) => { sent.Add(to); return true; };
        try
        {
            var interp = new Interp();
            interp.Run(r.Modules[0], new StringReader(""), new StringWriter());
            interp.FireEvery();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConsoleLauncher.NameVar, previous);
            ConsoleBus.Hook = null;
        }

        Assert.Empty(sent);
    }

    // These assert ROUTING and STATE — who answers whom, and that the counter advances — never the
    // wording. The sample is a scratchpad: reword a message and the test should stay green; change who
    // a role replies to and it should fail.

    [Fact]
    public void Alpha_echoes_back_to_main()
    {
        using var s = new Session("Alpha");
        s.Interp.Receive("Main", "a distinctive payload");

        var reply = s.Sent.Single();
        Assert.Equal("Main", reply.To);
        Assert.Contains("a distinctive payload", reply.Text);   // it echoes what it was sent
    }

    [Fact]
    public void Beta_counts_across_messages_and_reports_the_running_total()
    {
        using var s = new Session("Beta");
        s.Interp.Receive("Main", "first");
        s.Interp.Receive("Main", "second");

        Assert.Equal(new[] { "Main", "Main" }, s.Sent.Select(x => x.To));
        // The count is appended, so the reply ends with it — and it must advance between messages.
        Assert.EndsWith("1", s.Sent[0].Text);
        Assert.EndsWith("2", s.Sent[1].Text);
    }

    [Fact]
    public void Gamma_relays_to_alpha_so_a_ping_reaches_main_in_two_hops()
    {
        using var gamma = new Session("Gamma");
        gamma.Interp.Receive("Main", "a distinctive payload");
        var relayed = gamma.Sent.Single();
        Assert.Equal("Alpha", relayed.To);          // hop 1: Gamma → Alpha

        using var alpha = new Session("Alpha");
        alpha.Interp.Receive("Gamma", relayed.Text);

        var echoed = alpha.Sent.Single();
        Assert.Equal("Main", echoed.To);            // hop 2: Alpha → Main
        Assert.Contains("a distinctive payload", echoed.Text);   // carried the whole way
    }

    [Fact]
    public void An_unknown_address_falls_through_to_the_else_arm()
    {
        // A console the program knows nothing about still runs — it just takes the default role.
        using var s = new Session("Delta");
        s.Interp.Receive("Main", "a distinctive payload");

        Assert.Empty(s.Sent);                                     // it answers nobody …
        Assert.Contains("a distinctive payload", s.Out.ToString()); // … it just prints
    }
}

// `match` itself: it has been parsed, lowered and documented since M2, but the interpreter used to skip
// it (`default: break`), so every arm of every match was silently dead. These pin the semantics.
public class MatchTests
{
    private static string Run(string body)
    {
        string src = "bundle T by me { start @Boot { } event @Boot { }\n" +
                     "  shard M { hear @Boot as b {\n" + body + "\n  } } }";
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", src));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var sw = new StringWriter();
        new Interp().Run(r.Modules[0], new StringReader(""), sw);
        return sw.ToString().Trim();
    }

    private static string P(string expr) => "emit *Vein.Console.Io.@Print { text: " + expr + " }";

    [Fact]
    public void A_mark_pattern_matches_a_mark_value_by_name()
    {
        Assert.Equal("beta", Run(
            "let who = #Beta\n" +
            "match who {\n" +
            "  when #Alpha { " + P("\"alpha\"") + " }\n" +
            "  when #Beta  { " + P("\"beta\"") + " }\n" +
            "}"));
    }

    [Fact]
    public void Only_the_first_matching_arm_runs()
    {
        Assert.Equal("first", Run(
            "match #Alpha {\n" +
            "  when #Alpha { " + P("\"first\"") + " }\n" +
            "  when #Alpha { " + P("\"second\"") + " }\n" +
            "}"));
    }

    [Fact]
    public void An_unmatched_subject_takes_the_else_arm()
    {
        Assert.Equal("fallback", Run(
            "match #Zeta {\n" +
            "  when #Alpha { " + P("\"alpha\"") + " }\n" +
            "  else        { " + P("\"fallback\"") + " }\n" +
            "}"));
    }

    [Fact]
    public void An_unmatched_subject_with_no_else_arm_does_nothing()
    {
        Assert.Equal("", Run(
            "match #Zeta {\n" +
            "  when #Alpha { " + P("\"alpha\"") + " }\n" +
            "}"));
    }
}
