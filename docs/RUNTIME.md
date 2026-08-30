# VeinScript — Runtime & Boot Model

How a VeinScript program actually starts and runs, and how the planned `start` directive lets you
choose the boot event + payload instead of the hard-coded default. Status is called out honestly:
some of this **runs today**, some is **designed but deferred**.

---

## 1. Two execution models

VeinScript has two runtime shapes that share one data/identity model:

| Model | Drives | Constructs | Status |
|-------|--------|------------|--------|
| **Reactive event loop** | web / request-response / message flows | `emit` / `hear` / `ShardView` | **implemented** ([Interp.cs](../src/Vein.Compiler/Ir/Interp.cs)) |
| **ECS tick loop** | games / simulations | `target` / `each tick` / `folds` / `settled` | **implemented** ([EntityStore.cs](../src/Vein.Compiler/Ir/EntityStore.cs)); the clock is explicit — `--ticks N` |

Both are reactive at heart: behavior lives in shards that react to something (an event, or a frame) and
mutate identities / emit more events. There is **no `main()`** — a program is a set of reactions plus a
**boot event** that kicks the first reaction off.

---

## 2. The reactive event loop (what runs today)

`veinc render <file> [path]` runs this. Steps ([Interp.Render](../src/Vein.Compiler/Ir/Interp.cs)):

1. **Register** every `shard` / `ShardView` / `bridge` as a First-Class object (unique id) and wire each
   `hear @E` handler into a table keyed by event name.
2. **Boot**: fire one event. Today this is **hard-coded** to `@Request { path: <cli arg or "/"> }`.
3. **Loop**: dequeue an event → run every `hear` whose event matches (subject to the `audience` barrier)
   → each handler may `emit` more events (auto-tagged with provenance: `from`, `id`, `cause`, `trail`)
   → repeat until the queue drains (guarded against runaway loops).
4. **Output**: when a handler emits `@Response`, its `{ status, body }` becomes the result.

So "what starts the program" is a `start @E` declaration, else a baked-in `@Request`. The ECS side —
`target`, `each tick`, `settled`, `folds`, `Entity` — runs too, but only when a clock drives it (§4.1).

### 2.1 Console mode (`veinc run`)

`veinc run <file>` runs the same loop **interactively**, bridging two event names to the terminal (the
way `@Response` is bridged to the web output) — the canonical declarations live in `Vein.Console`:

- **`@Print { text }`** — when the loop dequeues one, the runtime writes `text` to **stdout**.
- **`@Input { text }`** — the runtime reads **stdin** line by line and fires one per line into the loop.

So [Interp.Run](../src/Vein.Compiler/Ir/Interp.cs) boots the program, drains, then loops: read a line →
`emit @Input { text }` → drain (writing any `@Print`s) → repeat until EOF. The runtime matches on the
simple event **name**, so a program uses the explicit stdlib form and it still runs:

```
shard Main {
    hear @Boot as b { emit *Vein.Console.Io.@Print { text: "hi" } }
    hear *Vein.Console.Io.@Input as i { emit *Vein.Console.Io.@Print { text: "you said " + i.text } }
}
```
```
$ echo hi | veinc run app.vein
hi
you said hi
```
Runs on today's net8 interpreter. (A console over the SECS/net9 runtime is a follow-on — see
BACKEND-CONTRACT.md.)

**A console run switches the console to UTF-8 first.** A Windows console starts on a legacy OEM code page
(850 here) whose encoder has no `→` and no `—`; printing one writes the correct bytes and the *console*
substitutes them — `→` becomes byte `0x1A`, which a modern terminal draws as a stray placeholder glyph.
So `Interp.Run` calls `ConsoleLauncher.UseUtf8()` **before** it captures the writer, then takes a fresh
`Console.Out`: setting `Console.OutputEncoding` replaces that writer, and a `StreamWriter`'s encoder is
fixed when it is built, so a reference grabbed beforehand would keep encoding in the old code page while
the property reported the new one. Only when the writer really is the console — a host's own writer is
left untouched.

### 2.2 Standalone app (`veinc build`)

`veinc build <file>` compiles a program to a **standalone console executable** you can double-click or
ship — it opens its own console window and runs the program, no terminal or install needed:

```
$ veinc build samples/console.vein          # → samples/console.exe (self-contained, ~65 MB)
$ echo hi | ./samples/console.exe
Type something (Ctrl+Z / Ctrl+D to end):
you said hi
```

The exe embeds the `.vein` source **plus the net8 interpreter**: on launch it lexes→parses→lowers→runs
[Interp.Run](../src/Vein.Compiler/Ir/Interp.cs) — the same console loop as §2.1, so `@Print`/`@Input`
bridge to its own window. Under the hood [BuildCommand](../src/Vein.Cli/BuildCommand.cs) generates a
throwaway net8 console project (referencing the already-built `Vein.Compiler.dll`, source as an embedded
resource) and `dotnet publish`es it self-contained/single-file for the current RID. Flags:
`-o <out.exe>`, `--rid <rid>`, `--framework-dependent` (small exe, needs .NET 8 installed).

Requires the .NET SDK (for `dotnet publish`) and targets **console** programs — a web-style program
(`@Response`) prints nothing through the exe (that's `veinc render`). Emitting real C#/SECS from the IR
(a true transpiler backend) is the larger follow-on; this ships a runnable app on today's interpreter.

### 2.3 Spawning + messaging between console apps

A running program can spawn **named console applications** and pass events between them:

- **`bring Console(name, firsttext)`** → emits `@Console { name, firsttext }`; the runtime
  ([ConsoleLauncher](../src/Vein.Compiler/Ir/ConsoleLauncher.cs)) relaunches the program in a new console
  window marked as that named console (`VEIN_CONSOLE` env), which titles its window and prints `firsttext`.
  A spawned console never spawns again (no runaway tree).
- **`emit @Send { to, text }`** → delivered to console `to` as **`@Message { from, text }`**, over a local
  **named pipe** `vein.console.<to>` ([ConsoleBus](../src/Vein.Compiler/Ir/ConsoleBus.cs)). Directed,
  real-time, same machine.

Under messaging mode ([Interp.Run](../src/Vein.Compiler/Ir/Interp.cs) with `messaging: true`, used by
`veinc run` and built exes) the event loop accepts input from **both** stdin and the bus on one thread
(a `BlockingCollection` inbox), so the interpreter stays single-threaded while messages arrive
asynchronously. A spawned console stays alive to receive; a piped `veinc run < file` still exits on EOF.

**Who may reach a handler is declared, not assumed.** The `audience` barrier (LANGUAGE.md §4) works across
the bus as well as inside a process: a console is an identity named by its address and carries that address
as a mark, so

```
hear *Vein.Console.Io.@Message as m audience #Alpha { … }   // only Alpha reaches this handler
```

No `audience` means anyone may reach it, exactly as before. A console carries no *shapes*, so a shape
requirement refuses every bus message — correct rather than broken, since nothing tells the runtime what a
console holds. Two honest limits: this filters at the **receiver**, so a refused message still crossed the
wire; and it is **advisory** — `ConsoleBus` takes the sender's name from the message body and nothing
authenticates it, so it guards against a mis-wired topology, not against a hostile process on the machine.

**A spawned console dies with the program that spawned it.** "Stays alive to receive" holds only while
there is someone to receive from, so `ConsoleLauncher.Spawn` passes its own pid down as `VEIN_CONSOLE_PARENT`
and the child waits on that process; when the parent exits, the child closes its inbox and follows. Without
this a closed window left a process with no window at all — invisible, unkillable from the UI, and still
holding `Vein.Compiler.dll` so the next `dotnet build` failed. If a run ever does leave strays behind:

```
Get-Process veinc | Stop-Process -Force
```

```
shard Chat {
    hear @Input   as i { emit @Send  { to: "Server", text: i.text } }        // I type → Server
    hear @Message as m { emit @Print { text: m.from + " says: " + m.text } } // I receive → I print
}
```
Try it: `veinc build samples/console_chat.vein` → run it → type in one window, watch it appear in Server's.
Cross-machine transport is no longer a follow-on: `Vein.Net.Peer` carries these same `@Send`/`@Message`
events over authenticated TCP, with the program unchanged (§4.3). Cross-bundle `use Console` import
remains one.

### 2.4 A console address is machine-global

The pipe a console listens on is named from the mark and nothing else —
`PipeName(console) => "vein.console." + console` ([ConsoleBus](../src/Vein.Compiler/Ir/ConsoleBus.cs)).
There is no process id, run id or session in it, so **the address space is the whole machine**, not the
run. This is deliberate: a mark is an identity, and two consoles carrying the same mark are the same
identity even in different processes.

- **Two runs of one program share their consoles.** `#Main` collides first, because `RootConsole` is the
  constant every unnamed root falls back to (only a *spawned* console is given a name, via `VEIN_CONSOLE`);
  the fixed worker marks then collide the same way. Run `samples/console_roles.vein` twice and the two Main
  windows start showing each other's replies.
- **Delivery among duplicate listeners is unspecified.** `MaxAllowedServerInstances` lets several processes
  each add an instance of one pipe name, and the OS hands a connecting sender to one of the waiting
  instances — which one is not defined.
- **The gift:** two independently launched programs address each other by identity, with no discovery, no
  ports and no handshake.
- **The bill:** runs interfere, `from` cannot tell you *whose* Alpha replied, and any process on the machine
  can open `vein.console.Main` and take delivery of messages meant for you.

If a run ever needs isolating, `PipeName` is the single lever (a per-run prefix would separate them). The
shared namespace stays the default.

---

## 3. Booting with `start` (implemented — bundle level)

**Problem:** the boot event is hard-coded to `@Request { path }`, which only fits web. A game wants to
start with `@NewGame { seed: 42 }`; a tool with `@Run { args: … }`.

**The rule: a bundle has AT MOST one entry point.** Zero or one `start @E { payload }`:
- **one** — the bundle's front door (two is an error);
- **none** — a **purely reactive** bundle that only `hear`s events others emit (most bundles: content
  providers, physics, logging…). It has no entry of its own and isn't booted directly.

An **app has no boot event of its own**: it composes bundles, and each bundle that *has* a `start` boots
at it. This keeps one meaning for `start`.

### A bundle's entry — runs a single file now
```
bundle Demo {
    start @Request { path: "/" }      // the entry point; fired first when running Demo
    …
}
```

### Overriding a loaded bundle's start (using others' bundles)

A dev composing **someone else's** bundle overrides its start payload at the **load site** — filling only
the fields they want to change; the rest keep the bundle's own values:

```
app MyGame {
    load "yolan_physics.vein" start { gravity: 9.8 }   // boot yolan's bundle with our value
    load "ui.vein"
}
```

The override body is emit-style (so `?` works), targets "the loaded bundle's start" (no need to name the
event), and is validated against that bundle's start signature — an unknown field, or a bundle with no
`start`, is an error. `veinc symbols` prints each bundle's start signature so you know what to fill.
(Surface + tooling now; the override is *applied* when app link+run lands.)

### Semantics
- The runtime fires the declared event with the declared payload **instead of** the built-in
  `@Request { path }`.
- **Running an app:** each loaded bundle boots at its own single `start` entry; a load may override that
  bundle's payload (above). There is no separate app-level boot.
- **Fallback:** a bundle with no `start` → the built-in `@Request { path }` default, so every existing
  sample keeps working unchanged.
- **Payload** is an emit-style body (`{ field: value … }`) validated against the event exactly like any
  `emit`, so the **`?` fill-the-rest sigil works here too**:
  - `start @NewGame ?` — expand/see the whole payload; defaults shown, required fields left as `?` holes.
    (Workbench: typing `?` after `start @NewGame` pops the field list, same as `emit`/`bring`.)
  - `start @NewGame { seed: 42, ? }` — supply some fields, `?` fills the rest.

- **External inputs (decided):** run inputs **override named fields** of the `start` payload. The CLI
  supplies them positionally/by name and they replace the matching payload field before boot — e.g.
  `veinc render app.vein --set path=/home` overrides `path` in `start @Request { path: "/" }`. This keeps
  today's `render <file> <path>` behaviour (path injected into `@Request`) as a special case, needs no new
  shape/binding concept, and leaves the payload literal as the default when no input is given.

### Grammar sketch
```
appMember   = "load" STRING [ "start" emitBody ] ;   // optional load-site payload override
bundleMember= … | start ;                            // exactly one per bundle
start       = "start" "@" IDENT emitBody ;
```

---

## 4. The ECS tick loop — and why `settled` exists

A shard is a set of **scheduled** behaviour blocks — `run once` · `each tick` · `each frame` ·
`every N` (seconds) · `settled` — with an entity `target` query nested inside each (schedule outer, query
inner). The intended per-frame loop over each targeted entity:

```
shard Drain {
    each tick {
        target $Health #Enemy as self { self.Health.hp -= 1 }   // MANY shards may write hp this frame
    }
    settled {                                                 // AFTER the frame's writes are reconciled
        target $Health #Enemy as self { if self.Health.hp <= 0 { mark self #Dead } }
    }
}
```

- **`each tick`** runs once per entity per frame and produces **contributions** to fields.
- **`folds`** (`sum`/`min`/`max`/…) **reconciles concurrent writes** to a field from different shards in
  the same frame — the IOP answer to "who wins when two systems both change hp?".
- **`settled`** runs **once, after** all of the frame's contributions are folded. It's the only correct
  place for post-resolution decisions (like death checks): reading `hp` *during* `each tick`, while other
  shards are still subtracting, would see a half-updated value.

### 4.1 What actually runs it

The world lives in [EntityStore.cs](../src/Vein.Compiler/Ir/EntityStore.cs) — entity ids, component
tables, mark sets, the query, and the fold reconciliation. `Interp` drives it:

| Phase | What happens |
|-------|--------------|
| `run once` | before the boot event, so the world exists by the time anything is heard |
| `each tick` / `each frame` | one activation per targeted entity; writes go to that activation's overlay |
| *commit* | the folds reduce every contribution, **then** the phase's queued mark/attach/destroy apply |
| `settled` | reads the reconciled world (this is the whole reason it exists) |
| *commit* | again, so a mark made in `settled` is visible to the **next** frame |
| *drain* | events emitted by either phase are handled against a settled world |

Two rules make a frame order-independent, and both are easy to get silently wrong:

- a `folds sum` field contributes its **delta from the snapshot** the activation took, not the value it
  wrote — `hp -= 1` from two shards is `hp − 2`, not `2·hp − 2`;
- a **structural** change (`mark` / `attach` / `destroy`) is queued and applied at the commit point, so no
  unit in the phase can observe a half-changed world.

**Entities are created by `spawn()`** — an ordinary prebuilt returning the new id (`let e = spawn()`),
so nothing in the lexer or parser had to change to make a world buildable.

**The frame clock is explicit.** `veinc run <file> --ticks N` (and `veinc render`) advance exactly N
frames, and `random` is seeded, so a run is reproducible and testable. With no `--ticks`, nothing drives
`each tick` and the program is purely reactive — which is what every event-driven sample wants.

### 4.2 `every N` — the other clock

`every N` is the one schedule defined in **real seconds**, so it is driven by a wall clock rather than
the frame count, and only in a live console session (`veinc run`, a built .exe). Each `every` block gets
its own timer thread; the thread only POSTS the firing onto the run's inbox, so the event loop stays
single-threaded — a timer arrives exactly like a console message does. A firing runs the block, commits
the phase, and drains what it emitted.

A spawned console **re-runs the same program**, so its timers fire too. `here()` is this console's own
address (`#Main` in the window the user launched), which is how a program restricts work to the root:

```
every 2 {
    if here() == #Main { emit *Vein.Console.Io.@Send { to: pick(consoles), text: "…" } }
}
```

See [samples/console_broadcast.vein](../samples/console_broadcast.vein): spawn three consoles, then
every 2 seconds write to one, to a random one, and to all of them. And
[samples/console_roles.vein](../samples/console_roles.vein), which takes the same idea further: `match
here() { when #Alpha { … } }` gives one file a different ROLE in each window it spawned — launcher,
echo, counter, relay — with no separate programs and no configuration.

See [samples/entities.vein](../samples/entities.vein): `veinc run samples/entities.vein --ticks 3`.

---

## 4.3 The network (`Vein.Net`) — the same identity model, off this machine

§2.4 admitted the ceiling of the console bus: a pipe name is machine-global, so two programs address each
other by identity with no discovery — but only if they share a machine. `Vein.Net` lifts exactly that
ceiling and nothing else. It is deliberately **two** different things, because a VeinScript peer and a
REST endpoint are not the same shape and one vocabulary for both would lie about one of them.

### 4.3.1 `Peer` — symmetric, identity-addressed

```
emit *Vein.Net.Peer.@Listen { as: #Hub, at: 9700, key: "shared-secret" }     // who I am, where I accept
emit *Vein.Net.Peer.@Link   { name: #Spoke, at: "10.0.0.5:9701", key: "…" }  // where a peer lives

emit *Vein.Net.Peer.@Send { to: #Spoke, text: "ping" }                       // …then nothing changes
hear *Vein.Net.Peer.@Message as m audience #Hub { … }
```

**The `@Send` and the `hear` are byte for byte the console versions.** `Vein.Net.Peer` re-declares
Console's `@Send`/`@Message`/`@Undelivered` because at runtime they are the *same events* — an `emit`
lowers to its bare event name and keeps the qualifier only for tooling. So there is exactly one place a
transport is ever chosen ([Interp.Drain](../src/Vein.Compiler/Ir/Interp.cs)): a mark that has been
`@Link`ed goes over the wire, anything else goes over the pipe. Delete the `@Listen`/`@Link` lines from
[net_peer.vein](../samples/net_peer.vein) / [net_spoke.vein](../samples/net_spoke.vein) and they are
local console programs again.

**The pair is the sample.** `net_peer.vein` is the hub (`veinc run` it first); `net_spoke.vein` pings it
every 2s. Run the spoke alone to watch `@Undelivered` report the silence, then start the hub and watch
the same program start getting answers with no restart. For two real machines, the spoke's `at:` address
is the only line in either program that changes.

That is the console model's claim carried intact: **a mark is an identity, and where it lives is routing.**

**Only the side that speaks first needs configuring.** A verified frame carries the port its sender
listens on, so receiving one teaches the receiver the way back ([NetBus.LearnRoute](../src/Vein.Compiler/Ir/NetBus.cs)).
A hub never has to be told where its spokes are.

### 4.3.2 The trust model — and why `audience` needed one

[KEYWORDS.md](KEYWORDS.md) has always defined `audience` as "networking/replication scope". Over the pipe
bus that was advisory: `ConsoleBus` takes the sender's name from the message body and nothing checks it.
Machine-local, that was tolerable — "who may claim to be `#Main`" was already bounded by "who is logged
into this machine". **A TCP port is not bounded that way**, so shipping `Peer` with the same honesty gap
would have turned `audience #Hub` into a decoration that reads like a barrier.

So every frame is HMAC-SHA256 signed with the pre-shared `key`, over the claimed mark, the advertised
return port, a timestamp, a nonce, and the body. The receiver refuses anything outside a ±30s window,
anything whose nonce it has already accepted (a bounded FIFO cache, so the check cannot itself be the
denial-of-service), and anything whose MAC does not verify in constant time.

| Property | Guaranteed? |
|---|---|
| **Authenticity** — `from` is a mark held by someone with the key, so `audience` really excludes | **yes** |
| **Integrity** — the body cannot be altered in flight | **yes** |
| **Freshness** — a captured frame cannot be replayed | **yes** |
| **Secrecy** — the body is hidden from anyone sniffing the wire | **no — plaintext** |

**This is authentication, not TLS.** Do not put a password in a `@Send` and assume the wire hid it.

`@Listen` **refuses an empty key** rather than accepting one. It is the single hard stop in the bundle:
a keyless listener would accept any claimed identity, and `audience` would still compile and still read
as a barrier while guaranteeing nothing — the worst of both worlds.

Still true, and worth stating: this filters at the **receiver**, so a refused frame has already crossed
the wire.

### 4.3.3 `Http` — asymmetric, URL-addressed

A REST endpoint has no mark, cannot call you back, and answers once per question, so `@Fetch` is a
request with a reply rather than an addressed send:

```
emit *Vein.Net.Http.@Fetch { url: "https://…", method: "GET", body: "" }
hear *Vein.Net.Http.@Fetched as r { … }      // it ANSWERED — any status, 404 and 500 included
hear *Vein.Net.Http.@Failed  as f { … }      // no answer at all — bad host, refused, timeout
```

The `@Fetched`/`@Failed` split is the same line `@Undelivered` draws for `Peer`: *no answer* and *an
answer you dislike* are different events. Collapsing a 404 into a failure would throw away the response
body, which for most APIs is the error explanation itself.

**Where the waiting happens depends on who owns the clock.** In a live session the request runs on a
worker and posts its result to the inbox, so a slow host cannot freeze the console — the same discipline
`every N` follows. In one-shot `render`/`serve` there is no loop to protect and the job is to produce one
response, so it waits inline.

### 4.3.4 `veinc serve` — the web backend, actually served

`stdlib/Web.vein` has always declared `@Request`/`@Response`, and `veinc render` has always fired a *fake*
`@Request` to prove a page assembles. `veinc serve` is that same pipeline with a socket in front:

```
veinc serve samples/web_demo.vein --port 8080 [--host 0.0.0.0]
```

Nothing in the language or the interpreter changed to make this work, because **a request was already the
boot event** — which is why it is one file and not a web framework. One request = one `Render` = one fresh
interpreter, so a page cannot depend on the last visitor's state. A query string arrives as boot payload
fields, exactly as `--set` does. A path that emits no `@Response` is a **404**, because that is what an
unrouted URL is.

> **Found while building this:** routing by path did not work, and the cause was not in `serve`. `==`
> read `Equals(Str(l), Str(r)) || AsDouble(l) == AsDouble(r)`, and `AsDouble` answers `0` for anything it
> cannot parse — so *every* pair of non-numeric strings took the numeric arm and compared `0` to `0`.
> `"cat" == "dog"` was **true**, and so was `req.path == "/"` for every URL a browser could ask for. The
> operands now decide the comparison: two numbers compare numerically, anything else compares as text
> (`"5" == 5` still holds). Guarded by `EqualityTests` — the bug survived so long because the test nobody
> writes is the one asserting that two *different* strings are unequal.

---

## 5. Apps, loading, linking, running

- **Load (implemented):** an `app` lists bundles across files; `veinc symbols` loads + parses them into a
  qualified symbol table for discovery and `*` resolution ([TOOLING.md](TOOLING.md)). No execution.
- **Link (implemented):** [AppLinker](../src/Vein.Compiler/Ir/AppLinker.cs) merges every loaded bundle
  into **one** `IrModule`, reporting the conflicts that merging can create.
- **Run (implemented):** `veinc run app.vein` / `veinc render app.vein` boot the linked module in a
  single interpreter.

### 5.1 The principal bundle — one boot, many capabilities

An app has a **principal** bundle: the **first `load`**, which `veinc new app` has always marked
`★ your principal bundle`. The rule is one sentence:

> **The principal's `start` is the app's single boot event. Every other bundle contributes its shards to
> the same runtime and boots nothing.**

A capability reacts; it does not start. So adding a bundle adds reactions, and removing its `load` line
removes them — with no edit to any other bundle. That is what makes capabilities *incremental*:

```
app Shop {
    load "Store.vein"      // ★ principal — the only bundle that starts anything
    load "Billing.vein"    // capability: invoices what Store ships
    load "Audit.vein"      // capability: watches both of the above
}
```
```
$ veinc run samples/app_capabilities/shop.app.vein
app Shop: principal 'Store' boots; 3 bundle(s) linked into one runtime [Store, Billing, Audit]
[Store] order: 3 x widget
[Audit] saw order of widget
[Billing] invoicing widget
[Audit] saw invoice 42 for widget
```

**One runtime is the whole point.** Before linking, `veinc run` gave each bundle its own `Interp` — its
own handler table and its own event queue — so a `hear` in one bundle could never see an `emit` from
another, and three bundles were three programs that happened to share a file. Now there is one queue, and
an event crosses a bundle boundary exactly as it crosses a shard boundary.

**Events unify by NAME**, which is what makes that work: `emit @Shipped` in the principal reaches
`hear *you.Store.Orders.@Shipped` in Billing because the runtime has always matched on the bare event
name (a qualified `*A.B.@E` keeps its qualifier for tooling and resolution, not for dispatch).

### 5.2 What linking reports

The same name-unification is the hazard hiding inside the feature, so merging is not silent:

| Code | When | Effect |
|---|---|---|
| `VS0331` | a **non-principal** bundle declares `start` | warning — it does not fire; its shards still run |
| `VS0332` | two bundles declare one event/shape name with **different fields** | warning — the first is linked |
| `VS0333` | two bundles declare one `fn`/`SF` name | warning — the first is linked |
| `VS0334` | the manifest links no bundles | error |
| `VS0335` | a load-site `start { … }` override is not a literal | warning — ignored |
| `VS0301` | a `load` target does not exist | error |

`VS0331` exists because the alternative is a mystery: a capability bundle's own `start` is how it runs
standalone, and it is inert once composed. Two bundles declaring the *same* event identically stays
quiet — that is shared vocabulary, and warning on it would train people to ignore the warning.

A shard name used in two bundles is **qualified**, not rejected (`A.Boot`, `B.Boot`) — `Boot` is an
obvious name for anyone to pick, so a collision must never drop a reaction. A unique name is left alone,
so single-bundle provenance output is unchanged.

**Load-site `start { … }` overrides** (§3) now apply, to the principal, as boot **inputs** — the same
mechanism `--set` uses, so an explicit `--set` still wins. Literals only: a composition site has no
shard, entity or event in scope for anything else to evaluate against (`VS0335`).

### 5.3 What linking still does not do

- **`use` resolution** is untouched — cross-bundle references are still written `*Author.Bundle.…`.
- Only *leaf* primitives cross at **compile** time (shapes, builders, `fn`/`SF`). Linking merges the
  **runtime** — shards, handlers, schedules — which is a different axis; a bundle still cannot call
  another's private helper.
- Boot order among bundles is not a question any more: exactly one bundle boots.

---

## 6. Status summary

| Piece | State |
|-------|-------|
| reactive emit/hear loop, `ShardView` assembly, `@Response` | **runs** |
| provenance (`from`/`id`/`cause`/`trail`), `audience` barrier | **runs** |
| boot event via `start` (bundle level) + `--set` overrides | **runs** (`veinc render samples/boot.vein`) |
| no `start` → default `@Request { path }` | **runs** (back-compat) |
| at most one entry per bundle (0 = reactive, 2 = error) | **enforced** |
| load-site `start { … }` override (parsed + validated by `veinc symbols`) | fires once app link+run lands |
| `target`/`each tick`/`folds`/`settled`, `Entity` id, `spawn()`/`attach`/`mark`/`destroy` | **runs** (`veinc run samples/entities.vein --ticks 3`) |
| `every N` (wall-clock schedule), `here()` | **runs** in a console session (`veinc run`, built .exe) |
| cross-machine `@Send`/`@Message` by identity (`Vein.Net.Peer`) | **runs** — HMAC-signed, so `audience` is **enforced**, not advisory (§4.3) |
| HTTP client (`Vein.Net.Http` `@Fetch`/`@Fetched`/`@Failed`) | **runs** (§4.3.3) |
| HTTP server — real `@Request`→`@Response` over a socket | **runs** (`veinc serve <file> --port N`) |
| transport encryption (TLS) for `Vein.Net.Peer` | **follow-on** — frames are authenticated, not secret |
| C# backend → SECS (identity half: shapes/marks/`target`/folds/phases) | **runs** (`veinc emit`, verified equal to the interpreter by `tools/check-backend.sh`) |
| app link + run — principal boots, capabilities join one runtime | **runs** (`veinc run samples/app_capabilities/shop.app.vein`) |

## 7. Open questions
- **Resolved:** external inputs reach the boot payload by **overriding named fields** of the `start`
  payload (see §3) — CLI supplies them, e.g. `--set path=/home`; today's `render <file> <path>` is the
  special case for `@Request { path }`.
- **Resolved:** a bundle has at most one entry (`start`); zero = a purely reactive bundle. An app has no
  boot event of its own — it boots each loaded bundle that *has* a `start` (with optional override).
- When an app boots several bundles, in what order do their entries fire (declaration order, or does
  link-time dependency ordering matter)?
