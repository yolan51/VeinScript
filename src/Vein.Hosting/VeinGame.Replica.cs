using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Vein.Compiler.Diagnostics;
using Vein.Compiler.Ir;

namespace Vein.Hosting;

/// How a host wants a program run (VeinGame.Open). The defaults are an ordinary game.
public sealed record VeinOptions
{
    /// Refuse, at compile time (VS0364), a program that could do something different on another machine —
    /// read the clock, the environment or the command line, draw fresh randomness, or reach outside through
    /// files, the network, consoles or sound (Purity). For REPLICAS: several machines running the same rules
    /// over the same inputs, which must agree.
    public bool Pure { get; init; }
}

/// What one Execute did: whether the work FINISHED, the steps it used (-1 on compiled code, which counts
/// none), and why not when it did not — out of gas, a fault in a handler, or no such event.
public sealed record VeinOutcome(bool Ok, long Gas, string Reason);

/// The whole state of a running program at one moment (VeinGame.Checkpoint): every identity, every shard
/// var, the mutes and where ids carry on. Opaque to a host, and immutable — Restore copies out of it.
public sealed class VeinCheckpoint
{
    internal VeinCheckpoint(IReadOnlyList<VeinEntity> entities,
                            IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> vars,
                            IReadOnlyList<string> mutedMarks, IReadOnlyList<string> mutedShards, long nextId)
    {
        Entities = entities; Vars = vars; MutedMarks = mutedMarks; MutedShards = mutedShards; NextId = nextId;
    }

    internal IReadOnlyList<VeinEntity> Entities { get; }
    internal IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Vars { get; }
    internal IReadOnlyList<string> MutedMarks { get; }
    internal IReadOnlyList<string> MutedShards { get; }
    internal long NextId { get; }
}

/// REPLICAS — what a host needs when several machines run the same rules over the same inputs and must
/// agree on the result (a ledger's validators, a job run twice to check it). Four pieces:
///
///   Open(…, new VeinOptions { Pure = true })   the rules cannot depend on the machine
///   Execute(event, fields, budget)            one input, with a step budget that FAILS (VeinOutcome)
///   StateHash()                               one SHA-256 for the whole state, the same on every replica
///   Checkpoint() / Restore(c)                 put the state back after work that did not finish
///
/// plus Reseed(seed), so `random()` is unpredictable in advance and identical everywhere.
public sealed partial class VeinGame
{
    /// `Open`, with options — `Pure` refuses a program that could disagree with itself on another machine.
    public static VeinGame Open(string path, VeinRuntime runtime, VeinOptions options)
    {
        var game = new VeinGame(path, runtime, options);
        var prepared = game.Prepare();
        game.Diagnostics = prepared.Diagnostics;
        game.RuntimeNote = prepared.Note;
        game._pending = prepared.Runtime;
        game._pendingModule = prepared.Module;
        return game;
    }

    /// Why the program at `path` is not pure (VS0364 errors), without running it; empty when it is. What
    /// `veinc check --pure` prints. Compile errors come back as they are.
    public static IReadOnlyList<Diagnostic> CheckPure(string path)
    {
        var (module, diagnostics) = Compile(System.IO.Path.GetFullPath(path));
        return module is null ? diagnostics : PurityProblems(module, System.IO.Path.GetFullPath(path));
    }

    private IReadOnlyList<Diagnostic> PurityProblems(IrModule module) => PurityProblems(module, Path);

    private static IReadOnlyList<Diagnostic> PurityProblems(IrModule module, string path) =>
        Purity.Check(module)
            .Select(p => new Diagnostic(Severity.Error, "VS0364",
                $"not allowed in a pure program — replicas would disagree: {p.What} (in {p.Where})",
                new Compiler.Lexing.SourceSpan(path, 1, 1, 0)))
            .ToList();

    /// Start `random()`, `pick` and `chance` over from `seed` (both runtimes).
    public void Reseed(int seed) => _runtime?.Reseed(seed);

    /// Raise one of the program's events, as `Emit` does, with at most `budget` steps (0: no limit; the
    /// interpreter counts them, compiled code does not). The outcome says whether the work FINISHED. When it
    /// did not, the world may be half-changed: a host that needs all-or-nothing takes a Checkpoint first and
    /// Restores it.
    public VeinOutcome Execute(string eventName, IReadOnlyDictionary<string, object?>? fields = null, long budget = 0)
    {
        if (!Running) return new VeinOutcome(false, 0, "not running");
        var outcome = _runtime!.Execute(eventName, fields, budget);
        Changed();
        return outcome;
    }

    /// SHA-256 of the whole state, as lowercase hex — equal on every replica that ran the same inputs, and
    /// on both runtimes. Canonical: identities by id; each one's marks, shapes and fields by ordinal name;
    /// numbers in invariant round-trip form, each value tagged with its type; then every shard's vars, by
    /// shard and name.
    public string StateHash() =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(StateText()))).ToLowerInvariant();

    /// The canonical text StateHash hashes — readable, one line per mark, shape, field and var, so two
    /// replicas that disagree can be diffed to the field that differs.
    public string StateText()
    {
        var text = new StringBuilder();
        foreach (var e in Entities.OrderBy(e => e.Id))
        {
            text.Append('E').Append(e.Id.ToString(CultureInfo.InvariantCulture)).Append('\n');
            foreach (string mark in e.Marks.OrderBy(m => m, StringComparer.Ordinal)) text.Append(" #").Append(mark).Append('\n');
            foreach (var (shape, fields) in e.Shapes.OrderBy(s => s.Key, StringComparer.Ordinal))
            {
                text.Append(" $").Append(shape).Append('\n');
                foreach (var (field, value) in fields.OrderBy(f => f.Key, StringComparer.Ordinal))
                {
                    text.Append("  ").Append(field).Append('=');
                    Canonical(text, value);
                    text.Append('\n');
                }
            }
        }
        foreach (var (shard, vars) in (_runtime?.ShardVars() ?? new Dictionary<string, IReadOnlyDictionary<string, object?>>())
                                         .OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            text.Append('S').Append(shard).Append('\n');
            foreach (var (name, value) in vars.OrderBy(v => v.Key, StringComparer.Ordinal))
            {
                text.Append("  ").Append(name).Append('=');
                Canonical(text, value);
                text.Append('\n');
            }
        }
        return text.ToString();
    }

    /// One value, unambiguously: a type tag, then the value (text length-prefixed, so no content can
    /// imitate a separator). Ints and floats are told apart, as the language tells them apart.
    private static void Canonical(StringBuilder text, object? value)
    {
        switch (value)
        {
            case null: text.Append('n'); break;
            case bool b: text.Append(b ? "t" : "f"); break;
            case int i: text.Append('i').Append(i.ToString(CultureInfo.InvariantCulture)); break;
            case long l: text.Append('i').Append(l.ToString(CultureInfo.InvariantCulture)); break;
            case double d: text.Append('d').Append(d.ToString("R", CultureInfo.InvariantCulture)); break;
            case float f: text.Append('d').Append(((double)f).ToString("R", CultureInfo.InvariantCulture)); break;
            case string s: text.Append('s').Append(s.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(s); break;
            case IReadOnlyDictionary<string, object?> map:
                text.Append('{');
                foreach (var (k, v) in map.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    text.Append(k.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(k).Append('=');
                    Canonical(text, v);
                    text.Append(',');
                }
                text.Append('}');
                break;
            case System.Collections.IEnumerable list:
                text.Append('[');
                foreach (object? item in list) { Canonical(text, item); text.Append(','); }
                text.Append(']');
                break;
            default: text.Append('?').Append(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
        }
    }

    /// The whole state now, to Restore later. Null on compiled code, which cannot yet say where its ids
    /// carry on — run replicas on the interpreter (VeinRuntime.Interpreter).
    public VeinCheckpoint? Checkpoint()
    {
        if (_runtime is not { NextId: { } next } r) return null;
        // Deep: a list in a field is the runtime's own object, and the program may change it in place later.
        var entities = r.Snapshot().Select(e => new VeinEntity(e.Id, e.Marks.ToList(),
            e.Shapes.ToDictionary(s => s.Key, s => (IReadOnlyDictionary<string, object?>)s.Value.ToDictionary(f => f.Key, f => Copy(f.Value), StringComparer.Ordinal), StringComparer.Ordinal))).ToList();
        var vars = r.ShardVars().ToDictionary(s => s.Key,
            s => (IReadOnlyDictionary<string, object?>)s.Value.ToDictionary(v => v.Key, v => Copy(v.Value), StringComparer.Ordinal), StringComparer.Ordinal);
        return new VeinCheckpoint(entities, vars, r.MutedMarks.ToList(), r.ShardsMutedByName.ToList(), next);
    }

    /// A value the runtime can change in place (a list, a record) copied all the way down; the rest as is.
    private static object? Copy(object? value) => value switch
    {
        null or string or bool or long or int or double or float => value,
        IReadOnlyDictionary<string, object?> map => map.ToDictionary(kv => kv.Key, kv => Copy(kv.Value), StringComparer.Ordinal),
        System.Collections.IList list => list.Cast<object?>().Select(Copy).ToList(),
        _ => value,
    };

    /// Put back a Checkpoint of THIS program: a fresh runtime of the same rules, resumed without its boot
    /// or `run once`, holding exactly the saved identities (same ids), shard vars and mutes, with ids carrying
    /// on where they did. The draws restart from 0 — Reseed after. False when nothing is running or the
    /// runtime cannot restore.
    public bool Restore(VeinCheckpoint checkpoint)
    {
        if (_module is null || _runtime is not InterpRuntime) return false;
        var next = new InterpRuntime(_module, FilesRoot ?? System.IO.Path.GetDirectoryName(Path)!, _sinks, _options.Pure);
        _runtime = next;
        Changed();
        next.Resume();
        foreach (var e in checkpoint.Entities.OrderBy(e => e.Id))
        {
            long id = next.SpawnAt(e.Id);
            foreach (var (shape, fields) in e.Shapes)
                foreach (var (field, value) in fields) next.Publish(id, shape, field, Copy(value));
            foreach (string mark in e.Marks) next.SetMark(id, mark);
        }
        next.ReserveIds(checkpoint.NextId);
        foreach (var (shard, values) in checkpoint.Vars)
            foreach (var (name, value) in values) next.SetShardVar(shard, name, Copy(value));
        next.RestoreMutes(checkpoint.MutedMarks, checkpoint.MutedShards);
        Changed();
        PublishView();
        return true;
    }
}
