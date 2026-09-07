namespace Vein.Compiler.Tooling;

/// Where a rule is written down, and the one line of background a message has no room for.
public sealed record DiagnosticNote(string Code, string Why, string Doc);

// WHY: the diagnostics already explain themselves and name their own fix — VS0228 ends "Add the value,
// or write `?` to fill the rest with typed zeros on purpose". Restating those here would be a second
// copy of the same sentence, drifting the moment one of them is reworded.
//
// So this carries only what a message CANNOT: why the rule exists, and where the reasoning is written
// down at length. That is the question a message leaves behind — not "what do I type" but "why is this
// a rule at all" — and the answer is usually a paragraph in RULES.md that took a mistake to learn.
//
// Silent on codes with nothing extra to say. A guide that produced filler for all 60 would teach you to
// stop reading it.
public static class DiagnosticGuide
{
    private static readonly DiagnosticNote[] Notes =
    {
        new("VS0204", "A builder's parameters are positional, so an extra argument has no slot to land in.",
            "docs/RULES.md — 'bring B(…) too many args'"),

        new("VS0205", "Reported at every CALL site rather than at the declaration that caused it, which is why one bad shape can light up a whole file.",
            "docs/RULES.md §65"),

        new("VS0212", "A console address is an identity, but the runtime never checks one — ConsoleBus concatenates it into an OS pipe name and a miss is silently swallowed. This is the only thing that catches a typo'd address.",
            "src/Vein.Compiler/Tooling/ConsoleGraph.cs"),

        new("VS0216", "Reach it by its `*Author.Bundle.Publicator.member` path instead; rule 17b allows one there. Or alias the bundles — `use Combat as C` imports qualified and widens no bare name, so two aliased bundles cannot be ambiguous with each other (rule 18b).",
            "docs/RULES.md §285"),

        new("VS0217", "`use` cannot rebind a built-in — the built-in wins, and the shadowed member becomes unreachable by its short name. `use Console` silently shadowing spawn() is the bug this was added for.",
            "docs/RULES.md §283"),

        new("VS0218", "A bundle that declares ANY mark has all of its mark names checked. Declaring one turns the check on for the file.",
            "docs/RULES.md §255"),

        new("VS0220", "A warning, because it fires for any bundle in the index whether or not this program links it. The error is VS0332, at the point an app actually folds two disagreeing declarations into one component.",
            "docs/RULES.md §252"),

        new("VS0332", "Unifying by bare name is deliberate — it is how a capability bundle sees the principal's data (rule 15b). Unifying two declarations that DISAGREE is a name clash wearing that feature's costume: one bundle would silently win by module order, and the other's shards would read fields the component does not have. So the app does not link.",
            "docs/RULES.md §252"),

        new("VS0221", "Only an identity template can be bound with `as`; a fragment builder has no identity to bind.",
            "docs/RULES.md §82"),

        new("VS0222", "A count cannot be combined with `as`, because the name would bind only the last one made.",
            "docs/RULES.md §82"),

        new("VS0223", "The top of a shape-including block admits `bring` and `target` and refuses everything else.",
            "docs/SYNTAX-DECISIONS.md §317"),

        new("VS0225", "`&Builder.param` names that builder's parameter, so every bring in the block has to be that builder.",
            "docs/RULES.md §206"),

        new("VS0226", "Two includes give a parameter of the same name, and the short forms refuse to guess between them. `&Builder.$Shape.param` narrows to one.",
            "docs/RULES.md §210"),

        new("VS0227", "An emit's field names are checked against the event's declaration; the message names what the event does take.",
            "docs/RULES.md §353"),

        new("VS0228", "Positional binding means only a TRAILING run of parameters can be optional, so a short call leaves the last ones empty rather than the ones you skipped.",
            "docs/RULES.md §352"),

        new("VS0230", "Checked against the parameter's declared type — `bring Row(42, \"words\")` with those the other way round is the case this catches.",
            "docs/RULES.md §355"),

        new("VS0231", "`base` takes a parameter's DEFAULT. Without one there is nothing to take, so it falls back to a typed zero — which is legal and rarely what was meant.",
            "docs/RULES.md — `base`"),

        new("VS0233", "The whole value of an ascription is that its fields get checked, so a shape that does not exist checks nothing.",
            "docs/RULES.md §391"),

        new("VS0234", "A name that is neither built in nor declared by any `fn`/`SF`. The call would answer nothing — empty text, zero, false — and a program full of them compiles, builds and runs in silence, which is how a typo'd `isNumbre` guard let every value past.",
            "docs/RULES.md — built-ins"),

        new("VS0235", "The built-ins are total: a wrong argument count answers an empty value rather than failing, so `substring(s)` is \"\" and `int()` is 0. Defined, but indistinguishable from a real answer — which is why the count is worth saying out loud.",
            "docs/RULES.md — built-ins"),

        new("VS0236", "A query's binding reads fields, and the shapes it names are what give it fields to read — `target #Enemy as e` leaves `e` with nothing on it. It was also silently skipped by the C# backend, which has no query-by-mark, so the same program did one thing interpreted and another compiled.",
            "docs/RULES.md — target"),

        new("VS0006", "Deliberate, not missing: logic is spelled in words, so inequality is `not (a == b)` and negation is `not x`. `!` is kept unlexed for a future sigil. The lexer still hands the parser the token you meant, so this is one error rather than a cascade about braces.",
            "docs/SYNTAX-DECISIONS.md — D12"),

        new("VS0007", "Builders and events can include a `$Shape`; a shape body takes fields only, because components unify by bare name and an include would be a second definition of the same component. Put the include one level out, in the builder or event that carries this shape.",
            "docs/RULES.md — 15b"),

        new("VS0008", "`not` takes a unary operand, so `not x == y` is `(not x) == y`. For a number the two readings agree by accident; for a string `not name == \"\"` is false for every input. The parentheses are the whole difference.",
            "docs/SYNTAX-DECISIONS.md — D12"),
    };

    private static readonly Dictionary<string, DiagnosticNote> ByCode =
        Notes.ToDictionary(n => n.Code, StringComparer.Ordinal);

    /// Background for a code, or null when there is nothing to add beyond the message itself.
    public static DiagnosticNote? For(string? code) =>
        code is not null && ByCode.TryGetValue(code, out var note) ? note : null;

    /// Every code with a note, for tests and for a listing.
    public static IReadOnlyList<DiagnosticNote> All => Notes;
}
