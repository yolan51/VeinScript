using Vein.Hosting;
using Xunit;

namespace Vein.Tests;

// REPLICAS (VeinGame.Replica.cs): several machines run the same rules over the same inputs and must agree —
// a ledger's validators. Pure mode refuses what could differ by machine (VS0364), Execute fails work that
// runs out of gas, Checkpoint/Restore puts the state back, StateHash says whether two replicas agree, and
// Reseed makes random() the same everywhere.
public class ReplicaTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-replica-" + Guid.NewGuid().ToString("N"));

    public ReplicaTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string Write(string name, string source)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, source);
        return path;
    }

    private const string Ledger = """
        bundle Ledger by me {
            shape $Account { owner: string, balance: int = 0 }
            mark #Frozen
            event @Open { owner: string, amount: int }
            event @Move { sender: string, receiver: string, amount: int }
            event @Freeze { }
            event @Spin { turns: int }
            event @Roll { }
            shard Book {
                var moves: int = 0
                hear @Open as o {
                    let a = spawn()
                    attach $Account to a { owner: o.owner, balance: o.amount }
                }
                hear @Move as m {
                    target $Account as a { if a.Account.owner == m.sender { a.Account.balance -= m.amount } }
                    target $Account as b { if b.Account.owner == m.receiver { b.Account.balance += m.amount } }
                    moves += 1
                }
                hear @Freeze as f {
                    let e = spawn()
                    mark e #Frozen
                }
                hear @Spin as s {
                    let junk = spawn()
                    var i = 0
                    while i < s.turns { i += 1 }
                }
                hear @Roll as r { emit *Vein.Console.Io.@Print { text: "roll " + string(random()) } }
            }
        }
        """;

    private static Dictionary<string, object?> F(params (string Key, object? Value)[] fields) =>
        fields.ToDictionary(f => f.Key, f => f.Value);

    private static VeinGame Start(string path, VeinRuntime mode)
    {
        var game = VeinGame.Open(path, mode, new VeinOptions { Pure = true });
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
        return game;
    }

    [Fact]
    public void Pure_mode_refuses_what_differs_by_machine_and_keeps_what_does_not()
    {
        string impure = Write("impure.vein", """
            bundle Clock by me {
                event @Go { }
                shard S {
                    hear @Go as g {
                        emit *Vein.Console.Io.@Print { text: now() }
                        emit *Vein.Files.Io.@WriteFile { path: "x.txt", text: "x" }
                    }
                }
            }
            """);
        var game = VeinGame.Open(impure, VeinRuntime.Interpreter, new VeinOptions { Pure = true });
        Assert.False(game.Start());
        var refused = game.Diagnostics.Where(d => d.Code == "VS0364").Select(d => d.Message).ToList();
        Assert.Contains(refused, m => m.Contains("now()"));
        Assert.Contains(refused, m => m.Contains("@WriteFile"));
        Assert.DoesNotContain(refused, m => m.Contains("@Print"));

        // Needing a library with impure functions is fine as long as nothing calls them: Vein.Crypto has
        // newKeys and seal; this calls only sha256 and verify, which every machine answers alike.
        string pure = Write("pure.vein", """
            bundle Hashing by me {
                need "Vein.Crypto"
                event @Go { text: string }
                shard S {
                    hear @Go as g { emit *Vein.Console.Io.@Print { text: sha256(g.text) + " " + string(verify(g.text, "vg1:AAAA", "nope")) } }
                }
            }
            """);
        var ok = VeinGame.Open(pure, VeinRuntime.Interpreter, new VeinOptions { Pure = true });
        Assert.True(ok.Start(), string.Join("\n", ok.Diagnostics));
        Assert.Empty(VeinGame.CheckPure(pure));
        Assert.NotEmpty(VeinGame.CheckPure(impure));
    }

    [Theory]
    [InlineData(VeinRuntime.Interpreter)]
    [InlineData(VeinRuntime.Compiled)]
    public void The_same_inputs_give_the_same_state_hash_on_both_runtimes(VeinRuntime mode)
    {
        string path = Write($"ledger-{mode}.vein", Ledger);
        var reference = Start(path, VeinRuntime.Interpreter);
        var game = Start(path, mode);
        foreach (var g in new[] { reference, game })
        {
            Assert.True(g.Execute("Open", F(("owner", "alice"), ("amount", 100L))).Ok);
            Assert.True(g.Execute("Open", F(("owner", "bob"), ("amount", 5L))).Ok);
            Assert.True(g.Execute("Move", F(("sender", "alice"), ("receiver", "bob"), ("amount", 30L))).Ok);
            Assert.True(g.Execute("Freeze").Ok);   // an identity carrying only a mark counts too
        }
        Assert.Equal(mode == VeinRuntime.Compiled ? "compiled" : "interpreter", game.Runtime);
        Assert.Equal(reference.StateText(), game.StateText());
        Assert.Equal(reference.StateHash(), game.StateHash());
        Assert.Equal(64, game.StateHash().Length);
        Assert.Contains(game.Entities, e => e.Marks.Contains("Frozen") && e.Shapes.Count == 0);

        // …and any difference changes it.
        Assert.True(game.Execute("Move", F(("sender", "bob"), ("receiver", "alice"), ("amount", 1L))).Ok);
        Assert.NotEqual(reference.StateHash(), game.StateHash());
    }

    [Fact]
    public void Work_that_runs_out_of_gas_fails_and_a_checkpoint_puts_everything_back()
    {
        string path = Write("gas.vein", Ledger);
        var game = Start(path, VeinRuntime.Interpreter);
        var replica = Start(path, VeinRuntime.Interpreter);   // one that never sees the failed work
        foreach (var g in new[] { game, replica })
            g.Execute("Open", F(("owner", "alice"), ("amount", 100L)), budget: 10_000);

        string before = game.StateHash();
        var saved = game.Checkpoint();
        Assert.NotNull(saved);

        var spin = game.Execute("Spin", F(("turns", 1_000_000L)), budget: 5_000);
        Assert.False(spin.Ok);
        Assert.Contains("out of gas", spin.Reason);
        Assert.True(spin.Gas > 5_000 - 1);

        // A `while` cap is a failure too under a budget — it is not "finished".
        Assert.True(game.Restore(saved!));
        var capped = game.Execute("Spin", F(("turns", 1_000_000L)), budget: 100_000_000);
        Assert.False(capped.Ok);
        Assert.Contains("100,000", capped.Reason);

        Assert.True(game.Restore(saved!));
        Assert.Equal(before, game.StateHash());

        // Ids carry on where the checkpoint left them, so the next spawn agrees with the replica's.
        foreach (var g in new[] { game, replica })
        {
            var open = g.Execute("Open", F(("owner", "carol"), ("amount", 7L)), budget: 10_000);
            Assert.True(open.Ok, open.Reason);
            Assert.True(open.Gas > 0);
        }
        Assert.Equal(replica.StateHash(), game.StateHash());

        // Without a budget the same work is merely cut short, as a game needs: the frame must end.
        Assert.True(replica.Execute("Spin", F(("turns", 1_000_000L))).Ok);
    }

    [Theory]
    [InlineData(VeinRuntime.Interpreter)]
    [InlineData(VeinRuntime.Compiled)]
    public void Reseeding_makes_random_draws_agreed_but_not_fixed(VeinRuntime mode)
    {
        string path = Write($"roll-{mode}.vein", Ledger);
        var game = Start(path, mode);
        var printed = new List<string>();
        game.Printed += (line, _) => printed.Add(line);

        game.Reseed(1); game.Execute("Roll");
        game.Reseed(1); game.Execute("Roll");
        game.Reseed(2); game.Execute("Roll");
        Assert.Equal(3, printed.Count);
        Assert.Equal(printed[0], printed[1]);
        Assert.NotEqual(printed[0], printed[2]);

        var other = Start(path, mode == VeinRuntime.Compiled ? VeinRuntime.Interpreter : VeinRuntime.Compiled);
        var otherPrinted = new List<string>();
        other.Printed += (line, _) => otherPrinted.Add(line);
        other.Reseed(2); other.Execute("Roll");
        Assert.Equal(printed[2], Assert.Single(otherPrinted));
    }

    [Theory]
    [InlineData(VeinRuntime.Interpreter)]
    [InlineData(VeinRuntime.Compiled)]
    public void Signatures_verify_for_their_signer_and_text_only(VeinRuntime mode)
    {
        string path = Write($"sign-{mode}.vein", """
            bundle Signed by me {
                need "Vein.Crypto"
                SF say(name: string, good: bool) {
                    if good { emit *Vein.Console.Io.@Print { text: "ok " + name } } else { emit *Vein.Console.Io.@Print { text: "FAIL " + name } }
                }
                shard S { run once {
                    let alice = newKeys()
                    let bob = newKeys()
                    let card = publicKey(alice)
                    let sig = sign("pay bob 5", alice)
                    do say("a signature verifies", verify("pay bob 5", sig, card))
                    do say("not for another text", not verify("pay bob 50", sig, card))
                    do say("not for another signer", not verify("pay bob 5", sig, publicKey(bob)))
                    do say("a signature starts vg1:", startsWith(sig, "vg1:"))
                    do say("nonsense does not verify", not verify("x", "nonsense", card))
                } }
            }
            """);
        var game = VeinGame.Open(path, mode);
        var printed = new List<string>();
        game.Printed += (line, _) => printed.Add(line);
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
        Assert.Equal(mode == VeinRuntime.Compiled ? "compiled" : "interpreter", game.Runtime);
        Assert.Equal(5, printed.Count(l => l.StartsWith("ok ")));
        Assert.DoesNotContain(printed, l => l.StartsWith("FAIL"));

        // Signing draws randomness (ECDSA): a pure program may verify, not sign.
        Assert.Contains(VeinGame.CheckPure(path), d => d.Message.Contains("cryptoSign"));
    }
}
