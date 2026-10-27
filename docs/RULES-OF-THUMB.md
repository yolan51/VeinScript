# VeinScript rules of thumb

**How to shape a program so it stays readable.** This is not a list of traps (that's [RULES.md](RULES.md):
"this compiles and does the wrong thing"), and not the reusable-bundle patterns (VeinScriptBundle's
`PATTERNS.md`). These are the habits that decide whether a file reads like a description of the program, or like
a maze of `target`s.

Each rule says what to do, why, and how to spot that you need it. The examples come from
`samples/desktop/todo.vein`, before and after its rewrite: the same app at 7 nested `target`s, then at 2.

---

## 1. Data first: give the data its own shapes

If a thing in your program has facts (a task is done, has a priority, belongs to a group), give it a shape that
holds them: `$Task { group: string, text: string, done: bool, priority: int }`. Don't leave those facts scattered
across widgets (a checkbox's `checked`, a dropdown's `picked`) and climb to collect them.

**Spot it:** you read a fact by walking from one identity to another to another.

```
// before: "is this task done?" is a checkbox, under a row, under a list, under a group
target $Parent #Tasks as list { if list.Parent.of == g { target $Parent as row { … target $Toggle on cell as t { … } } } }

// after: the task says it
target $Task as t { if t.Task.done { … } }
```

## 2. More than two `target`s deep means a link is missing

Nesting is how VeinScript asks "which one?". Three or more levels deep, the program is searching for something
it should simply be pointing at. Add the link as a field, `do ownedBy(tab, g)` on every widget that
belongs to a group (rule 3), or make the data identity *be* the widget (a task identity is also its row), so its
parts hang straight under it.

**Spot it:** `if a.Parent.of == b` inside `if b.Parent.of == c`.

## 3. Two links, from the stdlib: `$Parent` and `$Owner`

Most links in a program say one of two things, and `Vein.Core` already has a shape for each:
- **`$Parent { of }`: where it sits.** The tree: a checkbox in a row, a row in a window. Vein.Desktop draws by it.
- **`$Owner { ownerId }`: what it belongs to.** A group's tab sits in the tab row, but it is the group's.
  Set it with `do ownedBy(tab, g)`; read it back as `t.Owner.ownerId`.

Use these before inventing a `$For` or `$BelongsTo`. A widget can carry both, and they say two different, true
things. `do discard(g)` takes what sits under `g` *and* what `g` owns, so removing a group is one line.

Name your own link only when an identity needs a **second** relation (an item owned by a player and also in a
bag: `$InBag { owner }`; one identity carries only one `$Owner`), or when the link carries data of its own.

**Spot it:** a shape whose only field is an `Entity`.

## 4. Look at one identity with `on`, never by scanning

When you already have the identity (an event's `target`, a field holding an `Entity`), look at it with `on`:
`target $Task on box.Parent.of as t { … }` (D22). Scanning every `$Task` for the one whose id matches reads all of
them to find one.

## 5. Name roles with marks, not with text

A button is "the Add button" because it wears `#Add`, not because its label says "Add". Labels get
translated, ids change between runs, and marks are checked by the compiler. Compare *data* by value; identify
*roles* by mark.

## 6. Say what you leave out in the query

`target $Group #Shown not #Hidden as g` (D25), rather than `target $Group #Shown as g { if not (g carries #Hidden) { … } }`.
The body is then only about what it does, and `Index` counts only what was kept.

## 7. Handlers change data; `settled` derives what is shown

A handler that adds a task should add the task, and stop there. Counts, progress bars, badges, colours and
"hide done" are recomputed in a `settled` block from the data, every frame. Then no handler can forget to
repaint, and a new way of changing the data needs no new repaint code.

**Spot it:** the same "update the label" lines in several handlers.

## 8. One place builds the screen

Loading, "Add" and "Add group" should all just `bring` data. One `View` shard gives every piece of data that
has no widgets yet its widgets, and marks it: `target $Task not #Shown as t { …widgets…; mark t #Shown }`. The
construction code then exists once, and loading can never build a screen that differs from adding.

## 9. Write each action once

When several inputs mean the same thing (Enter in a box, a click on Add), each emits one event (`@AddTask`) and
one handler does the work.

## 10. Save with `toJson`, load with a loom

Each record you save is one shape's `toJson(t.Task)`. Collect them with a list (`var tasks = []`,
`tasks += …`, `join(tasks, ",")`). Read them back with the built-in JSON loom, one identity per record, without
writing any parsing:
```
loom<Json> SavedTask { $Task   mark #Task }
hear *Vein.Files.Io.@FileLoaded as f { weave SavedTask(f.text) }
```
`weave`, `loom` and `Json` are part of the language; `need "Vein.Weaving"` only adds more formats. Prefer flat
files of one record kind each (`groups.json`, `tasks.json`) to one nested document. A loom reads arrays of
records, and flat files stay flat in the code.

## 11. Save after a change, not every frame

Emit `@Changed` from each user action, and write in `settled` only when it arrived. A program that writes every
frame can save an empty file before its load has finished.

## 12. A question is an `fn`; a change is an `SF` or a handler

`fn left(group: string) return int { … target $Task … }` answers a question, and may only read. `SF discard(e)`
and handlers change the world. Small `fn`s with names (`total`, `left`, `listFor`) turn a deep block into one
line that says what it asks.

## 13. Reach for the language before writing it

Before writing a helper, check whether VeinScript already does it:
- **JSON:** `toJson`, `fromJson`, `weave`.
- **Removing a widget, everything inside it and everything it owns:** `do discard(e)`.
- **Links:** `$Parent` (where it sits), `$Owner` (what it belongs to), both in `Vein.Core`.
- **Several marks at once:** `mark t #Shown #Row`.
- **Windows and widgets:** `Vein.Desktop`.
- **Escaping text:** `toJson` does it.

`veinc symbols` and the Workbench's Outline list what exists.

## 14. The top of the file says the ideas

Begin a program with a short comment naming the three to five ideas that carry it ("data first, one place
builds the screen…"). A reader who has those can read the rest; one who hasn't is reverse-engineering them from
`target`s.

---

**When a rule here is broken on purpose** (a quick test, a demo of the alternative), say so in a comment. The
next reader will otherwise "fix" it.
