# VeinScript — the rules that bite

Written for someone meeting this language cold, and weighted by what **costs time** rather than by what
is merely true. Every rule below was learned by getting it wrong first. [LANGUAGE.md](LANGUAGE.md) is the
spec and [samples/LANGUAGE-TOUR.vein](../samples/LANGUAGE-TOUR.vein) is the annotated tour; this is the
short list of things that will otherwise be re-derived.

**When this document is wrong, these are:** the lexer's `Keywords` map
([Lexer.cs](../src/Vein.Compiler/Lexing/Lexer.cs)) for what is a keyword · `samples/`, because
`SamplesTests` compiles every `.vein` in it, so anything there provably parses · then the prose docs.
*Verified 2026-09-07.*

---

## 1. Writing any line

**1. A newline ends a statement — so a continuation must END the line it continues from.**
The lexer emits a terminator at `\n` only when the previous token *can* end a statement, and `+` cannot.

```
markup = "<a>" +          // RIGHT — trailing +, no terminator, expression continues
         "</a>"

markup = "<a>"            // WRONG — the line ends, and the next one is a new statement
       + "</a>"           //         beginning with `+`
```

Commit `62e4189` reverted the wrong form and concluded wrapping was impossible; it is not, it is just
trailing-only. The same holds for a trailing `,` in an argument list.

**2. There is no `!=`, and no `!` at all.** Inequality is `not (a == b)`; negation is `not x`. Logic in
this language is words, because `and`/`or`/`not` are the operators
([D12](SYNTAX-DECISIONS.md#not)) — which is also the general rule: a C-family construct is not adopted
here just for being familiar.

**2b. `} else {` goes on ONE line.** A newline after `}` ends the statement (rule 1), so an `else`
beginning its own line is orphaned and reports **VS0104: Unexpected 'else' in expression**. A `match`
arm may sit on its own line — `ParseMatch` reads `else` explicitly — which is why
`samples/console_roles.vein` appears to contradict this and does not.

**3. A string literal is one line.** `\n \t \r \\ \"` are the escapes; a literal newline inside quotes is
VS0003. To emit a multi-line string, use `\n` and wrap the source with trailing `+` per rule 1.

**4. All 63 lexer keywords are reserved as EXPRESSIONS; most are still legal as field and member names.** `target` is a keyword,
so `builder Clock { target: string }` is a parse error — it was renamed to `id`. Check the map before
choosing a name; do not count entries by line, because the table holds two per line.

---

## 2. Builders

**5. A builder's OUTPUT FIELD NAME decides what it makes.** Not a keyword, not a suffix — the name.

| field | makes |
|---|---|
| `markup` | an `@Html` fragment |
| `css` | an `@Style` fragment |
| `code` | an `@Script` fragment |
| `line` | an `@Print` line |
| a `mark` member | an **identity** (see rule 7) |
| *(inherited)* | whatever `from Base` builds (rule 7c) |
| *(none of the above)* | emits `@<BuilderName>` carrying all its params |

**6. Therefore `markup`, `css`, `code` and `line` cannot be parameter names.** A builder written
`CodeBlock { code: string … }` is read as "this writes on the @Script channel", has no value for it, and
reports **VS0205 at every call site** while saying nothing at the declaration that caused it.

**7. A `mark` member makes the builder an identity template.** Only an identity can be marked, so the
keyword is the discriminator:

```
builder Unit { $Health $Shield   mark #Unit }

bring Unit(10, 6)     ==     let e = spawn()
                             attach $Health to e { hp: 10 }
                             attach $Shield to e { sp: 6 }
                             mark e #Unit
```

**7b. `bring X(…) as name` binds what a template built.** `bring` is a statement and the identity it
spawns lives in a local the program never sees, so anything that must REFER to it needed a hand-written
`spawn`/`attach`/`mark`. `as` fixes that, and means what it means in `target … as self`. Only an
identity template can be bound — `as` on a fragment builder is **VS0221** — and a count cannot be
combined with it (**VS0222**), since the name would bind only the last one.

**7c. `builder BigCoin from Coin { value = 5 }` — a VARIANT fixes a base's arguments by name.** A builder
is already a prefab, so what a variant adds is *naming* a set of its arguments. It takes the base's
members, may add shapes and marks of its own, and inherits the base's marks — or it would not be the same
kind of thing, and a shard reacting to coins would miss it. A chain is allowed (`GoldCoin from BigCoin`)
and a cycle is **VS0239**; an unknown base is **VS0238**.

**BY NAME, and a fixed parameter is then not a parameter at all** — it consumes no argument, so
`bring BigCoin(x, y, z, sound)` passes four where the base takes five and the rest still bind in order.
Fixing by position would break the moment the base gained a shape, which is exactly the failure of the
alternative: writing a second builder that repeats the shape list duplicates the definition instead of
deriving from it, so the day `Coin` gains a shape the copy silently stops being a coin. `veinc scaffold`
and the editor's `?` list only the remaining parameters, because they flatten a variant the same way.

**8. A `$Shape` include is a COMPILE-TIME field expansion, and `bring` attaches nothing.** The include
copies the shape's fields into the signature; it does not make the built thing carry the component. So:

```
builder Card { $Card   markup = … }
bring Card("a", "b")            // emits @Html
target $Card as self { … }      // matches ZERO — nothing was ever attached
```

To get both, `attach` the shape to an identity as well; rule 7 is the shorthand for exactly that.

**9. Arguments bind positionally across includes, in declaration order.** `{ $Health $Shield }` takes
`(hp, sp)`. Ask rather than counting: `veinc scaffold <file> '&Name'` prints every slot with the shape it
came from.

---

## 3. Identities

**10. `spawn()` is the only way to make an entity.** A program with no `spawn` has an empty world and
every `target` matches nothing — which looks exactly like a query with no results.

**11. `spawn` is immediate; `attach`, `unattach`, `mark`, `unmark` and `destroy` are DEFERRED** to the
commit point, after the folds. That is what stops one unit seeing a world another half-changed.

**12. A structural change is invisible inside the event that made it.** Attach in a `hear` handler and a
`target` in that same handler will not see it; the next event will. Seed worlds in `run once`.

**12b. …but the NEXT event sees it, because commit is per EVENT.** `Drain` commits after every event's
handlers, not once per drain: *"handlers run sequentially, so the next event sees what the last one
built"* ([Interp.cs](../src/Vein.Compiler/Ir/Interp.cs)). So wiring written in one event is readable in
the next, and that is the only way to arrange *change it, then read it* — see `samples/dom_rewire.vein`.
Two consequences: **`run once` commits once for ALL shards**, so one shard cannot wire what another just
spawned; and within a single `hear @Request` a wiring shard and a rendering shard cannot see each other,
which (with rule 22) is why a web page cannot wire-then-render in one request.

**12c. A nested `target` binds BY NAME, so both bindings are readable.** Inside
`target $Deck as d { target $Card as c { … } }`, `d.Deck.title` is the deck's and `c.Card.face` is the
card's, in both runtimes. `samples/entities_nested.vein` is the guard, and it is in `check-backend`
because each runtime used to get this wrong in its own way.

**This rule used to say the opposite, and the history is worth one line.** `Lower` emitted a NAMELESS
`IrSelfRef`, so nothing recorded which loop a reference meant: the interpreter resolved every one to the
innermost binding and `d.Deck.title` came back empty — a plausible blank, never an error,
indistinguishable from correct in a single loop. The C# backend had it worse: both loops emitted
`foreach (var __e …)` and both declared `self_<Comp>`, so a nested query did not compile at all. The
advice here was *"keep queries flat"*, and `samples/web_app` still carries the workaround it prescribed
— an `Entity` field plus a `fn` to look the value up — which is now a choice rather than a requirement.

A `hear` binding was never affected: `r.path` read inside a `target` loop was always correct, because a
handler's parameter is an ordinary named local. That is precisely the mechanism the fix gave target
bindings — `IrSelfRef` carries its binding's name and resolves out of the same locals.

**13. `folds` reconciles concurrent writes; `settled` is where the reconciled value is readable.** Two
shards doing `hp -= 1` in one tick give `hp - 2`, because each contributes a *delta* from its own
snapshot. Reading `hp` during the tick sees an unreconciled value — death checks belong in `settled`.

**13b. `folds` ACCUMULATES across ticks — it does not recompute.** A `folds sum` field keeps its value
and each tick's contributions land on top: a per-tick "count my children" reads 2, then 4, then 6.
Deriving a quantity fresh means zeroing it first; the fold will not. And a child can write its PARENT's
component — `c.Parent.of.Deck.count += 1` — because an entity plus a component name is a handle, which
is how `samples/entities_tree.vein` aggregates without the parent holding a list of children.

**14. `target` binds ONE shape per loop, and nothing can ask an identity which shapes it carries.**
Several shapes are an AND (`target $A $B`). There is no dispatch — a heterogeneous ordered sequence
cannot be rebuilt by query, which is why `/docs` in `samples/web_app` renders from a literal sequence and
only its section index is a query.

**14b. QUERY ORDER IS SPAWN ORDER unless you ask otherwise.** A `target` yields entities ascending by id,
ids are handed out by `spawn`, and `bring` spawns — so by default `query order == spawn order == the
order you called bring`. Both runtimes: `EntityStore.Query` ends `.OrderBy(e => e)`, and the backend's
`VeinWorld` iterates a `SortedSet<int>`.

**`by Shape.field` sorts it** — at READ time, so entity ids never move and nothing referring to a row is
disturbed:

```
target $Row #Row by Row.rank  as r { … }     // int
target $Row #Row by Row.title as r { … }     // string, compared ORDINALLY
```

Ties keep spawn order (the sort is stable). Strings compare **ordinally** on both sides, deliberately:
C#'s default string comparison is culture-sensitive and would order differently per machine.
`samples/entities_ordered.vein`.

This is also what orders a PAGE, because the `bring` that emits a fragment sits inside the loop — so the
fragments queue in the query's order. The `bring` that SPAWNS a row is a different one, and its order
stops mattering the moment you sort at read time.

**14c. `Index` is the nearest loop's 0-based counter, and it does NOT sort.** The parallel to `Entity`:
that names WHICH identity, this names WHICH ITERATION. Works in `target` (query and collection) and in
`repeat`, where it is the same number `as i` binds. Use it for numbering, striping, first/last, a top-N
cutoff — `bring Item(Index + 1, …)`.

It counts position in the order the loop already yields, which is spawn order (14b), so
`if r.rank == Index + 1` compares a key against a position and matches only by luck. To order by a key,
use one pass per key value. Nested loops shadow and restore, like `Entity`.
`samples/entities_index.vein` is in `check-backend` because the two runtimes could easily have disagreed:
with two components the interpreter filters before iterating while the emitted C# `continue`s inside the
loop, so the counter has to be bumped after those guards or the backend numbers entities the interpreter
never sees.

**14d. `ordered by &Builder.$Shape.param { bring … }` sorts the BRINGS — the other place an order comes
from.** Rule 14b sorts a *query*, which needs identities to query. A `bring` on a FRAGMENT builder emits
its `@Html` the instant it runs and leaves no identity behind — so its call order IS the output order,
and nothing can sort it afterwards. That is most of a web page.

```
ordered by &Card.$Card.rank {
    bring Card("delta", 4)      // emitted second
    bring Card("Zeta", -2)      // emitted first
}
```

The key is a builder **parameter** (`$Shape` includes expanded) — that is what a `bring` supplies, and a
builder may also have loose parameters belonging to no shape. Both qualifiers are optional and each
narrows one step:

| written | means |
|---|---|
| `param` | resolved per bring, so a block may mix builders that each have it |
| `&Builder.param` | that builder's parameter; every bring must be it, else **VS0225** |
| `&Builder.$Shape.param` | and contributed by that include |

The middle segment is not decoration: `builder Both { $A $B }` with `rank` in **both** shapes gives two
parameters of that name, and the bare form refuses to guess between them (**VS0226**, which names the
candidates and spells the fix). A parameter that does not exist is **VS0224**; a statement other than
`bring` or `target` at the top of the block is **VS0223**.

Keys are all evaluated BEFORE any body runs, so a bring cannot change a key that has not been read yet.
Same comparer as an ordered query: numbers numerically, strings ordinally, stable so ties keep the
order they were reached in. On an identity template it bakes the order into the ENTITY IDS, so every
later query gets it free without a `by` clause — `samples/entities_bring_order.vein`.

**14e. `ordered by` takes a `target` loop, which is how DATA gets ordered.** Rule 14d's block of literal
brings only sorts what you typed out. Rows from JSON or a database arrive as a *list*, and one written
`bring` inside a loop becomes N of them — a count nothing knows until the list is in hand.

```
ordered by rank {
    bring Row("literal", 0)              // literals and loops mix; they are the same kind of thing
    target doc.rows as row {
        if row.rank > 0 {                // filtering is why they are collected, not counted ahead
            bring Row(row.title, row.rank)
        }
    }
}
```

Only `bring` is reordered. An `emit` or a `let` inside the loop runs where it stands, in the order the
loop reaches it — the brings are set aside with their keys and replayed once the loop has finished.

`Index` inside a deferred bring is the SOURCE position, not the sorted one, and it survives the wait:
the body runs after the loop has ended, so the counter, the `target` binding and the current entity are
all captured per row and put back before it runs. Getting that wrong is silent — the fields come out
blank rather than wrong — because `row` lowers to the nameless `IrSelfRef` (rule 12c) and reads a bind
stack, not a local. `samples/entities_bring_rows.vein`, and `samples/json_roundtrip.vein` for the JSON
case.

**15. `$Enemy` and `#Enemy` are different things.** Different keyword, different sigil; a program may use
both. In emitted C# the mark becomes `Marks.Enemy` and the shape `Enemy`.

**15b. COMPONENTS UNIFY BY BARE NAME, and a `shape` cannot include a `shape` (VS0007 says so).** `attach $Position`
lowers to the name alone, and linking folds every bundle's types into one table keyed by it — deliberately,
since that is how a capability bundle sees the principal's data. So two bundles saying `$Position` share
one component whether or not they agree on its fields. Only builders and events can include a `$Shape`;
a shape body takes fields, so reusing `Vein.Transform.Spatial.$Position` means retyping its fields. Get
them wrong and it is **VS0220** at declaration, or **VS0332** when an app links both.

**15c. A shape may BRING marks, and they arrive and leave with it.** `shape $GameCamera { zoom: float,
#CameraFollow }` — or `mark #A #B` in the body, or several bare marks between the fields — means carrying
this shape is what wearing these marks means. Attaching it adds them and `unattach` removes them, **in the
same commit**, so a `target $GameCamera #CameraFollow` matches on the very frame the shape lands. That is
the point: an adoption shard that marks carriers afterwards can only run a frame later, and an event aimed
at the identity in between is *dropped, not delayed*. There is no refcount, so detaching also removes a
mark that was set by hand — the alternative leaves the mark on an identity whose data is gone. A builder
including such a shape builds an **identity** even with no `mark` line of its own (rule 5), so moving the
mark from the builder into the shape never quietly changes what `bring` does.

**16. A mark is a name unless declared.** `mark #Enemy` at bundle or publicator level declares it, and a
bundle that declares *any* mark has its mark names checked — an undeclared one is **VS0218**. A bundle
that declares none is unchecked, so a misspelling there is silently a new mark. `shared("…")` exports a
mark, and one reached through `use` counts as declared; the gate stays on the bundle's *own*
declarations, so adding a `use` never starts checking a file that did not opt in.

---

## 4. Bundles

**17. Only `shared("…")` inside a `publicator` crosses a bundle boundary.** Everything else is
bundle-private, and a qualified reference to it does not resolve.

**17b. The qualified `*A.B.P.$Shape` / `#Mark` form is for INCLUDES and DISCOVERY, not use sites.**
`target`, `mark`/`unmark`, `audience` and `match` take a **bare** `$Shape`/`#Mark` — the qualified form
parses there and resolves to nothing. Reach it by `use`ing the owning bundle instead. `veinc symbols`
lists what is exported.

**17c. An IMPORTED `$Shape` becomes a real component here the moment something attaches it.** A
qualified include (`builder Conn { *Vein.Rest.Db.$Connection  mark #Conn }`, or a stdlib builder you
`bring`) used to expand FIELDS only, and a field access resolves to a component solely when the module
declares one of that name. So the attach landed, `target $Connection #Conn` matched the entity, and
`c.Connection.base` read as **the empty string** — no diagnostic, anywhere. `Lower.RegisterImportedShape`
now adds the type to the module, and rule 17b still holds at the use site: `target` takes the **bare**
`$Connection`. A mark riding in on an imported builder likewise no longer needs a local `mark` line —
it is declared in the bundle that owns the builder, and reporting **VS0218** for it pointed the author
at a span in stdlib source.

**18. `need "Author.Bundle"` names what this bundle is built on, and WIDENS what a bare name may mean.**
Precedence is local declaration → built-in → `need` fallback. A built-in wins and the shadowed member is
reported as **VS0217** (`need "Vein.Console"` used to capture `spawn`, so `let e = spawn()` built no
entity). Two needed bundles exporting one name is **VS0216** — reach for the
`*Author.Bundle.Publicator.member` path, where rule 17b allows one.

**And it LINKS.** In an app, everything a loaded bundle needs is linked too — transitively, each file
once, a cycle terminating on the same set — so `app KitDemo { load "KitDemo.vein" }` is the whole
manifest and the game is the principal. The standard library is the one exception: it declares no
shards, so a `need` on it resolves names and links nothing, and its every shape stays out of the app's
shared table (where a game's own `$Counter` beside `Vein.Core.Quantity.$Counter` would be VS0332).

The **author is part of the name**, and that is why it is a quoted string rather than a bare word. The
retired `use N` matched on the bundle segment alone, so `use Combat` matched every author's `Combat` at
once: two were indistinguishable and collapsed into VS0216, after which the reference resolved to
nothing. It also validated nothing — `use Movemnet` was completely silent, and the failure surfaced later
and elsewhere as **VS0234** at each call that needed the widening, so a misspelled bundle read as a
broken call. A `need` that names no bundle on the search path is **VS0340** at the declaration, and one
written without an author is **VS0339**. `use` itself is **VS0338**, which names the `need` line to write.

**18b. `need "Author.Bundle" as Y` imports QUALIFIED, and widens nothing.** The alias names the head of a
`*` path — `need "Vein.Math" as M` makes `*M.Roots.sqrt(16.0)` resolve — and deliberately does *not* make
bare `sqrt` mean anything. That is what makes it the answer to VS0216: two bundles exporting one name are
ambiguous only because both contribute bare names, so aliasing both removes the ambiguity instead of
restating it, and each vocabulary stays reachable under its own head. Only the FIRST segment substitutes;
an alias names a bundle, and a publicator that happens to share its spelling is not one. This is
`import numpy as np`, not `from numpy import *`.

**19. A bundle can span files, and FRAGMENTS MERGE BEFORE THE MAIN FILE.** `publicators/*.vein` is API,
`shards/*.vein` is behaviour, and a fragment carrying an API declaration is **VS0321**. Member order is
the order shards run in, so a shard declared last in the main file still runs last — and a fragment
therefore cannot close a phase.

**20. Events unify by name across a linked app.** One queue, one `_self`: an `emit` in one bundle reaches
a `hear` in another, and a capability bundle can bind the socket the whole app answers on.

---

## 5. Running, and measuring

**20b. An SF runs INLINE; a shard hearing an event does not.** That is the difference between a helper
whose `bring`s land where you called it and one whose fragments arrive after everything already queued.
`navBar(r.path)` in `samples/web_app` is an SF for exactly this reason — plus a shard on `hear @Request`
would emit for EVERY path, and an unrouted path emitting no `@Html` is how the view tells a 404 from a
page. A builder cannot hold statements at all, so it can never run a query.

**21. `bring` and `emit` QUEUE — they do not write.** The queue drains only after every handler of the
current event has finished. So a view that answered on `@Request` would always see an empty page, and an
assembly trigger has to land *behind* the fragments. See `Kernel` in `samples/web_app/web_app.vein`.

**22. `veinc serve` gives every request a fresh interpreter.** `run once` therefore re-runs per request,
and no page can see the last visitor's state. `veinc render` is the same pipeline, one-shot.

**23. Under Git Bash, pass `MSYS_NO_PATHCONV=1` for a `/` argument.** Without it
`veinc render f.vein /` becomes `C:/Program Files/Git/` and reports no `@Response` — which reads exactly
like a route that did not match.

**24. There are four checks, and they guard different things.**

| | |
|---|---|
| `dotnet test src/Vein.Tests` | behaviour |
| `bash tools/check-ir.sh` | the IR's *shape* — golden trees |
| `bash tools/check-backend.sh` | the backend's *meaning* — emitted C# compiled, run, diffed against the interpreter |
| `bash tools/check-perf.sh` | the backend's *speed* |

**24b. JSON crosses the boundary: `fromJson(text)` and `toJson(shape)`.** A parsed object is a
dictionary and a parsed array is a list, both of which the language already handles — field access
resolves against a dictionary, `target … as x` iterates a list, `len` measures either — so
`fromJson(body).rows` and `row.title` are the ordinary `.` with nothing added.

An integral number parses as an **int**, not a float, so `rank + 1` is integer arithmetic and prints `2`
rather than `2.0`. Malformed input is `null`, not a crash.

`toJson` takes a SHAPE ON an identity (`toJson(r.Row)`), never an identity: nothing can ask an entity
which shapes it carries (rule 14), so there would be nothing to write. Fields come out in declaration
order, so a round trip is stable.

Both are **interpreter-only**, and `veinc emit` says so before the generated C# fails to compile — a
value-returning builtin cannot be stubbed, because code that compiles and computes something else is
worse than code that does not compile. `samples/json_roundtrip.vein`.

**25. Measuring the backend: Release on both sides, and past JIT warm-up.** Timing the Debug CLI against
a Release backend inflates the ratio ~1.4×. And the generated code keeps getting faster for several
hundred frames — a window opening at frame 100 reports 6.6× where the steady state is 10–18×. Both
mistakes were made here before the harness was trusted.

**26. ARITY AND LITERAL TYPES ARE CHECKED — everything else about a value still is not.** Four checks
fire before the program runs, and the Workbench shows them because they are ordinary diagnostics:

| written | code | |
|---|---|---|
| `bring B(…)` too many args | **VS0204** | error |
| `bring B(…)` too few | **VS0228** | warning — the missing params read empty |
| `emit @E { typo: … }` | **VS0227** | warning — names what @E does take |
| `fn`/`SF` call, wrong count | **VS0229** | warning — local and `*A.B.P.` alike |
| literal of the wrong kind | **VS0230** | warning — `bring Row(42, "words")` |

`?` (FillRest) and a parameter with a **default** are the two ways to say "fewer on purpose", and
neither is reported. An `int` where a `float` is declared is widening, not a mismatch.

**What is deliberately NOT checked**, each for a reason:

- **A missing `emit` field.** It reads as empty, and that is load-bearing — `@Fetch` gained `headers`
  after programs were emitting it with three fields, and they still mean what they did.
- **A non-literal argument.** `IrExpr.ResolvedType` is never assigned and there is no inference pass, so
  the type of `a + b` or `row.title` is genuinely unknown. A warning that fires on correct code is worse
  than no warning, so anything but a literal is skipped.
- **Anything about DATA.** A JSON body, a database row, a form field — their shape is not in your source
  and no check can reach it. That is what a guard shard is for: *a static check catches what you wrote,
  a guard catches what arrived* (`samples/diagnostics_guard.vein`).

A missing value reads **empty, not zero**. `< 1` is true for it either way, but printing shows `[]`
against `[0]`.

**27. `target rows as row: $Row` — say what data from OUTSIDE looks like.** A collection binding is the
one thing nothing can infer: `fromJson` returns whatever the payload held, a query reply carries no
shape, and a field read off a parsed document is just a value. So `row.titel` was a silent nothing —
in exactly the code that talks to a schema someone else controls and changes.

The author knows, because they asked for those columns. The ascription says it once, at the boundary,
using the shape they have already declared for the same records:

```
target rows as row: $Task { bring Task(row.id, row.title, row.rank, row.done) }
```

- It describes a **RECORD, not a component**. Nothing is attached to an identity, so fields are read
  directly — `row.title`, not `row.Task.title`. That is the opposite of `target $Task #Task as t`,
  where the shape names a component ON an entity and the read is `t.Task.title`.
- **Optional, always.** Leave it off and the loop runs untyped, which is what you want while exploring
  a payload whose shape you do not know yet.
- A shape that does not exist is **VS0233** — the ascription's whole value is that fields get checked,
  so a typo silently switching that off would be its worst failure.
- It pairs with a guard rather than replacing one: *the ascription says what you EXPECT, the guard
  checks what ARRIVED* (`samples/diagnostics_guard.vein`).

**28. A CHARACTER IS A ONE-CHARACTER STRING.** `s[0]` yields one, `==` compares it, `<` orders it. There
is no `char` type, no `'a'` literal, no `$Char` shape and no `&Char` builder, because none of those buy
anything a one-character string does not already do — and a shape is for data an identity carries, while
a character carried by nothing is just a value.

So every classifier is an ordinary `fn`:

```
fn isDigit(c: string)  -> bool { return c >= "0" and c <= "9" }
fn isLetter(c: string) -> bool { return (c >= "a" and c <= "z") or (c >= "A" and c <= "Z") }
```

**Two strings compare ORDINALLY; anything else compares numerically.** This was a real bug until it was
not: all four of `< > <= >=` went through `AsDouble`, which is 0 for a non-numeric string, so
`c >= "a" and c <= "z"` was `0 >= 0 and 0 <= 0` — **true for every string**, silently. Ordinal and never
culture-sensitive, matching `EntityStore.OrderKey`, so an operator cannot disagree with the sort.

A **mixed** comparison is still numeric and a string operand is still 0, so `"5" > 3` is false. That is
a known wart, left alone: making it parse would add a loose coercion reaching every mixed comparison in
every existing program.

Capitals sort before lowercase, so a case-insensitive test is `lower(a) == lower(b)`.

**28a. Splitting is done by the HOST, and the exact rule each follows is part of the language** — not
because a program could not walk characters itself, but because doing so interpreted, one concatenation
at a time, is slow and because two of these carry a trap nothing can undo afterwards:

| | rule | for |
|---|---|---|
| `split(text, sep)` | exact separator, **keeps empties** | a blank CSV column is still a column |
| `lines(text)` | splits on line endings, **strips `\r`** | a file written on Windows |
| `words(text)` | runs of whitespace, **drops empties** | `"a  b"` is two words |
| `chars(text)` | one entry per character | walking a word |
| `trim(text)` | surrounding whitespace | |

All four hand back a **list**, so `target chars(word) as c { … }` is the same statement that walks
anything else. Alongside them, `code(c)` / `chr(n)` convert to and from a codepoint — what ordering
alone cannot do, since `chr(code(c) + 1)` is the next letter — and `upper` / `lower` fold case.

`lines` is not `split(text, "\n")`, and the difference is the trap: with `split`, a CRLF file leaves a
`\r` on every line, so `line == "end"` is false against `"end\r"` and **both sides look identical in
any output you print to check**. `words` is not `split(line, " ")` for the mirror reason — `"a  b"`
gives three pieces with an empty middle, and the empty one is indistinguishable from a real word once
you have it.

**CHARACTER WORK COMPILES; the splits do not.** `s[i]`, `len`, `code`, `chr`, `chars`, `upper`, `lower`
and string ORDERING all emit to C# and are diffed against the interpreter by
`samples/entities_chars.vein` in tools/check-backend.sh — so a program that reads text a character at a
time can go down the compiled path. `split`/`lines`/`words`/`trim`/`join`/`toJson`/`fromJson` stay
interpreter-only: each carries a rule (which empties survive, which line endings) that would have to be
reproduced rather than approximated, and `veinc emit` reports each call rather than emitting something
that computes a different answer.

`contains`, `startsWith`, `endsWith`, `indexOf`, `substring` and `replace` are built in alongside them,
and they compile. Their ABSENCE — not any missing syntax — is what made "if the line mentions ERROR"
unwritable, which is most of what a file-handling program spends its time doing. `s[0] == "F"` can only
ever test one character; a multi-character prefix needs `startsWith`, and writing it by hand
(`fn startsWith(s, p) { … }`) walks the string one interpreted concatenation at a time.

`indexOf` answers `-1` when the needle is absent — the one value a valid position can never be, so
`indexOf(s, x) >= 0` reads as "is in there" without a second call — and `substring` CLAMPS rather than
throwing, because a runtime is not a place to crash a console app over an index.

`samples/characters.vein` is the whole of this rule as a running program: classification, an identifier
validator, ROT13 with `code`/`chr`, and why case folding needs a function.

**28d. A conversion is named after the type, and `isNumber` is why it can be.** `int(x)`, `float(x)`,
`string(x)` and `bool(x)` convert between the scalar types. They needed **no new syntax**: `int` and
friends are not keywords — the parser only ever meets them in type position — so `int(x)` already
parsed as an ordinary call and merely resolved to nothing.

They are **total**. `int("abc")` is `0`, `float("")` is `0`, and nothing throws — the same choice
`substring` makes by clamping and `indexOf` makes by answering `-1`, and for the same reason: a
language with no `catch` has nowhere to put a guard.

Which is exactly why `isNumber(s)` exists beside them:

```
if isNumber(typed) { let n = int(typed) } else { … }
```

A total function buys its safety by making failure indistinguishable from a real answer — `int("abc")`
and `int("0")` are both `0`. Without `isNumber` this pair would be `fromJson` returning `null` again,
wearing different clothes.

`int` truncates toward zero rather than rounding, because it is asked for most often to index or to
count and 4.9 items is four. Parsing is **invariant-culture**, so `"1.5"` reads the same on a machine
whose decimal separator is a comma — otherwise a program would read its own saved files differently
depending on where it ran. And `bool(x)` reuses the interpreter's own truthiness, so it and `if x` can
never disagree.

All five compile. `samples/entities_convert.vein` is in `tools/check-backend.sh`, which is how a
locale-dependent parse would be caught rather than shipped.

**28e. Ordering decides in three steps, and text is never silently zero.** `<` `>` `<=` `>=` ask:

| operands | compared as |
|---|---|
| both text | **ordinal** — `"10" < "9"` is true |
| both read as numbers, parsed text included | numerically — `"5" > 3` is true |
| anything else | ordinal on their text — `"abc" > 3` is true, `'a'` after `'3'` |

The both-text rule is the load-bearing one: character comparison depends on it (28), and
`EntityStore.OrderKey` sorts the same way, so an operator that disagreed would be a second answer to
the same question.

The middle rule is newer, and it replaced a real trap. `AsDouble` answers `0` for anything that is not
a number, and mixed comparisons used to run through it — so **`"5" < 3` was `0 < 3`, true**, and
`"5" > 3` was false. Every ordering that mixed text and a number silently agreed the text was zero.
It survived because parsing text in a comparison would have been a new loose coercion reaching every
existing program. `int(x)` is what settled it: once the language can say what a numeric string means,
a comparison saying something else has nothing left to stand on.

Note what the third rule is **not**: a fallback to zero. `"abc"` against `3` compares `"abc"` with
`"3"` — deterministic, and the same place `==` lands when it cannot compare numerically.

**28b. A file is an event, and a failure is a message.** `*Vein.Files.Io.@ReadFile { path }` answers
with `@FileLoaded` or `@FileMissing`; `@WriteFile` answers with `@FileWritten` or `@FileMissing`. That
is the same shape as `@Fetch`/`@Fetched`/`@Failed` and a console send's `@Undelivered` — there is no
`catch` to write and no block to encircle.

The events are named `@ReadFile` and not `@Read` because the runtime routes on the UNQUALIFIED name, so
a host-handled name is taken for every program at once — and `@Read`/`@Write` are the two most likely
names for an event a program would declare itself.

Writing creates missing folders on the way. There is no sandbox, no directory listing (the answer would
be a list and a payload field holds a scalar) and no binary mode (`text` is a string).

**28c. Route files by `tag`, not by path.** `hear @FileLoaded` fires for **every** file the program ever
reads. With one file that is invisible; with nineteen, every handler opens `if f.path == "…"` — and
nothing there fails loudly. A typo means the block never runs, a renamed file has to be found in every
handler that mentioned it, and the error path needs its own nineteen guards.

So every file event carries a `tag` that the runtime hands back untouched:

```
emit *Vein.Files.Io.@ReadFile { path: settings, tag: #Config }

hear *Vein.Files.Io.@FileLoaded as f {
    match f.tag {
        when #Config { … }
        when #Log    { … }
        else         { … }          // the arm nineteen `if`s never had
    }
}
```

A mark in value position is its own name, so `#Config` at the emit and `when #Config` at the handler are
the same string with no quoting at either end — and `when` is checked against declared marks, so a
misspelled tag is a **compile error** instead of a block that silently never runs. `@FileMissing` and
`@FileWritten` carry it too, so the failure routes through the same `match` as the success.

The field is a **string** and not a mark type because tags are often computed: nineteen save slots want
`"slot" + n`, which no mark can spell. It defaults to `""`, so a program written before it existed is
unchanged. It is not a handle — nothing is allocated, nothing is closed, and two reads may share a tag
on purpose, which is how "all nineteen configs" is written. `f.path` is still there when a handler wants
to know which file it actually got.

`samples/file_tags.vein` is five files through two handlers, including a computed tag.
