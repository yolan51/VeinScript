-- 0007_chat.sql — one room, and the messages in it.
--
-- Polled, not streamed: there is no WebSocket client anywhere in this repo, and Supabase Realtime's
-- Phoenix join/heartbeat protocol is a project rather than an afternoon. The primary key below is
-- what makes polling exact, so the polling version is correct and not merely a placeholder.

create table public.rooms (
    id         uuid primary key default gen_random_uuid(),
    slug       text not null unique,
    name       text not null,

    -- global | project. Costs nothing today and is what lets per-project rooms arrive later without
    -- migrating live data — the alternative is discovering you need the column once there are
    -- messages in the table.
    kind       text not null default 'global',
    project_id uuid references public.projects(id) on delete cascade,

    created_at timestamptz not null default now(),

    constraint rooms_slug_shape  check (slug ~ '^[a-z0-9][a-z0-9_-]{0,39}$'),
    constraint rooms_kind_known  check (kind in ('global', 'project')),
    constraint rooms_project_matches_kind check ((kind = 'project') = (project_id is not null))
);

insert into public.rooms (slug, name, kind)
values ('general', 'VeinScript', 'global')
on conflict (slug) do nothing;

create table public.messages (
    -- A BIGINT IDENTITY, NOT A TIMESTAMP, and this is the whole reason polling works. The client asks
    --     ?id=gt.<last>&order=id.asc
    -- which is exact: no clock skew, no two messages sharing an instant, no message fetched twice or
    -- silently skipped. A created_at cursor has all three failure modes and only shows them in
    -- production.
    id            bigint generated always as identity primary key,

    room_id       uuid not null references public.rooms(id) on delete cascade,

    -- Set null on account deletion, with the handle copied — same reasoning as projects.owner_handle.
    -- A conversation that loses half its names when one person leaves is not a conversation.
    author        uuid references public.profiles(id) on delete set null,
    author_handle citext not null,

    body          text not null,

    created_at    timestamptz not null default now(),
    edited_at     timestamptz,

    -- Soft. Removing your own message must not punch a hole in everyone else's poll cursor, and a
    -- gap in the id sequence is exactly that hole.
    deleted_at    timestamptz,

    constraint messages_body_len check (length(body) between 1 and 2000)
);

-- The polling index, in the shape the poll reads it.
create index messages_room_stream_idx on public.messages (room_id, id);

-- The reference 0006 could not declare, because this table did not exist yet.
alter table public.takedown_requests
    add constraint takedown_message_fk
    foreign key (message_id) references public.messages(id) on delete cascade;

-- ---------------------------------------------------------------------------------------------------
-- Rate limits.
--
-- Cheap to add now and awkward to retrofit once there is traffic. These are not anti-abuse in any
-- serious sense — a determined person makes more accounts — they are the guard against one runaway
-- client loop filling the table overnight.
-- ---------------------------------------------------------------------------------------------------

create or replace function public.messages_rate_limit()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
declare
    recent int;
begin
    select count(*) into recent
      from public.messages
     where author = new.author
       and created_at > now() - interval '1 minute';

    if recent >= 20 then
        raise exception 'slow down — at most 20 messages a minute'
            using errcode = 'check_violation';
    end if;

    return new;
end;
$$;

create trigger messages_rate_limit_before
    before insert on public.messages
    for each row execute function public.messages_rate_limit();

create or replace function public.versions_rate_limit()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
declare
    recent int;
begin
    select count(*) into recent
      from public.project_versions v
      join public.projects p on p.id = v.project_id
     where p.owner = auth.uid()
       and v.created_at > now() - interval '1 hour';

    if recent >= 20 then
        raise exception 'at most 20 published versions an hour'
            using errcode = 'check_violation';
    end if;

    return new;
end;
$$;

create trigger versions_rate_limit_before
    before insert on public.project_versions
    for each row execute function public.versions_rate_limit();

-- ---------------------------------------------------------------------------------------------------
-- What an author may still change about a message.
-- ---------------------------------------------------------------------------------------------------

create or replace function public.messages_guard()
returns trigger
language plpgsql
as $$
begin
    if new.id is distinct from old.id
       or new.room_id is distinct from old.room_id
       or new.author is distinct from old.author
       or new.author_handle is distinct from old.author_handle
       or new.created_at is distinct from old.created_at then
        raise exception 'only the body of a message may be edited';
    end if;

    -- A moderator removes a message; a moderator does not put words in someone's mouth.
    if new.body is distinct from old.body then
        if old.author is distinct from auth.uid() and coalesce(auth.role(), '') <> 'service_role' then
            raise exception 'a moderator may remove a message, not rewrite it';
        end if;
        new.edited_at := now();
    end if;

    return new;
end;
$$;

create trigger messages_guard_update
    before update on public.messages
    for each row execute function public.messages_guard();

-- ---------------------------------------------------------------------------------------------------
-- Policies
--
-- Authenticated for READING as well as posting, per "authenticate before chatting". A signed-out
-- Workbench therefore makes no chat request at all — which is also why the chat panel ships connected
-- to nothing until someone signs in.
-- ---------------------------------------------------------------------------------------------------

alter table public.rooms enable row level security;

create policy rooms_select_signed_in on public.rooms
    for select using (auth.uid() is not null);

-- No insert/update/delete. The one room is seeded above; project rooms will come with the feature
-- that needs them, and a policy written in advance for a shape nobody has designed is a guess.

alter table public.messages enable row level security;

create policy messages_select_signed_in on public.messages
    for select using (auth.uid() is not null);

create policy messages_insert_self on public.messages
    for insert with check (
        author = auth.uid()
        and public.is_active()
        and author_handle = (select p.handle from public.profiles p where p.id = auth.uid())
        and deleted_at is null
    );

create policy messages_update_own on public.messages
    for update using (author = auth.uid()) with check (author = auth.uid());

-- Chat moderation. Narrowed by the guard above to setting deleted_at — the row stays, so the id
-- sequence every client's poll cursor walks is never broken, and the removal is visible as a removal
-- rather than as a message that silently never existed.
create policy messages_update_moderator on public.messages
    for update using (public.is_moderator()) with check (public.is_moderator());

-- No DELETE policy for anyone, moderators included: removing a message is an UPDATE that sets
-- deleted_at, for the reason just given.
