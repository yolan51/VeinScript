# VeinScript — HIR (High-level Typed IR) Specification

The HIR is **the one representation** every backend consumes: a typed, tree-structured IR that stays
close to IOP source but is stripped of everything a backend shouldn't have to know about.

## Invariants (what a backend can rely on)

1. **Fully typed.** Every `IrExpr` has a resolved `IrTypeRef`. No inference remains.
2. **Names resolved.** Every reference points to a declaration (local, param, field, function, type,
   shard). No unresolved names; no `::` scope resolution left.
3. **Surface sugar lowered.** No sigils, `chance`, `mark`, `each tick`, `folds`, `target` *syntax*.
   Data is `IrType`; behavior is `IrShard`; IOP intent survives as `IrType.Kind` + `IrAttr` + fold
   info + query descriptors (§3–4).
4. **Assignment normalized.** `x += e` → `IrAssign(x, IrBinary(Add, x, e))`; `if…else if` → nested
   `IrIf`; `chance` → `IrIf` over `random()`.
5. **Purity known.** `SF` functions carry `IsPure = true`.
6. **Spans preserved.** Every node keeps a `SourceSpan`.

The HIR is **not** SSA and **not** a CFG — control flow stays structured (`IrIf`, `IrLoop`). That
keeps the C#/JS transpilers trivial. A future native/VM backend can lower HIR → a low-level IR itself
(out of scope here; see [ROADMAP.md](ROADMAP.md)).

---

## 1. Node catalog

Records live in `src/Vein.Compiler/Ir/`. Every node has `SourceSpan Span`. Illustrative shapes
(final C# decided in M4).

### 1.1 Top level

```
IrModule(string Name, IrType[] Types, IrFunction[] Functions, IrShard[] Shards, IrMeta Meta)

IrType(string Name, IrTypeKind Kind, IrField[] Fields, IrEnumCase[] Cases, IrMeta Meta)
  // Kind: Struct | Enum | Component | Message | Tag
  // Cases is non-empty only for Kind=Enum; an enum nested in a shape has Name "Shape.Enum"
IrField(string Name, IrTypeRef Type, IrFold? Fold)     // Fold set from `folds <reducer>`
IrFold(FoldReducer Reducer)                            // Sum|Min|Max|Replace|First|All|Any
IrEnumCase(string Name, int Ordinal)

IrFunction(string Name, IrParam[] Params, IrTypeRef Return, IrBlock Body, bool IsPure, IrMeta Meta)
IrParam(string Name, IrTypeRef Type)

IrShard(string Name, IrField[] State, IrQuery? Query, IrFunction[] Methods, IrMeta Meta)
  // Methods include lifecycle: start / tick / settled, plus hear-handlers
IrQuery(IrTypeRef[] Components, IrTypeRef[] Tags, string Bind)   // from `target … as self`
```

### 1.2 Types

```
IrTypeRef
  ├ IrPrimitive(PrimKind)         // Int | Float | Bool | String | Percent | Void
  ├ IrNamedType(IrType)           // resolved struct/component/message/tag/enum
  ├ IrShardRef(IrShard)           // reference to a shard (rare)
  ├ IrGeneric(name, IrTypeRef[])  // list<T>, map<K,V>
  └ IrNullable(IrTypeRef)         // T?
```

### 1.3 Statements

```
IrBlock(IrStmt[] Statements)
IrLet(string Name, IrTypeRef Type, IrExpr? Init, bool Mutable)
IrAssign(IrExpr Target, IrExpr Value)                        // includes lowered +=, and fold contributions
IrIf(IrExpr Cond, IrBlock Then, IrBlock? Else)
IrLoop(IrLoopKind Kind, IrLoopClause Clause, IrBlock Body)   // Kind: While | Target | Repeat
  IrWhileClause(IrExpr Cond)
  IrTargetClause(string Var, IrTypeRef VarType, IrExpr Source | IrQuery Query)
  IrRepeatClause(IrExpr Count, string? Var)                  // Var = counter, 0..Count-1
IrMatch(IrExpr Subject, IrMatchArm[] Arms, IrBlock? Else)
  IrMatchArm(IrPattern Pattern, IrBlock Body)
IrReturn(IrExpr? Value)
IrBreak() / IrContinue()
IrExprStmt(IrExpr Expr)
```

### 1.4 Expressions (each carries `IrTypeRef Type`)

```
IrLiteral(object? Value, IrTypeRef Type)          // int/float/bool/string/percent
IrLocalRef(IrLet|IrParam Decl)
IrSelfRef()                                        // the identity bound by target (was `self` / `::`)
IrFieldAccess(IrExpr Receiver, IrField Field)      // a.b, self.Health.hp
IrCall(IrCallable Callee, IrExpr[] Args)
IrBinary(IrBinOp Op, IrExpr L, IrExpr R)           // + - * / % == != < > <= >= and or
IrUnary(IrUnOp Op, IrExpr Operand)                 // neg, not
IrStructInit(IrTypeRef Type, (IrField, IrExpr)[] Fields)   // Vec2 { x:1, y:2 }, Damaged { … }
IrIndex(IrExpr Receiver, IrExpr Index)             // a[i]  (if D9(a))
IrEnumRef(IrType Enum, IrEnumCase Case)            // Movement.Facing.North
```

`IrCallable` = `IrFunctionRef` | `IrMethodRef(receiver)` | `IrCtorRef(type)` | `IrRuntimeRef(name)`
(a runtime call produced by desugaring: `Emit`, `AddTag`, `DestroyEntity`, …).

### 1.5 Metadata

```
IrMeta(string? Doc, IrAttr[] Attrs)     // Doc from `shared("…")`
IrAttr(string Name, object?[] Args)     // @component @message @tag @system @query(...) @sync @fold(...)
```

IOP intent that isn't a distinct node survives as attributes — see §4.

---

## 2. Lowering — core → HIR

| Core / IOP node | HIR |
|-----------------|-----|
| `bundle N` | `IrModule` |
| `use N` | resolved away |
| `type` | `IrType(Struct)` |
| `shape $H` | `IrType(Component)` `@component`; fields with `folds` carry `IrFold` + `@fold` |
| `event @E` | `IrType(Message)` `@message` |
| `#Tag` | `IrType(Tag)` |
| `enum` (in shape) | `IrType(Enum)` named `Shape.Enum` |
| `shard S` | `IrShard` (`@system`; `@query`/`@sync` if present) |
| `fn` / `SF` | `IrFunction` (`IsPure` for `SF`) |
| `let` / `var` | `IrLet` (`Mutable` for `var`) |
| `if/else if/else` | nested `IrIf` |
| `while c` | `IrLoop(While, IrWhileClause)` |
| `target coll as x` / `target $C #T as self` | `IrLoop(Target, IrTargetClause)` (Source or Query) |
| `repeat n [as i]` | `IrLoop(Repeat, IrRepeatClause)` |
| `match` | `IrMatch` |
| `return` / `break` / `continue` | `IrReturn` / `IrBreak` / `IrContinue` |
| `x = e` / `x += e` | `IrAssign` (compound normalized to `x = x + e`) |
| literal / name / `self` / `::H.f` / `a.b` | `IrLiteral` / `IrLocalRef` / `IrSelfRef` / `IrFieldAccess` |
| `f(a)` / `a.m(b)` / `M::x` | `IrCall` / `IrCall(method)` / resolved `IrFunctionRef` |
| `T { f: e }` | `IrStructInit` |
| binary / unary | `IrBinary` / `IrUnary` |

## 3. Lowering — IOP surface sugar → HIR (desugar first)

Desugaring (`Semantics/`) rewrites sugar to plain core; core lowering (§2) then applies. Net effect:

| IOP surface | Net HIR |
|-------------|---------|
| `shape $H { hp: int folds sum }` | `IrType(Component "H")` with `IrField("hp", int, Fold=Sum)`, `@fold(hp, sum)` |
| `each tick { … }` | `IrShard.Methods += IrFunction "tick"` whose body is `IrLoop(Target, query)` |
| `settled { … }` / `start { … }` | `IrFunction "settled"` / `"start"` |
| `::Health.hp -= 1` | `IrAssign(IrFieldAccess(IrSelfRef, hp), IrBinary(Add, …, -1))` (a fold contribution) |
| `mark self #Dead` | `IrCall(IrRuntimeRef "AddTag", IrSelfRef, Dead)` |
| `emit @D { … }` | `IrCall(IrRuntimeRef "Emit", IrStructInit(D, …))` |
| `hear @D as evt { … }` | `IrShard.Methods += IrFunction` registered as a handler for `D` |
| `destroy self` | `IrCall(IrRuntimeRef "DestroyEntity", IrSelfRef)` |
| `chance 30% { … }` | `IrIf(IrBinary(Lt, IrCall(random), 0.30), then)` |
| `sync` | `IrShard.Meta.Attrs += @sync` |

---

## 4. Why attributes + `IrShard`, not a node per IOP construct

- `shape`/`event`/`#tag`/`enum` are all `IrType` distinguished by `Kind` + attributes — so type
  machinery (fields, refs, generics) is written once.
- `shard` gets its own node (`IrShard`) because it genuinely differs from data: it has a query and
  lifecycle methods. There is **no `IrClass`** — VeinScript has no general class, so the IR doesn't
  invent one.
- Fold policy rides on `IrField.Fold` + `@fold`; concurrent-write semantics are a backend concern, not
  a new control-flow node.
- New domains (web/desktop) attach `@route`/`@view`/`@window` the same way — no IR core changes.

---

## 5. Textual dump — `veinc ir <file>`

Indented tree; types as `: T`; attrs as `@`; spans via `--spans`. Mirrors `veinc tokens`
([Program.cs](../src/Vein.Cli/Program.cs)).

```
module Demo
  type Health : Component { hp: int folds sum, mp: int }
  type Damaged : Message   { amount: int, victim: Entity }
  type Dead : Tag { }
  fn clamp(v: int, lo: int, hi: int) -> int  [pure]
    if (< v lo) then { return lo }
    if (> v hi) then { return hi }
    return v
  shard Drain  @system @query(components=[Health], tags=[Enemy], bind=self)
    fn tick() -> void
      loop target self: Entity in query
        assign self.Health.hp = (+ self.Health.hp -1)      // folds sum
        if (< (call random) 0.30) then
          call Emit (Damaged { amount: 5, victim: self })
    fn settled() -> void
      loop target self: Entity in query
        if (<= self.Health.hp 0) then
          call AddTag self Dead
```

The full trace for `demo.vein` is in [EXAMPLE-PIPELINE.md](EXAMPLE-PIPELINE.md).
