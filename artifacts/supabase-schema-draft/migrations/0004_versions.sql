-- 0004_versions.sql — one immutable snapshot per publish.
--
-- A version is never updated and never renumbered. That is what lets someone depend on v3 of your
-- bundle and know it will still be v3 tomorrow. A mistake is fixed by publishing v4, which is also the
-- honest history.

create table public.project_versions (
    id               uuid primary key default gen_random_uuid(),
    project_id       uuid not null references public.projects(id) on delete cascade,

    -- Assigned by the trigger below, not by the client. Two machines publishing the same project at
    -- once would otherwise both compute the same next number and one would lose.
    seq              int not null,

    -- The way into a folder that holds several files: `app.vein`, or `demo/demo.vein`.
    entry_path       text not null,

    -- ON THE VERSION, NOT THE PROJECT. v1 went out under its terms and cannot be un-published under
    -- different ones; relicensing is something you do going forward, in v2.
    --
    -- Every value here is a permissive open-source licence, and every one of them lets whoever uses
    -- the bundle SELL the game, app or site they build with it. MPL-2.0 is the strictest that still
    -- does: modifications to the bundle's own files stay open, the product around them need not.
    --
    -- GPL-3.0 and AGPL-3.0 are deliberately absent. They permit selling too, but require the whole
    -- derived work's source to be released — so a bundle under one would quietly force every game
    -- built on it to open its own source. That is not a trap to leave a stranger to find after they
    -- have shipped.
    license          text not null default 'MIT',

    notes            text,

    -- What it was built against, so a failure two years from now has a starting point.
    compiler_version text,

    -- THE COMPILE GATE, ENFORCED BY THE DATABASE. The Workbench refuses to publish a project with
    -- errors; this makes a client bug unable to put broken source in the corpus anyway.
    error_count      int not null default 0,

    -- Recorded and shown on the listing. Never a refusal — warning-free is the right bar for the
    -- standard library and a hostile one for a stranger's first upload.
    warning_count    int not null default 0,

    -- Maintained by the trigger in 0005 as files arrive, so they describe what is actually stored
    -- rather than what a client claimed.
    file_count       int not null default 0,
    total_bytes      bigint not null default 0,

    created_at       timestamptz not null default now(),

    constraint versions_seq_positive  check (seq >= 1),
    constraint versions_clean         check (error_count = 0),
    constraint versions_counts_sane   check (warning_count >= 0 and file_count >= 0 and total_bytes >= 0),
    constraint versions_notes_len     check (notes is null or length(notes) <= 2000),

    constraint versions_license_known check (license in (
        'MIT', 'Apache-2.0', 'BSD-3-Clause', 'MPL-2.0', 'Unlicense', 'CC0-1.0'
    )),

    -- Relative, forward slashes, a .vein file, and no way out of the folder.
    constraint versions_entry_shape   check (
        entry_path ~ '^[A-Za-z0-9._][A-Za-z0-9._/-]*\.vein$'
        and entry_path !~ '(^|/)\.\.(/|$)'
    ),

    unique (project_id, seq)
);

create index versions_project_idx on public.project_versions (project_id, seq desc);
create index versions_license_idx on public.project_versions (license);

-- ---------------------------------------------------------------------------------------------------
-- seq is the server's to assign.
--
-- The advisory lock is per project and released at commit, so two simultaneous publishes of the same
-- project serialise on that one project and nothing else waits. Reading max(seq) without it is a race
-- that the unique constraint would catch as an error the person then has to understand.
-- ---------------------------------------------------------------------------------------------------

create or replace function public.version_assign_seq()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
    perform pg_advisory_xact_lock(hashtextextended(new.project_id::text, 0));

    select coalesce(max(v.seq), 0) + 1
      into new.seq
      from public.project_versions v
     where v.project_id = new.project_id;

    return new;
end;
$$;

create trigger version_assign_seq_before
    before insert on public.project_versions
    for each row execute function public.version_assign_seq();

-- The denormalised columns on `projects` have exactly one writer, and this is it. SECURITY DEFINER
-- because it reaches past the guard trigger's own rule about those columns (pg_trigger_depth) and
-- past RLS, so the listing cannot drift from the versions table.
create or replace function public.version_touch_project()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
    update public.projects
       set latest_seq     = new.seq,
           latest_license = new.license,
           updated_at     = now()
     where id = new.project_id
       and new.seq > latest_seq;

    return new;
end;
$$;

create trigger version_touch_project_after
    after insert on public.project_versions
    for each row execute function public.version_touch_project();

-- ---------------------------------------------------------------------------------------------------
-- Policies
-- ---------------------------------------------------------------------------------------------------

alter table public.project_versions enable row level security;

-- Readable exactly when the parent is. Written as the join rather than `true` so that tightening
-- projects' own select policy later tightens this automatically.
create policy versions_select_readable on public.project_versions
    for select using (
        exists (select 1 from public.projects p where p.id = project_id)
    );

create policy versions_insert_owner on public.project_versions
    for insert with check (
        public.is_active()
        and exists (select 1 from public.projects p where p.id = project_id and p.owner = auth.uid())
    );

-- NO UPDATE POLICY. That absence is the immutability — not a constraint, not a trigger, just nothing
-- that permits it. Adding one later silently changes what a dependent can rely on.

create policy versions_delete_moderator on public.project_versions
    for delete using (public.is_moderator());
