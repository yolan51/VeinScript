# VeinScript — Core Language Specification (IOP)

VeinScript is an **Identity Oriented Programming** language. This is the whole core: data lives in
`shape`/`type`/`event`, behavior lives in `shard`, and shards act by **targeting** identities. There
is no general `class`. Domains (game/web/desktop) are libraries written in this same core — see
[DIALECTS.md](DIALECTS.md).

> **Status note.** Some keywords/tokens below are **not yet in the lexer** and are added by the
> parser milestone (M2): `fn`, `type`, `enum`, `if`, `while`, `repeat`, `break`, `continue`, `match`
> (and `[` `]` if [D9(a)](SYNTAX-DECISIONS.md#d9) is chosen). The IOP keywords already in the lexer
> (`shape`, `shard`, `event`, `target`, `each`, `tick`, `settled`, `mark`, `emit`, `hear`, `folds`,
> `chance`, `attach`, `destroy`, `sync`, …) are marked ✅ in [KEYWORDS.md](KEYWORDS.md). `class`,
> `for`, `in`, `loop`, `push` are **not** part of the language (see SYNTAX-DECISIONS).

---

## 1. Lexical structure

Fully implemented in [Lexer.cs](../src/Vein.Compiler/Lexing/Lexer.cs). Three rules are load-bearing:

- **Sigils fold into the token.** `$Health` → one `ShapeRef(Text="Health")`, `@Damaged` →
  `EventRef`, `#Enemy` → `MarkRef`. The sigil is never a standalone token. Sigils are **core IOP
  syntax**: they mark the three kinds of identity reference (shape / event / mark).
- **`30%` is one token** (`Percent`), scanned before an `Int` can be emitted, so it never collides
  with modulo. Its value is the fraction `0.30` (a `double`).
- **Newline is a virtual terminator.** A `Term` is emitted at `\n` only when the previous token can
  end a statement (`CanEndStatement`). Blocks use braces; there is no `;`.

Comments `// line` and `/* block */`. Strings double-quoted with `\n \t \r \\ \"`. Identifiers
`[A-Za-z_][A-Za-z0-9_]*`; keywords are identifiers looked up in a table.

---

## 2. The unit: `bundle`

The top-level module. Members are **private to the bundle** unless placed in a `publicator` block.
Cross-bundle references use scope resolution `Other::name`.

```
bundle Demo {
    use Core                      // import another bundle
    publicator DemoCore { … }     // exported declarations
    …
}
```

**Visibility is two-tier.** A bundle's declarations are **private** by default (local to the bundle). A
`publicator N { … }` is the bundle's **public grouping** — its members are visible to all of this
bundle's shards (bundle-wide). Inside a publicator, a `shared("doc")` annotation (on its own line above
a declaration) marks that one member **public across all bundles** — only `shared` members appear in
`veinc symbols` and are reachable from another bundle via a `*Author.Bundle.Publicator.@…` reference.
`shared` outside a publicator is an error. `use N as M` aliases an import (planned).

**Data vs behaviour.** A publicator holds the shared **data/API** — `shape`s, `event`s, `builder`s. A
`shard` is the bundle's **behaviour**: it runs when the bundle is loaded and is *not* part of the
cross-bundle API, so a shard is declared at the **bundle level, never inside a publicator** (that is an
error, VS0108). You consume another bundle's *data* by referencing its `shared` members; you get its
*behaviour* for free by loading the bundle.

```
bundle Web by studio {
    publicator PageApi {
        shared("the inbound request")     // ← public across all bundles
        event @Request { path: string }
        event @Internal { … }             // bundle-wide only (no `shared`)
    }
    event @Local { … }                    // private to the bundle
    shard Router { hear @Request as r { … } }   // behaviour — bundle level, comes with the bundle
}
```

### 2.1 Author & cross-bundle references (`by`, `app`, `*`)

At scale, bundle names collide between authors, so a bundle may declare an **author/pseudo** with `by`:

```
bundle Combat by yolan { … }        // rooted as *yolan.Combat.…
```

An **app** file names the bundles that compose one program (no entry point — IOP is reactive; see §4):

```
app MyGame {
    load "yolan_combat.vein"
    load "alice_combat.vein"
}
```

The `*` sigil is a **collision-safe qualified reference**: `*Author.Bundle.Publicator.@Event` (also
`$Shape`, or a plain member). The leading segments are a suffix of `Author.Bundle.Publicator` — qualify
with only as many as needed to be unique; if two authors clash, add the author. `veinc symbols` lists
every qualified name and flags collisions ([TOOLING.md](TOOLING.md)). *This pass is discovery +
resolution only; linking loaded bundles into one running program is a follow-on.*

**Events have one owner.** An event is declared in one bundle; its payload types live in that single
declaration. `emit` / `hear` / `start` take a **bare `@Event`** (this bundle's own/local event) or a
**qualified `*Author.Bundle.Publicator.@Event`** when the event is owned by another bundle — so the
origin and payload types are unambiguous. A cross-bundle event must be `shared` (in the owner's
publicator); `veinc symbols` validates every qualified reference resolves to exactly one shared owner
(unknown or ambiguous → error).

**Booting — `start`.** A bundle has **at most one entry point**: `start @Event { payload }` names the
boot event the runtime fires first (instead of the default `@Request { path }`). A bundle with **no**
`start` is purely reactive — it only `hear`s events others emit. An app has no boot event of its own — it
boots each loaded bundle that has a `start`, optionally overriding the payload at the load site
(`load "f" start { … }`). The body is emit-style, so `?` fill-the-rest works, and `veinc render … --set
field=value` overrides payload fields at boot (see [RUNTIME.md](RUNTIME.md)):

```
bundle Game {
    start @NewGame { seed: 42, players: 2 }
    event @NewGame { seed: int, players: int }
    …
}
```

---

## 3. Data

### 3.1 Primitives

| Type | Literals | C# |
|------|----------|----|
| `int` | `0 42 -7` | `long` |
| `float` | `1.0 3.14` | `double` |
| `bool` | `true false` | `bool` |
| `string` | `"hi"` | `string` |
| `percent` | `30%` (=`0.30`) | `double` |
| `void` | — | `void` |

### 3.2 `shape` — the data an identity carries (a component)

```
shape $Health {
    hp: int folds sum        // see §3.5 folds
    mp: int
}
```

Fields are `name: Type`. A `shape` is attachable to an identity, foldable (§3.5), and can contain a
nested `enum` (§3.4). Referenced with the `$` sigil: `$Health`.

### 3.3 `type` — plain value data (not an identity component)

For helper structs that aren't attached to identities (math values, event payloads):

```
type Vec2 { x: float, y: float }
```

Value semantics (copied). Most program data is a `shape`; `type` is for the leftovers.

### 3.4 `enum` — an identity's states, declared inside a `shape`

Enums are not top-level; they describe the discrete states a shape's field can hold, so they live in
the shape that owns them:

```
shape $Movement {
    enum Facing { North, South, East, West }
    facing: Facing folds replace
    speed: float
}
```

Referenced as `Movement.Facing` / `Facing.North` inside that shape's scope. In the HIR the enum is a
named type scoped to its shape ([IR-SPEC.md](IR-SPEC.md)).

### 3.5 `folds` — how concurrent writes to a field combine

Many shards can write the **same** field in one tick. Rather than a system-order race, the field
declares a **fold reducer** that says how the tick's contributions merge:

```
shape $Health {
    hp: int folds sum        // deltas from all shards are summed
    shield: int folds max    // largest contribution wins
    mp: int                  // no folds → replace (last-writer-wins, order-defined)
}
```

Each shard's write (`::Health.hp -= 1`) is a **contribution**; at tick resolution the runtime folds
all contributions deterministically, independent of shard order. Reducers: `sum min max replace
first all any` (see [KEYWORDS.md §3.5](KEYWORDS.md#35-fold-reducers)). `folds` is a field modifier,
not a statement — it desugars to `@fold(field, reducer)` metadata on the shape.

### 3.6 `#mark` — a tag an identity wears

A `#Mark` is boolean identity state with no fields. Referenced with `#`; added/removed by shards via
`mark` / `unmark` (§4). A shape with no fields used purely as a tag is the same idea.

### 3.7 `event` — a message identities send

```
event @Damaged {
    amount: int              // required (no `=`)
    victim: Entity
    $Position                // include $Position — pulls in ALL of its fields
    $Health.hp = 0           // include one field, defaulted (optional at emit)
    var note: string = "hit" // a `var` member — same rules as a field
}
```

Sent with `emit`, reacted to with `hear` (§4). Referenced with the `@` sigil.

**Signature body (shared with `builder`, §3.9).** The `{ … }` after `event`/`builder` is a list of
members separated by whitespace, newline, or optional comma. Each member is one of:

- `[var] name [: type] [= default]` — a field (or `var`). No `type` ⇒ inferred.
- `$Shape` — include every field of that shape. `$Shape.field` includes a single field.

`=` marks a member **defaulted** (optional, overridable); no `=` marks it **required**. `veinc events`
and the `?` fill-the-rest sigil report and satisfy exactly the members listed here (with `$Shape`
includes expanded to their fields).

**Provenance — always present.** Beyond the fields you declare, the runtime auto-attaches a **provenance
envelope** to **every event, in every bundle and every app** — you never declare it, and it is always
readable on a `hear` binding (`d.from.kind`, `d.cause`, …):

| field | meaning |
|-------|---------|
| `id` | this event's own id |
| `from` | the emitter (a First-Class object): `from.name` · `from.kind` · `from.identity` · `from.shapes` · `from.marks` |
| `origin` | the ECS **entity** that emitted, when in an entity context (`null` until the entity runtime lands) |
| `source` | the originating entity/context |
| `bundle` | the emitting bundle |
| `cause` | the id of the event that caused this one |
| `trail` | `id[]` — the full causation chain that led here |

`from` is the answer to "who emitted this?" and is always populated; `origin` is the entity id (live
once the ECS runtime executes `target`/tick). `veinc events` prints this envelope so it's visible. The
`audience` barrier (§4) filters on the emitter's `from.shapes` / `from.marks`.

### 3.8 References & collections

- Non-nullable by default; nullable is `T?`.
- Built-in generics `list<T>`, `map<K,V>`, `set<T>`. User-defined generics are deferred
  ([D10](SYNTAX-DECISIONS.md#d10)).

### 3.9 `builder` — a reusable element template

A `builder` uses the same signature body as `event` (§3.7): parameters (fields/`var`s/`$Shape`
includes). A builder either has one **output-channel field** whose value is the template (the field's
name decides the kind), **or no channel at all** — in which case it constructs and emits an event named
after the builder, carrying all its params:

| output field | kind   | emitted event |
|--------------|--------|---------------|
| `markup`     | html    | `@Html`             |
| `code`       | script  | `@Script`           |
| `css`        | style   | `@Style`            |
| `line`       | console | `@Print`            |
| *(none)*     | event   | `@<BuilderName>` with all params |

```
builder Button {
    label: string                                    // required param
    onclick = "noop"                                 // defaulted param
    markup = "<button onclick=\"" + onclick + "\">" + label + "</button>"   // output ⇒ html
}

builder Console { name: string, firsttext: string }  // no channel ⇒ bring Console("Server","hi")
                                                     // emits @Console { name, firsttext }
```

There is no `( )` parameter list and no trailing kind keyword. Instantiate with `bring` (§4), which
binds arguments positionally to the parameters (every member except the output field, with `$Shape`
includes expanded), fills defaults, and emits the output event. `bring N Name(…)` repeats N times;
`?` fills the rest.

---

## 4. Behavior: `shard`

All behavior lives in shards. A shard is a set of **scheduled behaviour blocks**: the **schedule** (when
it runs) is the outer structure, and an entity **`target` query** nests inside it.

```
shard Drain {
    each tick {                              // the schedule (when) is outer …
        target $Health #Enemy as self {      // … the entity query (what) is inner
            ::Health.hp -= 1                 // a fold contribution (§3.5)
            chance 30% { emit @Damaged { amount: 5, victim: self } }
        }
    }
    settled {                                // after this tick's folds reconcile
        target $Health #Enemy as self { if ::Health.hp <= 0 { mark self #Dead } }
    }
}
```

**Schedules** (a shard's top-level blocks):

| Schedule | Runs |
|----------|------|
| `run once { … }` | once, when the shard/bundle starts (`start` is reserved for the bundle boot event, §2.1) |
| `each tick { … }` | every simulation tick |
| `each frame { … }` | once per render frame |
| `every N { … }` | on a timer — `N` is **seconds** (a plain number: `every 1.0`, `every 0.5`) |
| `settled { … }` | once, **after** a tick's `folds` contributions reconcile — the correct place to read final values (e.g. death checks) |

`run`, `once`, `frame`, `every` are **contextual words**, not reserved keywords. Inside a schedule:

| Construct | Meaning |
|-----------|---------|
| `target C… #T… as self { … }` | bind `self` to each identity matching the shapes/marks named — the entity query (§5.3) |
| `Entity` | keyword; as a type an entity id (`int`), as an expression the **nearest** entity's id — the enclosing `target` binding (same value as a bare `self`), or `0` when no entity is in scope. Distinct from the First-Class identity layer (Shard/ShardView/…). *Runtime note:* the interpreter does not yet execute schedules/`target` loops, so today `Entity` reads `0` outside a materialized entity; the id becomes live with the ECS runtime. |
| `::Shape.field` | the current identity's component field (`self`-scope) |
| `mark self #T` / `unmark self #T` | add / remove a tag |
| `attach $C to self { … }` / `unattach $C from self` | add / remove a component |
| `emit @E { … }` | send a message |
| `hear @E as evt { … }` | react to a message; `evt` bound to it |
| `destroy self` | remove the identity |
| `chance 30% { … }` | run the block with 30% probability |
| `sync` | scheduling hint (ordering/parallelism) on the shard |

A shard may also hold private state fields (`var`/`let`) and call functions.

---

## 5. Functions & control flow

### 5.1 Functions — `fn` and `SF`

```
fn move(target: Vec2, speed: float) -> Vec2 { … }   // general; may have effects
SF clamp(v: int, lo: int, hi: int) -> int {         // pure: verified side-effect-free
    if v < lo { return lo }
    if v > hi { return hi }
    return v
}
```

Params are `name: Type`; return type follows `->` (omit for `void`). `SF` is an `fn` the semantics
pass verifies pure (no state mutation, emit, or I/O), enabling reuse and folding.

### 5.2 Conditionals — `if` / `else`

```
if hp <= 0 { mark self #Dead } else if hp < 10 { warn() } else { }
```

### 5.3 Iteration — `target`, `repeat`, `while`

`target` is the one way to **cycle through data** — the same keyword shards use, generalized:

```
target enemies as e { e.hp -= 1 }         // over any collection/query, bind e
target $Health #Enemy as self { … }       // typed identity query (the shard form)
```

`repeat` is the **counted** loop, with an optional counter binding:

```
repeat 5 { spawn() }            // run 5 times
repeat n as i { grid[i] = 0 }   // i = 0 … n-1
```

`while` is the **conditional** loop:

```
while alive { step() }
```

`break` / `continue` control the nearest enclosing loop. There is no `for`/`in`/`loop` keyword.

### 5.4 Pattern match — `match` / `when`

Branch on an enum (or value); `when` is the arm keyword:

```
match facing {
    when North { dy -= 1 }
    when South { dy += 1 }
    else       { }
}
```

### 5.5 Bindings & assignment

`let` (immutable), `var` (mutable). Assignment `=`, compound `+= -= *= /=`. Compound assignment on a
shape field is the normal way to contribute to a fold (§3.5).

### 5.6 Expression statement

A bare call/expression on its own line (`warn()`, `emit @E { … }`).

---

## 6. Expressions

Precedence, lowest → highest (one parser method per level):

| Level | Operators | Assoc |
|-------|-----------|-------|
| Or | `or` | left |
| And | `and` | left |
| Cmp | `== < > <= >=` | left |
| Add | `+ -` | left |
| Mul | `* / %` | left |
| Unary | `not  -` | prefix |
| Primary | literals, names, `( )`, call `f(…)`, member `a.b`, scope `M::x`, index `a[i]`, sigil refs, struct/shape/event literal | — |

There is no `!=` operator — inequality is written `not (a == b)` (`!` is reserved/free for a future sigil).

`and`/`or` short-circuit. Struct/shape literals: `Vec2 { x: 1.0, y: 2.0 }`; event literals appear in
`emit @E { … }`. Indexing `a[i]` needs `[` `]` tokens ([D9](SYNTAX-DECISIONS.md#d9)).

---

## 7. Grammar sketch (EBNF)

Terminals are `TokenKind`s from [TokenKind.cs](../src/Vein.Compiler/Lexing/TokenKind.cs). `TERM` is
the virtual newline terminator.

```ebnf
program     = { bundle } EOF ;
bundle      = "bundle" IDENT "{" { TERM } { decl { TERM } } "}" ;

decl        = useDecl | publicator | shapeDecl | typeDecl | eventDecl
            | shardDecl | funcDecl | varDecl ;

useDecl     = "use" IDENT [ "as" IDENT ] ;
publicator  = "publicator" IDENT "{" { TERM } { [attr] decl { TERM } } "}" ;
attr        = "shared" "(" STRING ")" TERM ;

shapeDecl   = "shape" SHAPEREF "{" { TERM } { shapeMember { TERM } } "}" ;
shapeMember = enumDecl | field ;
field       = IDENT ":" type [ "folds" IDENT ] ;          (* IDENT = fold reducer *)
enumDecl    = "enum" IDENT "{" IDENT { "," IDENT } "}" ;
typeDecl    = "type" IDENT "{" fieldList "}" ;
eventDecl   = "event" EVENTREF "{" fieldList "}" ;
fieldList   = field { ("," | TERM) field } ;

shardDecl   = "shard" IDENT "{" { TERM } { shardMember { TERM } } "}" ;
shardMember = varDecl | funcDecl | schedule | hearBlock ;
schedule    = ("run" "once" | "each" ("tick" | "frame") | "every" NUMBER | "settled") block ;
queryStmt   = "target" { SHAPEREF | MARKREF } "as" IDENT block ;   (* a statement, inside a schedule *)
hearBlock   = "hear" EVENTREF "as" IDENT block ;

funcDecl    = ("fn" | "SF") IDENT "(" [ params ] ")" [ "->" type ] block ;
params      = param { "," param } ;  param = IDENT ":" type ;
varDecl     = ("let" | "var") IDENT [ ":" type ] [ "=" expr ] ;
type        = IDENT [ "<" type { "," type } ">" ] [ "?" ] ;

block       = "{" { TERM } { stmt { TERM } } "}" ;
stmt        = varDecl | ifStmt | whileStmt | targetStmt | repeatStmt | matchStmt
            | "return" [ expr ] | "break" | "continue"
            | iopStmt | assignStmt | exprStmt ;

ifStmt      = "if" expr block [ "else" (ifStmt | block) ] ;
whileStmt   = "while" expr block ;
targetStmt  = "target" expr "as" IDENT block ;
repeatStmt  = "repeat" expr [ "as" IDENT ] block ;
matchStmt   = "match" expr "{" { "when" pattern block } [ "else" block ] "}" ;

iopStmt     = "mark" expr MARKREF | "unmark" expr MARKREF
            | "emit" EVENTREF structBody | "destroy" expr
            | "attach" SHAPEREF "to" expr [ structBody ] | "unattach" SHAPEREF "from" expr
            | "chance" PERCENT block ;

assignStmt  = lvalue ("=" | "+=" | "-=" | "*=" | "/=") expr ;
exprStmt    = expr ;

expr        = orExpr ;
orExpr      = andExpr  { "or"  andExpr } ;
andExpr     = cmpExpr  { "and" cmpExpr } ;
cmpExpr     = addExpr  { ("==" | "<" | ">" | "<=" | ">=") addExpr } ;   (* no "!="; use `not (a == b)` *)
addExpr     = mulExpr  { ("+" | "-") mulExpr } ;
mulExpr     = unary    { ("*" | "/" | "%") unary } ;
unary       = ("not" | "-") unary | postfix ;
postfix     = primary { "(" [ args ] ")" | "." IDENT | "::" IDENT | "[" expr "]" } ;
primary     = INT | FLOAT | PERCENT | STRING | "true" | "false"
            | IDENT | SHAPEREF | EVENTREF | MARKREF | scopeRef
            | "(" expr ")" | structLit ;
scopeRef    = "::" IDENT ;                       (* ::Health = self's component *)
structLit   = IDENT structBody ;
structBody  = "{" fieldInit { ("," | TERM) fieldInit } "}" ;
fieldInit   = IDENT ":" expr ;
args        = expr { "," expr } ;
lvalue      = (IDENT | scopeRef) { "." IDENT | "[" expr "]" } ;
```

The IOP statements (`mark`, `emit`, `chance`, `each tick`, …) are surface sugar that the desugar pass
rewrites to plainer core (compound assignment + runtime calls + metadata) before lowering — see
[DIALECTS.md](DIALECTS.md) and [IR-SPEC.md §3](IR-SPEC.md).
