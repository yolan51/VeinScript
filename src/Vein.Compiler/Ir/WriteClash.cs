namespace Vein.Compiler.Ir;

/// Two shards wrote DIFFERENT values to one shape field of one identity in the same phase, and the field has no
/// fold (`replace`, the default, or `first`). Units of one phase — the hearers of one event, every `each tick`,
/// every `settled` — commit together (RULES 11–13); a fold combines their writes in any order, but without one the
/// order the shards were registered in decides which value is kept, so reordering two `load` lines or adding a
/// bundle changes the game and nothing says so. Both runtimes keep the value as before and raise this as a
/// @DiagnosticRaised, once per field and pair of shards for the run.
///
/// Found at the commit, not by `veinc check`: whether two writes reach the SAME identity, and whether two modes
/// ever run in one frame, is only known while the program runs — a static check fired 63 times in the corpus,
/// almost all for systems moving different identities (Cameras' Orbit and Fly). Two writes by one shard are not a
/// clash: they run in the order written, which no other bundle can change. A write that leaves the field as it
/// was is no write at all (the compiled fold cannot see it either).
/// The unit a contribution came from, as a diagnostic names it: its shard, its phase (`tick`, `hear @E`), and where.
public sealed record StoreWriter(string Shard, string Phase, string File, long Line);

public static class WriteClash
{
    /// A clash, for the message: the field, and the first writer and value against the first that disagreed.
    public sealed record Clash(string Shape, long Entity, string Field, string FirstWriter, object? FirstValue,
                               string SecondWriter, object? SecondValue);

    /// The message — word for word in both runtimes.
    public static string Describe(Clash c) =>
        $"`${c.Shape}.{c.Field}` has no fold and was written as {PayloadFold.Show(c.FirstValue)} by {c.FirstWriter} and as " +
        $"{PayloadFold.Show(c.SecondValue)} by {c.SecondWriter} in one phase: the order the shards run in decides which is kept, " +
        "and adding, removing or reordering a bundle changes it. Give the field a fold (`folds max`, `folds sum`, `folds any`), " +
        "or let one shard write it.";

    /// The order clashes found in one commit are reported in — the same in both runtimes, whatever order each
    /// gathered them in: by shape, then identity, then field.
    public static IEnumerable<T> InOrder<T>(IEnumerable<T> clashes, Func<T, Clash> of) =>
        clashes.OrderBy(c => of(c).Shape, StringComparer.Ordinal).ThenBy(c => of(c).Entity).ThenBy(c => of(c).Field, StringComparer.Ordinal);

    /// The key a clash is said once under, for the run.
    public static string Key(Clash c) => $"{c.Shape}.{c.Field}|{c.FirstWriter}|{c.SecondWriter}";
}
