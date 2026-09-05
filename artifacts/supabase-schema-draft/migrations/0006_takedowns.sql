-- 0006_takedowns.sql — the only route to removal.
--
-- Nothing in the corpus is deletable by its owner. The reasoning is worth stating plainly, because
-- the schema is otherwise unusual: IF A HUNDRED PROJECTS LOAD YOUR BUNDLE, YOUR RIGHT TO WITHDRAW IT
-- STOPS WHERE THEIR WORKING BUILD BEGINS. A moderator is the only place that judgement can be made —
-- weighing "this is my code" against "this is now load-bearing for other people" — so removal is a
-- request that a person reads, not a button.

create table public.takedown_requests (
    id            uuid primary key default gen_random_uuid(),

    -- A whole project, one bad version, or one chat message. Exactly one of the three.
    --
    -- The message target is here rather than in a separate `reports` table because the queue is the
    -- same queue and the decision is the same decision — a moderator reading a list should not have
    -- to remember there is a second list.
    project_id    uuid references public.projects(id) on delete cascade,
    version_id    uuid references public.project_versions(id) on delete cascade,
    message_id    bigint,

    -- Nullable, and set null rather than cascade, so the request survives the account. A takedown
    -- record that vanishes with the requester is not an audit trail.
    requester     uuid references public.profiles(id) on delete set null,
    requester_handle citext,

    -- A request with no reason cannot be judged, so the database will not take one.
    reason        text not null,

    status        text not null default 'pending',

    decided_by    uuid references public.profiles(id) on delete set null,
    decided_at    timestamptz,
    decision_note text,

    created_at    timestamptz not null default now(),

    -- message_id has no foreign key here: `messages` is created in 0007, after this file. The
    -- reference is added there with an ALTER, so the run order in the filenames stays honest.
    constraint takedown_one_target check (num_nonnulls(project_id, version_id, message_id) = 1),
    constraint takedown_reason_len check (length(reason) between 10 and 2000),
    constraint takedown_status_known check (status in ('pending', 'approved', 'rejected')),

    -- A decided request names its moment; a pending one does not. Keeping these in step means a
    -- listing can trust either column alone.
    constraint takedown_decided_together check ((status = 'pending') = (decided_at is null)),
    constraint takedown_note_len check (decision_note is null or length(decision_note) <= 2000)
);

create index takedown_pending_idx   on public.takedown_requests (status, created_at)
    where status = 'pending';
create index takedown_requester_idx on public.takedown_requests (requester);
create index takedown_project_idx   on public.takedown_requests (project_id);

-- Only a moderator moves a request off `pending`, and the decision records who made it. A takedown
-- with no name attached is not reviewable afterwards, which defeats the point of having review.
create or replace function public.takedown_guard()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
    if new.status is distinct from old.status
       or new.decision_note is distinct from old.decision_note then

        if not public.is_moderator() then
            raise exception 'only a moderator decides a takedown request';
        end if;

        new.decided_by := auth.uid();
        new.decided_at := case when new.status = 'pending' then null else now() end;
    end if;

    if new.project_id is distinct from old.project_id
       or new.version_id is distinct from old.version_id
       or new.message_id is distinct from old.message_id
       or new.requester is distinct from old.requester then
        raise exception 'a request cannot be retargeted; file a new one';
    end if;

    return new;
end;
$$;

create trigger takedown_guard_update
    before update on public.takedown_requests
    for each row execute function public.takedown_guard();

-- ---------------------------------------------------------------------------------------------------
-- Policies
-- ---------------------------------------------------------------------------------------------------

alter table public.takedown_requests enable row level security;

-- Not public. A report names a problem with someone's code, often before it is established that
-- there is one.
create policy takedown_select_own_or_mod on public.takedown_requests
    for select using (requester = auth.uid() or public.is_moderator());

create policy takedown_insert_self on public.takedown_requests
    for insert with check (
        requester = auth.uid()
        and public.is_active()
        and requester_handle = (select p.handle from public.profiles p where p.id = auth.uid())
        and status = 'pending'
    );

create policy takedown_update_moderator on public.takedown_requests
    for update using (public.is_moderator()) with check (public.is_moderator());

-- No delete policy. The queue is the record.
