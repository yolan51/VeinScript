# VeinIR — `veinc ir` ASCII tree format

`veinc ir <file.vein>` prints the program as a single ASCII tree where nesting is unambiguous from
the glyphs alone. It renders the **AST** (the rich, un-desugared VeinIR): `Publicator`, `Shape`,
`EachTick`, `Chance`, `Mark`, `Assign -=`, etc. The older lowered-HIR dump is still available via
`--ir=legacy`.

Implementation is split so glyphs live in exactly one place:
`Ir/AstTree.cs` (traversal → `IrNode`, no glyphs) · `Ir/IrTree.cs` (`IrNode` + the renderer, the
**only** file allowed to contain the connector glyphs; enforced by `tools/check-ir.sh`).

## Glyphs

ASCII by default; `--ir-unicode` swaps the box-drawing set. Each form is **5 columns wide** and the
prefix is built from the stack of "is this ancestor the last child?" flags — never from depth.

| Role | ASCII | Unicode |
|------|-------|---------|
| child, has following siblings | `|--- ` | `├─── ` |
| last child | `` `--- `` | `└─── ` |
| continuation under a non-last child | `|    ` | `│    ` |
| continuation under a last child | `␣␣␣␣␣` (5 spaces) | `␣␣␣␣␣` |

The root node is printed with **no prefix**.

## Line grammar

```
<prefix><Kind> <primary>[ = <inline-value>][  <attr>=<value>]*[  @<line>:<col>]
```

- One node per line; never wrapped, never reflowed; no blank lines; no trailing whitespace.
- Two spaces before the attribute run; **single** space between attributes.
- Sigils are verbatim: `$Shape`, `#Mark`, `@Event`, `::Path`.
- **Inline leaves.** Value-wrapper kinds (`Arg`, `Cond`, `Let`, `Body`, `Var`-with-init) fold their
  value's header onto their own line as `= <Kind> <primary>` and *promote* the value's children.
  A leaf value gives `Arg amount = Int 5`; a value with children gives `Arg body = Concat` + the
  Concat's items as children.
- String literals truncate at 60 chars with a trailing `...` inside the quotes; inner `"` escaped
  `\"`. `--ir-full-strings` disables truncation.
- `--ir-spans` appends `  @<line>:<col>` (start only — see "Known gaps").
- First line is a header: `VeinIR v1  file=<path>`. Output ends with exactly one newline and is
  deterministic + idempotent (declaration order, never sorted/deduped).

## Kind → attributes

Attributes are omitted entirely when empty/default.

| Kind | Attributes | Notes |
|------|-----------|-------|
| `Bundle` `Publicator` `Use` `Type` `Enum` | — | |
| `Shape` `Event` | `doc="…"` | when documented |
| `Field` | `folds=<reducer>` | when the shape field declares a fold |
| `SF` | `params=(n: T, …)` | |
| `Builder` | `kind=<html\|script\|style>` `params=(…)` | |
| `Shard` `ShardView` `Bridge` | `carries=[$Shape #Mark …]` | when it carries any |
| `Hear` | `audience=[$Shape #Mark …]` | when the barrier is present |
| `Bring` | `count=<n>` | for `bring N Builder(...)` |
| `Var` | — | `Var name: T`; inline `= …` when it has an initializer |

Structural kinds with children and no attrs: `Target`, `EachTick`, `Settled`, `Start`, `Emit`,
`Mark`/`Unmark`, `Assign`, `If`/`Then`/`Else`/`Cond`, `While`/`Do`, `Match`/`When`, `Chance`,
`Destroy`, `Attach`/`Unattach`.

Value-wrapper kinds (use `= <header>`): `Arg`, `Cond`, `Let`, `Body`.

Expression node kinds: `Int` `Float` `Str` `Bool` `Ref` (a bare name) `Path` (a member/scope/self
chain) `Binary <op>` `Concat` (a `+` chain) `Unary <op>` `Call` `New $Shape`/`New Type` `List`.

## Known gaps / deliberate choices

- **`+` renders as `Concat`.** The AST has no separate concat node and no type info, so every `+`
  chain is flattened to `Concat`. In the samples all `+` are string building; numeric `+` would also
  show as `Concat`.
- **Spans are start-only.** `SourceSpan` stores `line`, `col`, `length` — there is no end line/col,
  so `--ir-spans` prints `@<line>:<col>` rather than a `<line>:<col>-<line>:<col>` range.
- **Attribute spacing.** Single space between attributes (per this grammar); if you expected two,
  it's a one-character difference in the header line only.
- **Legacy view.** `--ir=legacy` prints the lowered HIR (`Type … : Component`, `loop target …`,
  `call AddTag`, `chance`→`if random`) — a different, post-desugar representation.
