# supabase/

The database behind VeinScript accounts, the chat, and the corpus of published bundles. Ten migrations
and one proof file; run them in filename order and you have a working project from empty.

Nothing in here is Supabase-specific beyond `auth.users` and `auth.uid()` — it is ordinary PostgreSQL
with Row Level Security.

## Running it

**Supabase SQL editor** — paste each file in order, `0001` → `0010`. Then paste `0099_checks.sql` and
read the output.

**Supabase CLI** — `supabase db push` from the repo root, once `supabase/config.toml` exists for your
project. `0099_checks.sql` is *not* a migration; run it by hand.

| file | what it creates |
|---|---|
| `0001_extensions.sql` | citext, pgcrypto |
| `0002_profiles.sql` | `profiles`, `retired_handles`, the signup trigger, `is_moderator()`, `is_active()` |
| `0003_projects.sql` | `projects` — the mutable half, and the address rule |
| `0004_versions.sql` | `project_versions` — immutable snapshots, server-assigned `seq` |
| `0005_files.sql` | `project_files` — the source, and the full-text index over it |
| `0006_takedowns.sql` | `takedown_requests` — the only route to removal |
| `0007_chat.sql` | `rooms`, `messages`, the rate limits, moderator removal |
| `0008_yanks.sql` | `version_yanks` — withdrawing a release without deleting it |
| `0009_stars.sql` | `stars` and the derived count |
| `0010_publish_rpc.sql` | `publish_version(...)` — a publish as one transaction |
| `0099_checks.sql` | **not a migration** — proves the policies, rolls back |

## The first moderator

Deliberately not seeded: a handle that does not exist yet would make `0002` fail on a fresh project.
Sign up on the site, then run this once:

```sql
update public.profiles set role = 'admin' where handle = 'your-handle';
```

Until someone holds that role `is_moderator()` is false for everyone, so nothing in the corpus can be
deleted by anybody at all. That is a safe place to start rather than a problem.

## Five rules the schema enforces, so nothing else has to

**RLS is the whole security model.** The anon key ships inside the Workbench. There is no second layer
behind it, which is why every table is created, locked and given its policies *in one file* — a
separate `rls.sql` is a file someone forgets to run, and the cost of forgetting is a public database.

**Published content is immutable.** `project_versions` and `project_files` have no UPDATE policy at
all. Not a constraint, not a trigger — nothing that permits it. A mistake is fixed by publishing the
next version, and a bad release is *yanked* (0008), which leaves it resolvable for anyone already
depending on it while warning everyone else off.

**Nothing in the corpus is deletable by its owner.** If a hundred projects load your bundle, your right
to withdraw it stops where their working build begins. Removal is a `takedown_request` that a moderator
reads. Deleting an *account* is safe: `owner` is `ON DELETE SET NULL` and the handle was copied at
publish time, so the work survives, visibly unowned.

**A project's address in the corpus is its address in the language.** `bundle Combat by alice` can only
publish to `alice/combat`, so `*alice.Combat` names a row. Enforced by
`projects_slug_is_the_bundle_name` for `kind = 'bundle'`; a solution or a scratch project is not a
`load` target and takes a free slug.

**Publishing is public.** There is no `private` visibility, and `unlisted` only keeps a project off the
browse page — its source is still searchable. The publish dialog has to say this in those words, since
a person who reads *unlisted* as *private* will publish something they should not have.

## Two things the database cannot check

**That every `bundle X by Y` in a package says your handle.** That needs a VeinScript parser, so it
lives in the Workbench — where it can also offer to fix a `by you` line instead of failing. What *is*
enforced here is the half that matters: `owner_handle` is read from your profile and never taken from
the client.

**That the source compiles.** `error_count` is checked to be zero and is not a parameter of
`publish_version`, so a client cannot claim it — but the compile itself happens in `PublishGate` before
anything is sent.
