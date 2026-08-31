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

**3. A string literal is one line.** `\n \t \r \\ \"` are the escapes; a literal newline inside quotes is
VS0003. To emit a multi-line string, use `\n` and wrap the source with trailing `+` per rule 1.

**4. All 60 lexer keywords are reserved, including as parameter and field names.** `target` is a keyword,
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

**13. `folds` reconciles concurrent writes; `settled` is where the reconciled value is readable.** Two
shards doing `hp -= 1` in one tick give `hp - 2`, because each contributes a *delta* from its own
snapshot. Reading `hp` during the tick sees an unreconciled value — death checks belong in `settled`.

**14. `target` binds ONE shape per loop, and nothing can ask an identity which shapes it carries.**
Several shapes are an AND (`target $A $B`). There is no dispatch — a heterogeneous ordered sequence
cannot be rebuilt by query, which is why `/docs` in `samples/web_app` renders from a literal sequence and
only its section index is a query.

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

**25. Measuring the backend: Release on both sides, and past JIT warm-up.** Timing the Debug CLI against
a Release backend inflates the ratio ~1.4×. And the generated code keeps getting faster for several
hundred frames — a window opening at frame 100 reports 6.6× where the steady state is 10–18×. Both
mistakes were made here before the harness was trusted.
