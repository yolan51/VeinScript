-- 0008_yanks.sql — withdrawing a release without deleting it.
--
-- Nothing in the corpus is deletable by its owner, and that rule is right: if a hundred projects load
-- your bundle, your right to withdraw it stops where their working build begins. But a leaked key or
-- a release that corrupts saves needs an answer in minutes, and a moderator queue is not that.
--
-- A yank is the answer, and it is the no-delete rule rather than an exception to it:
--
--   * the version still RESOLVES, so anyone already depending on it keeps building;
--   * it is dropped from listings and warns loudly on the project page, so nobody new starts there.
--
-- WHY A SIDE TABLE RATHER THAN COLUMNS ON project_versions. Because `project_versions` has no UPDATE
-- policy at all, and that absence IS the immutability — there is nothing to reason about. Adding
-- yanked_at to that table would mean adding an update policy and then narrowing it with a trigger,
-- which is a weaker guarantee explained at greater length. The version's CONTENT is immutable; an
-- advisory ABOUT it is a separate, appendable fact, and belongs in a separate, appendable table.

create table public.version_yanks (
    version_id  uuid primary key references public.project_versions(id) on delete cascade,

    yanked_at   timestamptz not null default now(),
    yanked_by   uuid references public.profiles(id) on delete set null,

    -- Required, and shown to anyone who lands on the version. "This release leaks the staging key" is
    -- what makes a yank actionable; a yank with no reason is just a version that stopped working.
    reason      text not null,

    -- Lifting is a moderator's call, so a yank cannot be used to hide a version from review and then
    -- quietly restored once attention moves on.
    lifted_at   timestamptz,
    lifted_by   uuid references public.profiles(id) on delete set null,
    lift_note   text,

    constraint yank_reason_len     check (length(reason) between 5 and 1000),
    constraint yank_lift_together  check ((lifted_at is null) = (lifted_by is null)),
    constraint yank_note_len       check (lift_note is null or length(lift_note) <= 1000)
);

-- The listing predicate: a version is in force when it has no row here, or its yank was lifted.
create index yanks_active_idx on public.version_yanks (version_id) where lifted_at is null;

create or replace function public.yank_guard()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
    if tg_op = 'INSERT' then
        new.yanked_by := auth.uid();
        new.yanked_at := now();
        return new;
    end if;

    if new.version_id is distinct from old.version_id
       or new.yanked_at is distinct from old.yanked_at
       or new.yanked_by is distinct from old.yanked_by
       or new.reason is distinct from old.reason then
        raise exception 'a yank is a record: lift it, do not edit it';
    end if;

    if new.lifted_at is distinct from old.lifted_at and not public.is_moderator() then
        raise exception 'only a moderator lifts a yank';
    end if;

    new.lifted_by := case when new.lifted_at is null then null else auth.uid() end;
    return new;
end;
$$;

create trigger yank_guard_write
    before insert or update on public.version_yanks
    for each row execute function public.yank_guard();

-- ---------------------------------------------------------------------------------------------------
-- Policies
-- ---------------------------------------------------------------------------------------------------

alter table public.version_yanks enable row level security;

-- Public. A warning nobody can read is not a warning — and the person who most needs to see it is a
-- stranger deciding whether to depend on this version.
create policy yanks_select_all on public.version_yanks
    for select using (true);

-- The owner of the project, or a moderator. Notably NOT gated on is_active(): a suspended account
-- must still be able to pull a release that is actively harming people.
create policy yanks_insert_owner_or_moderator on public.version_yanks
    for insert with check (
        public.is_moderator()
        or exists (
            select 1
              from public.project_versions v
              join public.projects p on p.id = v.project_id
             where v.id = version_id and p.owner = auth.uid()
        )
    );

create policy yanks_update_moderator on public.version_yanks
    for update using (public.is_moderator()) with check (public.is_moderator());

-- No delete policy. Un-yanking is `lifted_at`, which leaves the history of both decisions.
