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

**4. All 64 lexer keywords are reserved as EXPRESSIONS; most are still legal as field and member names.** `target` is a keyword,
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

**7d. A name is ONE declaration — there is no overloading.** Two `builder Line`, two `fn twice` or two
`shape $P` in one bundle is **VS0320**, at the second. It used to be silent: the second replaced the
first, so with the same parameter count every `bring Line(…)` ran a builder its author did not write, and
with a different count the only clue was a VS0228 describing the wrong one. "The same thing with fewer
arguments" is a default (`prefix: string = ""`); "the same thing with some arguments fixed" is a variant
(7c). Overloading by signature is an open design question, not a bug left in — a builder's name is also
the event it emits, a variant's base, an `ordered by` key and a `vein.lock` line, and each of those
would have to say which one it means.

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

**11b. A shape is ONE component per identity — `attach` REPLACES it, whole.** Attaching `$Price` to an
identity that already carries it never adds a second copy: `p.Price.amount` would no longer say which.
It replaces the component, and the replacement is the WHOLE component, so a field the new `attach` does
not write goes back to the shape's **default**, not to the value it had:

```
attach $Price to e { amount: 5, currency: "USD" }
attach $Price to e { amount: 9 }          // now 9 "EUR" — the shape's default, not "USD"

target $Price as p { p.Price.amount = 9 } // to change ONE field and keep the rest, assign it
```

Re-attaching is legal and sometimes the point — `samples/dom_rewire.vein` rewires every button's
`$Handler` that way — so it is not a warning. Where the compiler can be SURE the shape is already there
(inside a `target` that matched it, or after an earlier `attach` in the same block) it is note
**VS0348**, which says the part that surprises. Several of a thing per identity is a list field, or child
identities with `$Parent` (13c). Both runtimes agree because the compiler writes every defaulted field
into the attach itself; `samples/attach_replace.vein` is in check-backend as the guard.

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

**13c. `$Parent` makes a `$Position` LOCAL, and the runtime publishes `$World`.** An identity carrying
`*Vein.Core.Relations.$Parent` has its position read as an offset from that parent; the runtime composes
the chain — parents first, each chain once — and writes `*Vein.Transform.Spatial.$World`. A crate on a
platform is written once as an offset and never touched again.

| | written by | means |
|---|---|---|
| `$Position` | the PROGRAM | local when parented, absolute otherwise |
| `$World` | the RUNTIME, every phase | where the identity actually is |

**Two shapes, not one composed in place, and that is the load-bearing part.** A collision pass is
`target $Position $Collider as a { target $Position $Collider as b { b.Position.x - a.Position.x } }`.
If a parented identity's `$Position` were silently composed, that subtraction compares a local against an
absolute — and it does not fault, does not warn, and does not look wrong, because the renderer composes
correctly and the crate is *drawn* where it belongs while failing to collide with what it is visibly
touching. Anything reading world coordinates asks for `$World` by name.

**`$World` is on every positioned identity**, parented or not — an unparented one's equals its
`$Position` — so a kit reads it unconditionally. Composition runs after the tick commits and **before
`settled`**, which is where collision reads: composing later is a one-frame lag, invisible at 60fps and
wrong at every speed. A cycle leaves every identity in it at its own position and reports once; a parent
that was destroyed leaves the child where it is.

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
ordered by &Row.rank {
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

**Write the key `&Builder.param`.** `ordered by rank` reads as if `rank` were a variable in scope; it is
Row's parameter, the value each `bring Row(…)` passes. When the block brings only one builder the bare
form is **VS0343** (a warning), and the Workbench's Quick Fix writes `&Row.rank`. Bare stays legal, and
silent, for the one thing only it can do: a block that brings more than one builder, each with a `rank`.

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
lists what is exported. The one exception is a SHARD in an `audience` (17e): `*you.Billing.!Invoicer`.

**17e. In an `audience`, `!Ledger` is a SHARD and `not` EXCLUDES — `!` never means "not".**
`audience not !Ledger not !Billing` hears everyone except those two shards; `audience !Ledger` hears only
Ledger. `not` works in front of `$`/`#` too: `audience $Session not #Banned`. Required `$`/`#` must ALL be
carried, but listed `!` shards are ANY OF (a sender is one shard). A bare `!Name` is this bundle's shard,
checked like `mute` (**VS0352**); another bundle's is `*Author.Bundle.!Name`, checked when the app links
(**VS0353**). The C# backend does not enforce `audience` and notes it. SYNTAX-DECISIONS D19.

**17f. `x carries #M` is how a body asks what a sender or an entity carries.** `if d.from carries #Trusted`
branches where an `audience` could only refuse; `if u carries #Boss` tests an entity outside a `target #Boss`
query. Write `not (x carries #M)` — `not x carries #M` negates `x` first (**VS0008**). A sender's answer is
what it carried when it EMITTED. `carries` is a word only before a `#`/`$`, so it is still a legal name. The
C# backend answers it for an entity, not for a `from` (it notes that). SYNTAX-DECISIONS D20.

**17c. An IMPORTED `$Shape` becomes a real component here the moment something attaches it.** A
qualified include (`builder Conn { *Vein.Rest.Db.$Connection  mark #Conn }`, or a stdlib builder you
`bring`) used to expand FIELDS only, and a field access resolves to a component solely when the module
declares one of that name. So the attach landed, `target $Connection #Conn` matched the entity, and
`c.Connection.base` read as **the empty string** — no diagnostic, anywhere. `Lower.RegisterImportedShape`
now adds the type to the module, and rule 17b still holds at the use site: `target` takes the **bare**
`$Connection`. A mark riding in on an imported builder likewise no longer needs a local `mark` line —
it is declared in the bundle that owns the builder, and reporting **VS0218** for it pointed the author
at a span in stdlib source.

**17d. The `shared("…")` string is a LABEL, not a docblock — over 160 characters is `VS0243`.** The
editor prints it *in the completion row*, under the name, trimmed to one line of about eighty
characters. With forty names offered from ten bundles that sentence is what the reader chooses BY, and
everything past the trim is written for nobody. The limit is measured rather than picked: across the
210 docs in `stdlib/` and `samples/` the median is 45 characters and the 95th percentile is 90, so 160
sits well above every label anybody has actually needed while still refusing a paragraph. It is a
**warning** — refusing to compile over the size of a comment would be the tail wagging the dog, and
would break a working bundle on upgrade rather than telling its author to trim it. The remedy is in the
message and is always the same: keep the sentence that says what the thing IS, and move the usage
detail into a `//` comment above the declaration, which nothing truncates.

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

**Without a manifest too.** A single bundle run on its own (`veinc run game.vein`, a game in an engine)
links the same way when it needs a bundle INSTALLED beside it — the project's `bundles/`, or a library
package (below): it runs as the principal of an implicit `app Game { load "game.vein" }`, so
`need "game.Cameras"` brings the camera library's shards, not only its shapes. (Before 2026-10-14 it only
imported declarations, and a kit's behaviour silently stayed behind.)

**Libraries** are bundles shipped as packages — an assembly carrying `.vein` files as `vein/…` resources
(VeinScriptBundle's Vein.Bundles.*). A host unpacks them outside the project and searches them after the stdlib and `bundles/`
(a `bundles/` copy of one wins, VS0310), and writes their folders to `.vein/libraries` so `veinc` and the
Workbench resolve them too. They are installed bundles: linked, and pinned in `vein.lock` — which sits
beside the program when a project has libraries but no `bundles/` of its own.

The **author is part of the name**, and that is why it is a quoted string rather than a bare word. The
retired `use N` matched on the bundle segment alone, so `use Combat` matched every author's `Combat` at
once: two were indistinguishable and collapsed into VS0216, after which the reference resolved to
nothing. It also validated nothing — `use Movemnet` was completely silent, and the failure surfaced later
and elsewhere as **VS0234** at each call that needed the widening, so a misspelled bundle read as a
broken call. A `need` that names no bundle on the search path is **VS0340** at the declaration, and one
written without an author is **VS0339**. `use` itself is **VS0338**, which names the `need` line to write.

**18a. A library PACKAGE is added, not copied: `veinc add Vein.Bundles.Web`.** Reusable bundles (the
VeinScriptBundle repo: `core.*`, `game.*`, `web.*`) ship as .NET packages. A Godot game references them in
its csproj; any other project lists them in `.veinproj` (`"Packages": { "Vein.Bundles.Web": "0.1.0-*" }`) —
`veinc add` writes that, unpacks the package from the local feed and lists it in `.vein/libraries`, where
veinc and the Workbench search; `veinc restore` does it again on a fresh clone (the Workbench does on open).
Then `need "web.Page"` resolves and LINKS (18). A built exe carries the libraries inside it. Writing one:
a shared SF runs inline in the CALLER, so name everything inside it qualified.

**18b. `need "Author.Bundle" as Y` imports QUALIFIED, and widens nothing.** The alias names the head of a
`*` path — `need "Vein.Math" as M` makes `*M.Roots.sqrt(16.0)` resolve — and deliberately does *not* make
bare `sqrt` mean anything. That is what makes it the answer to VS0216: two bundles exporting one name are
ambiguous only because both contribute bare names, so aliasing both removes the ambiguity instead of
restating it, and each vocabulary stays reachable under its own head. Only the FIRST segment substitutes;
an alias names a bundle, and a publicator that happens to share its spelling is not one. This is
`import numpy as np`, not `from numpy import *`.

**18c. `tune("doc")` above a shard `var` makes it a SETTING, and a project's values live in `vein.settings`** (D21).
The starting value must be a literal (VS0356); only a shard/ShardView/bridge `var` or `let` can be tuned
(VS0355). `vein.settings` sits beside the project's `.veinproj` (else beside the program), one
`[Author.Bundle]` section per bundle, `Shard.var = literal` per line — a NEEDED bundle's settings
included, which is the only way to change a library's. A line naming no setting is VS0357; a value of the
wrong type is VS0358, and the source's default is used. `veinc settings game.vein` lists them.

**19. A bundle can span files, and FRAGMENTS MERGE BEFORE THE MAIN FILE.** `publicators/*.vein` is API,
`shards/*.vein` is behaviour, and a fragment carrying an API declaration is **VS0321**. Member order is
the order shards run in, so a shard declared last in the main file still runs last — and a fragment
therefore cannot close a phase.

**20. An event is `Author.Bundle.Name`; a SHAPE is just its name.** One queue, one `_self` — an `emit` in
one bundle reaches a `hear` in another, and a capability bundle can bind the socket the whole app answers
on — but which event it reaches is decided by who declared it, not by the word alone.

So `acme.Radio`'s `@Dispatch` and `zenith.Courier`'s are two events that share a word, with different
payloads, both deliverable. **Events used to unify by bare name**, which meant two vendors who had never
met could not both use the obvious word: matching payloads merged silently and differing ones were VS0332,
so the app refused to link. That is a workable rule inside one team and no rule at all across a package
ecosystem. `samples/bundle_collisions` is the worked example.

**Shapes are the deliberate exception, and it is not an oversight.** Components unify by bare name because
that is precisely how a capability bundle reads the principal's `$Position` without either knowing the
other exists — take it away and the language loses its point. Two bundles disagreeing about a `$Shape` is
still **VS0332**, still an error, still "rename one or make them match".

**Two things keep their bare names**, because there is genuinely one of each: anything the standard library
declares, and anything the runtime raises itself (`@Request`, `@Ticked`, `@DiagnosticRaised`, `@Print` —
`Interp.RuntimeEvents` and `Interp.HostEvents` are the lists). There is one console to write to.

**Naming stays a separate question from identity.** With two bundles in scope a bare `@Dispatch` still
cannot say which it means — that is **VS0216**, a warning about that one reference, answered by qualifying
it or by aliasing one bundle (rule 18b).

**20a. `vein.lock` remembers what you built against — because a builder, fn or SF binds BY POSITION.**
An installed bundle is re-read from source on every compile, so nothing stale ever runs. That is also
the trap. A kit changes `fn clamp(v: float, lo: float, hi: float)` into `clamp(lo: float, hi: float,
v: float)`, and every `clamp(speed, 0.0, 1.0)` in the game still compiles, still passes the count check
(VS0229), still passes the literal check (VS0230) — and now clamps `0.0` between `1.0` and `speed`. Same
for a builder whose includes swap, or a `$Shape` it includes whose fields do: the bring binds the new
order without a word.

`vein.lock` sits beside `bundles/` and holds one line per member the project uses from there, in the
order its arguments bind:

```
builder kit.Coins.Pickups.Coin (x: float, y: float, value: int)
fn kit.Geo.Calc.clamp (v: float, lo: float, hi: float) return float
shape kit.Movement.Drive.Mover (dx: float, dy: float)
event kit.Coins.Pickups.Collected { by: Entity, value: int }
```

| what changed since the line | | |
|---|---|---|
| a parameter **moved** — swapped, or shifted by one inserted or removed before it | **VS0344** | error, at every call |
| renamed, retyped, a different count, a different return | **VS0345** | warning, at every call |
| trailing parameters that all have defaults; an event gaining a field; event fields reordered | — | no call can feel it (rule 26) |

- **A builder is recorded flattened** — includes expanded, a variant's fixed parameters removed (7c) —
  because that list is what the arguments bind to. It is taken from the very list binding walks, so the
  two cannot disagree. A LOCAL builder that includes an installed `$Shape` records the shape, and is
  checked by rebuilding its slot list with the shape as locked: kitdemo's `{ $Position $Mover $Score }`
  catches `$Mover` gaining a field, which shifts `$Score`'s argument though `$Mover` itself only grew.
- **An event is matched by field NAME**, so its order is never reported. A field it lost is VS0345 at
  the `hear` READ — `m.dy` now reads empty, and that line is the fix. The emit naming it is already VS0227.
- **Written by the commands that RUN** (`run`, `render`, `serve`, `build`; ▶ is `veinc run`), after a clean
  compile, adding new lines only. **Compiling never writes it**, and a changed line is never rewritten
  for you: `veinc lock <app-or-file>` prints was/now and accepts. VS0345 also has a Workbench Quick Fix;
  VS0344 deliberately has none — one accepted line would silence every call and fix none of them.
- **The standard library is not recorded**: it ships with the compiler. **Commit the file.** In a merge
  conflict keep either side and run `veinc lock`. `samples/kitdemo` carries one.

---

## 5. Running, and measuring

**20b. An SF runs INLINE; a shard hearing an event does not.** That is the difference between a helper
whose `bring`s land where you called it and one whose fragments arrive after everything already queued.
`do navBar(r.path)` in `samples/web_app` is an SF for exactly this reason — plus a shard on `hear @Request`
would emit for EVERY path, and an unrouted path emitting no `@Html` is how the view tells a 404 from a
page. A builder cannot hold statements at all, so it can never run a query.

**20c. An SF is called `do name(…)`; an fn is called bare.** `do attack()` changes the world, `roll(1, 6)`
cannot, and the call says which (SYNTAX-DECISIONS D15). Qualified, the `do` goes in front of the whole
path: `do *Vein.Console.Io.print("hi")`. Forget it and **VS0341** names the fix, which the Workbench
applies in one click; typing `do ` at the start of a line lists every SF you can call.

**20d. `mute Game` lands at the NEXT COMMIT, so Game still finishes what it is doing.** Mute and unmute
queue like `emit` (rule 21): Game still hears the event in progress, whether it is declared before the
muting shard or after it. Muted from the next event or phase on, its handlers and schedules are skipped,
events it misses are dropped (an `unmute` does not replay them), and its `var`s are kept. A mute written
in a tick skips Game's `settled` in the same frame; a mute in `run once` means Game starts muted. The name
must be a shard, ShardView or bridge of THIS bundle (**VS0350** otherwise, with a "did you mean"); typing
`mute ` lists them in the Workbench. SYNTAX-DECISIONS D18.

**20e. `mute #Gameplay` is a STANDING RULE over a mark, and reaches every bundle.** While it holds, any
shard carrying #Gameplay is silent — including one that gains the mark later through `mark !S #Gameplay`.
`unmute #Gameplay` lifts it; `unmute Physics` does not, because a shard runs only when it is neither muted
by name nor carrying a muted mark. `mark !Spammer #Banned` changes a shard's own marks (this bundle's,
VS0350 otherwise), queued like `mute`, and an event's `from` keeps the marks it was SENT with. `mute #M`
where nothing here carries #M is **VS0354**; declare `mark #M` when the carriers are another bundle's.
SYNTAX-DECISIONS D20.

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
| `fn`/`SF` call, wrong count | **VS0229** | warning — local, `*A.B.P.` and `need`-widened alike |
| SF call without `do`, or fn call with it | **VS0341** | error — names the right spelling (D15) |
| `do` on a built-in / not on a call | **VS0342** / **VS0112** | error |
| `hear` inside an SF or a block | **VS0113** | error — a hear is a shard member; call the SF from it (`samples/sf_emit_hear.vein`) |
| `ordered by key` over one builder | **VS0343** | warning — write `ordered by &Builder.key` (14e) |
| literal of the wrong kind | **VS0230** | warning — `bring Row(42, "words")` |
| a `bundles/` member's parameters moved since `vein.lock` | **VS0344** | error — fix the call, then `veinc lock` (20a) |
| a `bundles/` member otherwise changed since `vein.lock` | **VS0345** | warning (20a) |
| a `?` saved in a file | **VS0347** | error — the Quick Fix writes the fields `?` stood for |

A parameter with a **default**, `base` in its slot, and an explicit empty value (`""`, `0`) are the ways to say "fewer on purpose", and
none is reported. A saved `?` is **VS0347**: `?` is the Workbench's key for writing a signature out (TOOLING.md), not syntax. An `int` where a `float` is declared is widening, not a mismatch.

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
fn isDigit(c: string) return bool { return c >= "0" and c <= "9" }
fn isLetter(c: string) return bool { return (c >= "a" and c <= "z") or (c >= "A" and c <= "Z") }
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

**28f. `args()` is the command line; `now()` is the clock, and `VEIN_NOW` pins it.** A tool reads
what it was started with as a list of strings: `veinc run tasks.vein -- add "buy milk"` (everything after
`--`), or a built exe's own arguments (`tasks.exe add "buy milk"`). `len(args())`, `args()[0]` and
`target args() as a { … }` work on it like any list.

`now()` is local time, ISO 8601 to the second — `2026-10-10T19:52:33`. It is PULLED, which is a
deliberate exception: everything else time-shaped in this language is delivered as an event, so a program
stays deterministic. A date stamped on a record is the case that does not fit that, and `VEIN_NOW`
(`VEIN_NOW=2026-01-02T03:04:05`) replaces the answer outright, which is how a test of such a program
expects an exact output. Both are interpreter-only.

**28g. A tool ends with `do quit(code)`, and hears its input end as `@InputEnded`.** Both live in
`*Vein.Console.Io`.

- `do quit(0)` (the `@Quit { code }` event) stops the program. What is already queued still runs — a save
  emitted just before it lands — then nothing more is read: no input, no frames, no timers. A program
  that quits at boot (`tasks list`) never waits for stdin. `code` is the process's exit code, under
  `veinc run` and in a built exe alike.
- `@InputEnded { lines }` is raised once when stdin closes — `tool < file` reached the end, or Ctrl+Z /
  Ctrl+D — before the run finishes: the place to print a total or save.
- An ERROR nobody hears (a fault, `Report.error`) goes to **stderr** and makes the exit code **1**, so a
  script can tell a failed run from a working one. `@Quit`'s own code wins when there is one.
- `do printError("no task 9")` (the `@PrintError { text }` event) writes a line to **stderr**, so
  `tasks list > out.txt` keeps only the list in the file. It is not a fault and does not change the exit
  code; pair it with `quit(code)` for that. With no separate error stream (the Workbench), it lands with
  the rest. `veinc test` compares it against `name.err`.

**28h. A list grows with `+=`, an element is set with `xs[i] = v`, and `remove` / `sort` make new
lists.** D4 already said mutation is `+=`; now it applies to lists:

```
var xs = [3, 1]
xs += 2                 // [3, 1, 2]        — appends (a list on the right is joined: xs += [9, 8])
xs[0] = 7               // [7, 1, 2]        — in place; xs[len(xs)] = v appends; further is an ERROR
let s = sort(xs)        // [1, 2, 7]        — a NEW list, sorted as `by Shape.field` sorts
let r = remove(xs, 1)   // [7, 2]           — a NEW list; xs itself is unchanged
row.done = true         // a parsed JSON record's field, set — and m["key"] = v on a dictionary
```

`xs[i] = v` used to do NOTHING, silently — LANGUAGE.md's own `grid[i] = 0` with it — and `+=` on a
list did number addition and answered 0, so no program could build a list at all. Growing copies (so a
list held elsewhere is not changed by an append); setting is in place, like reading. A write past the end
is a reported error rather than a drop. All of it is **interpreter-only** for now: the C# backend
emits lists as fixed-size typed arrays, and `veinc emit` says so at each use rather than emitting
something that computes a different answer.

`slice(xs, start, count)` is a NEW list of `count` elements from `start`, or everything from `start` when
`count` is left out. The argument order and the clamping follow `substring`: a slice past the end is
shorter, never an error, so `join(slice(args(), 1), " ")` is "every argument after the verb" even when
there are none.

**28i. Columns, prompts and the environment: `padLeft` / `padRight`, `printInline`, `env`.** Each one
was found missing by apps/tasks (FRICTION #2, #8, #9).

```
padLeft(7, 3)               // "  7"     — widened with spaces in front; padRight puts them behind
padLeft(42, 6, "0")         // "000042"  — a fill character
padLeft("much too long", 4) // unchanged — padding NEVER cuts: a long row is still right, a cut title is not
do printInline("tasks> ")   // no line ending — the next print carries on from here
env("APPDATA")              // an environment variable; "" when it is not set
```

- `printInline` is the `@PrintInline { text }` event, beside `@Print`. It is flushed at once, so a prompt
  is on the screen before the program waits for the answer. `print` itself keeps ending the line.
- `env` is PULLED, like `now()` (28f): the environment belongs to the host, not to the program's events.
  An unset variable and an empty one are the same answer, `""`, which is what a program means by "not
  configured".
- `padLeft` / `padRight` compile with the C# backend (samples/text_pad.vein is diffed by
  check-backend). `slice`, `env` and `printInline` are interpreter-only, and `veinc emit` says so.

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
