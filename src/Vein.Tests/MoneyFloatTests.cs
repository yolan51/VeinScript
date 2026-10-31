using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// VS0404: money kept in a float. A float is binary — `0.1 + 0.2` is 0.30000000000000004 — so a price, a balance
// or a fee in one drifts by fractions of a cent. The warning goes where the field or var is declared, by its
// name; a name that means a fraction (`taxRate`) is a float on purpose. samples/money.vein shows the fix:
// whole cents in an int, with stdlib Vein.Money, checked on both runtimes by check-backend.
public class MoneyFloatTests
{
    private static List<string> Warnings(string body)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", "bundle T by me {\n" + body + "\n}"));
        return r.Diagnostics.Where(d => d.Code == "VS0404").Select(d => d.Message).ToList();
    }

    [Theory]
    [InlineData("  shape $Shop { price: float }", "$Shop.price")]
    [InlineData("  shape $Account { accountBalance: float = 0.0 }", "$Account.accountBalance")]
    [InlineData("  event @Paid { fee_due: float }", "@Paid.fee_due")]
    [InlineData("  shard Till { var cash = 9.99\n    run once { } }", "Till.cash")]
    public void Money_in_a_float_is_warned_where_it_is_declared(string body, string where)
    {
        var w = Assert.Single(Warnings(body));
        Assert.Contains("`" + where + "`", w);
        Assert.Contains("Cents: int", w);   // the fix, named
    }

    [Theory]
    [InlineData("  shape $Shop { priceCents: int }")]                 // the fix itself
    [InlineData("  shape $Shop { taxRate: float }")]                  // a fraction, on purpose
    [InlineData("  shape $Shop { priceFactor: float }")]
    [InlineData("  shape $Car { speed: float }")]                     // not money at all
    [InlineData("  event @Hit { amount: float }")]                    // a game's damage, not money
    [InlineData("  shape $Doc { payload: float }")]                   // `pay` inside another word is not `pay`
    [InlineData("  shard Till { var total = 9.99\n    run once { } }")]   // too general a word to call money
    public void Cents_rates_and_other_floats_are_left_alone(string body)
    {
        Assert.Empty(Warnings(body));
    }
}
