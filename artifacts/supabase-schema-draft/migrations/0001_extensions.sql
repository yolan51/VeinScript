-- 0001_extensions.sql — what the rest of the schema is built on.
--
-- Run order is the filename. Every later file assumes this one has run.

-- citext: a case-insensitive text type. Handles and slugs are compared without case, and doing that
-- with lower() in every query is a rule you only have to forget once. The type carries it instead.
create extension if not exists citext;

-- gen_random_uuid() is core in PostgreSQL 13+, but pgcrypto is what Supabase's own templates enable
-- and costs nothing to state.
--
-- Nothing here CALLS pgcrypto. Hashing published files uses the built-in sha256() rather than
-- pgcrypto's digest(), because Supabase installs extensions into the `extensions` schema and a plain
-- PostgreSQL into `public` — so a qualified call would be correct on exactly one of the two.
create extension if not exists pgcrypto;
