# VeinScript — the rules that bite

Written for someone meeting this language cold, and weighted by what **costs time** rather than by what
is merely true. Every rule below was learned by getting it wrong first. [LANGUAGE.md](LANGUAGE.md) is the
spec and [samples/LANGUAGE-TOUR.vein](../samples/LANGUAGE-TOUR.vein) is the annotated tour; this is the
short list of things that will otherwise be re-derived.

**When this document is wrong, these are:** the lexer's `Keywords` map
([Lexer.cs](../src/Vein.Compiler/Lexing/Lexer.cs)) for what is a keyword · `samples/`, because
`SamplesTests` compiles every `.vein` in it, so anything there provably parses · then the prose docs.
*Verified 2026-08-31.*

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

**4. All 62 lexer keywords are reserved as EXPRESSIONS; most are still legal as field and member names.** `target` is a keyword,
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

**12c. A nested `target` reads EMPTY from the outer binding — silently.** Inside
`target $A as x { target $B as y { … } }`, `x.A.field` yields empty rather than `x`'s value: Lower emits
a nameless `IrSelfRef` for every target binding and the interpreter resolves it to `_targetBinds[^1]`,
the innermost loop. Indistinguishable from correct in a single loop. Keep queries flat — carry what the
inner loop needs as a value (an `Entity` field plus a `fn`), as `samples/web_app` does for handler names.

A `hear` binding is NOT affected — `r.path` read inside a `target` loop is correct, which is what lets
the nav in `samples/web_app` bold the current page. The bug is target-inside-target only.

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

**15b. COMPONENTS UNIFY BY BARE NAME, and a `shape` cannot include a `shape`.** `attach $Position`
lowers to the name alone, and linking folds every bundle's types into one table keyed by it — deliberately,
since that is how a capability bundle sees the principal's data. So two bundles saying `$Position` share
one component whether or not they agree on its fields. Only builders and events can include a `$Shape`;
a shape body takes fields, so reusing `Vein.Transform.Spatial.$Position` means retyping its fields. Get
them wrong and it is **VS0220** at declaration, or **VS0332** when an app links both.

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

**18. `use` only WIDENS what a bare name may mean.** Precedence is local declaration → built-in → `use`
fallback. A built-in wins and the shadowed member is reported as **VS0217** (`use Console` used to
capture `spawn`, so `let e = spawn()` built no entity). Two used bundles exporting one name is
**VS0216** — reach for the `*Author.Bundle.Publicator.member` path, where rule 17b allows one.

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
