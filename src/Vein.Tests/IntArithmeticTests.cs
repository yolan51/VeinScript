using Vein.Hosting;
using Xunit;

namespace Vein.Tests;

// Two ints make an int on the interpreter, as in compiled code. `Num` returned `cond ? (long)v : v` — one type,
// double — so every `-`, `*` and `/` of ints gave a float, and `balance -= amount` turned an int field into a
// float on the interpreter only. Found by ReplicaTests' state hash, which differed between the runtimes.
public class IntArithmeticTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-int-" + Guid.NewGuid().ToString("N"));

    public IntArithmeticTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("a.Account.balance -= m.amount", 70L)]
    [InlineData("a.Account.balance = a.Account.balance - m.amount", 70L)]
    [InlineData("a.Account.balance = a.Account.balance * m.amount", 3000L)]
    [InlineData("a.Account.balance = a.Account.balance / m.amount", 3L)]
    [InlineData("a.Account.balance = -m.amount", -30L)]
    [InlineData("a.Account.balance = a.Account.balance + 9007199254740993", 9007199254741093L)]
    public void An_int_field_stays_an_exact_int(string body, long expected)
    {
        string path = Path.Combine(_dir, $"l{Math.Abs(body.GetHashCode())}.vein");
        File.WriteAllText(path, $$"""
            bundle Ledger by me {
                shape $Account { owner: string, balance: int = 0 }
                event @Open { owner: string, amount: int }
                event @Move { sender: string, amount: int }
                shard Book {
                    hear @Open as o {
                        let a = spawn()
                        attach $Account to a { owner: o.owner, balance: o.amount }
                    }
                    hear @Move as m {
                        target $Account as a { {{body}} }
                    }
                }
            }
            """);
        foreach (var mode in new[] { VeinRuntime.Interpreter, VeinRuntime.Compiled })
        {
            var game = VeinGame.Open(path, mode);
            Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
            game.Emit("Open", new Dictionary<string, object?> { ["owner"] = "alice", ["amount"] = 100L });
            game.Emit("Move", new Dictionary<string, object?> { ["sender"] = "alice", ["amount"] = 30L });
            Assert.Equal(expected, game.Entities[0].Shapes["Account"]["balance"]);
        }
    }
}
