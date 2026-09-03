namespace Vein.Compiler.Lexing;

public enum TokenKind
{
    // literals and names
    Ident,
    Int,
    Float,
    Percent,        // 30%  -- single token, see grammar 1.3
    String,

    // sigil-bound references; Text holds the name without the sigil
    ShapeRef,       // $Health
    EventRef,       // @Damaged
    MarkRef,        // #Enemy
    BuilderRef,     // &Console

    // keywords
    KwAnd, KwAs, KwAttach, KwAudience, KwBuilder, KwBridge, KwBring, KwBundle,
    KwBy, KwChance, KwCount, KwDestroy, KwEach, KwElse, KwEmit, KwEvent,
    KwFalse, KwFolds, KwFrom, KwHear, KwLet, KwMap, KwMark, KwMute,
    KwNot, KwOn, KwOr, KwPublicator, KwRandom, KwReturn, KwSettled,
    KwSf, KwShape, KwShard, KwShared, KwStart, KwSync, KwTarget, KwTick,
    KwTo, KwTransform, KwTrue, KwUnattach, KwUnmark, KwUnmute, KwUse,
    KwVar, KwWhen,

    // keywords added in Milestone 2 (see docs/SYNTAX-DECISIONS.md, docs/KEYWORDS.md §2)
    KwFn, KwType, KwEnum, KwIf, KwWhile, KwRepeat, KwBreak, KwContinue, KwMatch,

    // `base` as a `bring` argument means "use this parameter's declared default". Placed INSIDE the
    // IsKeyword range on purpose: that range is what lets a keyword still be spelled as a field or
    // member name (`from`, `count`, `to` all are), so `shape S { base: string }` and `c.S.base` keep
    // working. Only a BARE `base` in expression position is the keyword.
    KwBase,
    KwShardView,    // output-assembly construct: hears fragment events, concatenates a page
    KwEntity,       // the ECS entity type; as an expression, the nearest entity's int id
    KwIndex,        // as an expression, the nearest loop's 0-based iteration counter
    KwApp,          // app manifest: the set of bundles that compose a project

    // punctuation
    LBrace, RBrace, LParen, RParen,
    LBracket, RBracket,     // list literals / indexing (D9a)
    Question,               // `?` — fill-the-rest placeholder in emit/bring
    Arrow,          // ->
    Dot, Colon, Comma, Pipe,

    // assignment
    Assign, PlusEq, MinusEq, StarEq, SlashEq,

    // comparison
    Eq, Ne, Lt, Gt, Le, Ge,

    // arithmetic
    Plus, Minus, Star, Slash, Mod,

    // structural
    Term,           // virtual statement terminator inserted at newline
    EndOfFile
}
