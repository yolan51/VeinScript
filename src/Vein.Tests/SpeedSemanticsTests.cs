using Vein.Compiler.Ir;
using Vein.Compiler.Service;
using Xunit;

namespace Vein.Tests;

// The interpreter got faster (VeinCards' VEINSCRIPT_SPEED.md) in two places where a shortcut could change a
// meaning, and these hold the meaning:
//
//   * `return`, `break` and `continue` stopped being .NET exceptions and became a Flow each statement hands
//     back. A `return` must still leave every loop around it, and a `target` it leaves must still end its
//     activation.
//   * `xs += item` appends IN PLACE while only one local holds the list (GrowingList). Anyone else holding
//     it — another variable, a function, a payload, an outer list, a loop walking it — must still see the
//     list as it was, exactly as when every append copied.
public class SpeedSemanticsTests
{
    private static string Run(string body, string decls = "", string events = "") => RunSource($$"""
        bundle T by test {
            need "Vein.Console"
            {{events}}
            shard S {
                {{decls}}
                run once {
                    {{body}}
                }
            }
        }
        """);

    private static string RunSource(string source)
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", source));
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Select(d => d.ToString())));
        var output = new StringWriter();
        var errors = new StringWriter();
        new Interp { Ticks = 1, ErrorOutput = errors }.Run(r.Modules[0], new StringReader(""), output);
        Assert.Equal("", errors.ToString());
        return output.ToString().Replace("\r", "").TrimEnd('\n');
    }

    // ---- return, break, continue ----------------------------------------------------------------------

    [Fact]
    public void Return_leaves_every_loop_around_it()
    {
        string decls = """
            fn firstOver(xs: list<int>, n: int) return int {
                target xs as x {
                    repeat 3 as k {
                        if x > n { return x * 10 + k }
                    }
                }
                return -1
            }
            fn fib(n: int) return int {
                if n < 2 { return n }
                return fib(n - 1) + fib(n - 2)
            }
            """;
        Assert.Equal("40\n-1\n6765", Run("""
            do print(string(firstOver([1, 2, 4, 5], 3)))
            do print(string(firstOver([1, 2], 3)))
            do print(string(fib(20)))
            """, decls));
    }

    [Fact]
    public void Break_and_continue_act_on_the_nearest_loop_only()
    {
        Assert.Equal("0-0 0-1 1-0 1-1 2-0 2-1 |1 3 5", Run("""
            var seen = ""
            repeat 3 as i {
                repeat 5 as j {
                    if j == 2 { break }
                    seen = seen + "\(i)-\(j) "
                }
            }
            var odd = ""
            var n = 0
            while n < 6 {
                n += 1
                if n % 2 == 0 { continue }
                odd = odd + " \(n)"
            }
            do print(seen + "|" + trim(odd))
            """));
    }

    [Fact]
    public void A_return_out_of_an_identity_target_ends_its_activation()
    {
        // The second query would run inside the first one's activation if the return skipped EndActivation.
        Assert.Equal("7\n2", RunSource("""
            bundle T by test {
                need "Vein.Console"
                shape $Hp { hp: int }
                shard S {
                    fn firstHp() return int {
                        target $Hp as self { return self.Hp.hp }
                        return 0
                    }
                    run once {
                        let a = spawn()
                        attach $Hp to a { hp: 7 }
                        let b = spawn()
                        attach $Hp to b { hp: 9 }
                    }
                    each tick {
                        do print(string(firstHp()))
                        var total = 0
                        target $Hp as self { total += 1 }
                        do print(string(total))
                    }
                }
            }
            """));
    }

    // ---- lists grown in place -------------------------------------------------------------------------

    [Fact]
    public void Another_variable_keeps_the_list_it_was_given()
    {
        Assert.Equal("2 3 4", Run("""
            var xs = []
            xs += 1
            xs += 2
            let ys = xs
            xs += 3
            var zs = xs
            xs += 4
            do print("\(len(ys)) \(len(zs)) \(len(xs))")
            """));
    }

    [Fact]
    public void A_function_and_an_outer_list_keep_what_they_were_given()
    {
        string decls = """
            fn same(l: list<int>) return list<int> {
                return l
            }
            """;
        Assert.Equal("1 2 | 1 2 | 1 2 3 4", Run("""
            var xs = []
            xs += 1
            xs += 2
            let back = same(xs)
            var outer = []
            outer += [xs]
            xs += 3
            xs += 4
            do print("\(join(back, " ")) | \(join(outer[0], " ")) | \(join(xs, " "))")
            """, decls));
    }

    [Fact]
    public void A_payload_keeps_the_list_as_it_was_when_emitted()
    {
        string events = "event @Got { items: list<int> }";
        string decls = """
            hear @Got as g { do print(join(g.items, " ")) }
            """;
        Assert.Equal("1 2", Run("""
            var xs = []
            xs += 1
            xs += 2
            emit @Got { items: xs }
            xs += 3
            """, decls, events));
    }

    [Fact]
    public void A_loop_walks_the_list_as_it_was_and_reading_its_length_keeps_nothing()
    {
        Assert.Equal("6 | 1 2 3 1 2 3 | 8", Run("""
            var xs = []
            xs += 1
            xs += 2
            xs += 3
            target xs as x { xs += x }
            let shown = join(xs, " ")
            xs += xs[0]
            xs += len(xs)
            do print("\(len(xs) - 2) | \(shown) | \(len(xs))")
            """));
    }

    [Fact]
    public void A_list_added_to_itself_doubles()
    {
        Assert.Equal("1 2 1 2", Run("""
            var xs = []
            xs += 1
            xs += 2
            xs += xs
            do print(join(xs, " "))
            """));
    }
}
