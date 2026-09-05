# artifacts/

Rendered pages produced alongside the code — design reviews, schema drafts, reports. Open one in a
browser; each is a single self-contained `.html` file with no build step and no dependencies beyond a
web font.

These are **records of a decision**, not documentation of the shipped system. When a page and the code
disagree, the code is right and the page is history. Anything that must stay true belongs in `docs/`.

| file | what it is | live |
|---|---|---|
| `vein-primitives.html` | Every declaration, statement, operator and built-in, **read off `Lexer.cs` and `Parser.cs`** rather than the docs — 62 keywords, four sigils, 24 built-ins. Also carries the published-name mapping and a list of the places `docs/KEYWORDS.md` will mislead you. | https://claude.ai/code/artifact/6330eece-13ec-4299-b70f-2fdacf3323d8 |
| `cloud-schema.html` | The seven-table schema behind VeinScript accounts, chat and the published-source corpus. Written for review **before** any SQL existed. | https://claude.ai/code/artifact/67b2dde1-a204-4835-a9a6-be61f0632f03 |
| `supabase-schema-draft/` | What that review turned into: ten migrations plus an RLS proof file. **Never deployed** — the backend became a Base44 app instead. See below. | — |

## `supabase-schema-draft/`

Superseded, and kept deliberately. The platform changed after it was written; the **data model did
not**, and that model is the part that took the thinking:

- what an entity is — profile, project, immutable version, file, room, message, takedown, yank, star;
- which fields are derived and must never be writable by a client;
- the address rule that makes `alice/combat` in the corpus and `*alice.Combat` in the language the
  same name;
- the closed licence list, and why GPL/AGPL are off it;
- the rules that have to hold *somewhere* — versions immutable, `error_count = 0` unforgeable, only a
  moderator deletes, a publish is all-or-nothing.

That last group is the reason to keep it. Postgres enforced all of them in the schema. Whatever the
new backend cannot enforce server-side moves into the client and stops being a guarantee — so this
folder is the checklist to hold the replacement against, not just a souvenir.

`README.md` inside it explains the run order and the five rules. `migrations/0099_checks.sql` is the
proof file: 26 assertions, all of them things that must be refused.
