using Vein.Compiler.Backends;
using Vein.Compiler.Service;
using Vein.Hosting;
using Xunit;

namespace Vein.Tests;

// A program built on parsed records — Vein Cards keeps its whole game in one `fromJson` dictionary — compiles to
// C# now (VeinValues), and these hold the two places the first compiled run of it went wrong:
//
//   * an int literal reached VeinValues as a C# `int` or, past 2^31, a `uint`, which the interpreter's rules do
//     not count as a number: `seed % 2147483648` was NaN, and every compiled match was dealt differently;
//   * `@Ready { db: string }` given the game record: the interpreter carries the record through a field it
//     never type-checks, compiled code cannot — VS0400 says so, and the program stays on the interpreter.
public class CompiledRecordsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vein-records-" + Guid.NewGuid().ToString("N"));

    public CompiledRecordsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private List<string> Host(string source, VeinRuntime mode)
    {
        string path = Path.Combine(_dir, mode + ".vein");
        File.WriteAllText(path, source);
        var game = VeinGame.Open(path, mode);
        var lines = new List<string>();
        game.Printed += (line, isError) => lines.Add(isError ? "ERR " + line : line);
        Assert.True(game.Start(), string.Join("\n", game.Diagnostics));
        Assert.Equal(mode == VeinRuntime.Compiled ? "compiled" : "interpreter", game.Runtime);
        game.Step(1.0 / 60);
        return lines;
    }

    [Fact]
    public void Arithmetic_on_a_record_field_with_a_large_literal_is_the_same_compiled()
    {
        // Vein Cards' own seeded generator and shuffle, word for word.
        const string source = """
            bundle T by you {
                need "Vein.Console"
                fn rand(db: dictionary) return float {
                    db["seed"] = (db.seed * 1103515245 + 12345) % 2147483648
                    return db.seed / 2147483648.0
                }
                fn shuffled(db: dictionary, xs: list<string>) return list<string> {
                    var out = []
                    target xs as x { out += x }
                    var i = len(out) - 1
                    while i > 0 {
                        let j = int(rand(db) * (i + 1))
                        let t = out[i]
                        out[i] = out[j]
                        out[j] = t
                        i -= 1
                    }
                    return out
                }
                shard S {
                    run once {
                        let db = fromJson("{\"seed\": 42}")
                        do print("r \(rand(db)) seed \(db.seed)")
                        do print(join(shuffled(db, ["a", "b", "c", "d", "e", "f"]), ","))
                    }
                }
            }
            """;
        var interpreted = Host(source, VeinRuntime.Interpreter);
        Assert.Equal(2, interpreted.Count);
        Assert.StartsWith("r 0.5823075897060335 ", interpreted[0]);   // not NaN
        Assert.Equal(interpreted, Host(source, VeinRuntime.Compiled));
    }

    private const string ReadySource = """
        bundle T by you {
            event @Ready { db: string }
            event @Named { id: string }
            shard Data {
                var db = fromJson("{\"cards\": 3}")
                fn get(d: dictionary, k: string, fallback: json) return json {
                    return d[k]
                }
                run once {
                    emit @Ready { db: db }
                    emit @Named { id: get(db, "cards", "") }
                }
            }
        }
        """;

    [Fact]
    public void A_record_given_to_a_text_field_is_said_and_stays_on_the_interpreter()
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", ReadySource));
        var warnings = r.Diagnostics.Where(d => d.Code == "VS0400").ToList();

        Assert.Single(warnings);   // `@Named.id` takes a `json` value that is not certainly a record: no word
        Assert.Contains("`@Ready.db` is declared `string`", warnings[0].Message);
        Assert.Contains("Declare the field `json`", warnings[0].Message);

        var emitted = new CSharpBackend().Emit(r.Modules[0]);
        Assert.Contains(emitted.Notes, n => n.Contains("Ready.db") && n.Contains("VS0400"));
    }

    [Fact]
    public void Declared_json_the_record_compiles()
    {
        var r = new VeinCompilerService().Compile(new CompileRequest("t.vein", ReadySource.Replace("@Ready { db: string }", "@Ready { db: json }")));

        Assert.DoesNotContain(r.Diagnostics, d => d.Code == "VS0400");
        Assert.DoesNotContain(new CSharpBackend().Emit(r.Modules[0]).Notes, n => n.Contains("VS0400"));
    }
}
