-- 0002_profiles.sql — an account's public half, and the one function every other policy leans on.
--
-- WHY POLICIES LIVE BESIDE THEIR TABLE. The anon key ships inside the Workbench, so Row Level Security
-- is not a layer of the security model — it is all of it. A separate rls.sql is a file someone forgets
-- to run, and the cost of forgetting is a public database. Every table in this folder is created,
-- locked and given its policies in one file, so a half-applied migration leaves a table unreadable
-- rather than unprotected.

create table public.profiles (
    id           uuid primary key references auth.users(id) on delete cascade,

    -- The unique VeinScript pseudonym. NOT NULL, so no listing anywhere has to render a nameless
    -- author, and no policy has to handle the case.
    handle       citext not null unique,

    display_name text,
    bio          text,
    avatar_url   text,

    -- user | moderator | admin. Nothing in the corpus is deletable by its owner, so removal needs
    -- someone who can act — see 0006_takedowns.sql. NOT self-service; the trigger below refuses.
    role         text not null default 'user',

    -- Suspension, and the reason it exists: approving a takedown removes a project, and without this
    -- the same account re-uploads it a minute later or keeps flooding the chat. A suspended account
    -- can still sign in and still READ — it simply cannot write anything anywhere.
    suspended_at     timestamptz,
    suspended_reason text,
    suspended_by     uuid references public.profiles(id) on delete set null,

    created_at   timestamptz not null default now(),

    constraint profiles_handle_shape   check (handle ~ '^[a-z0-9_-]{3,32}$'),
    constraint profiles_role_known     check (role in ('user', 'moderator', 'admin')),
    constraint profiles_display_len    check (display_name is null or length(display_name) between 1 and 64),
    constraint profiles_bio_len        check (bio is null or length(bio) <= 500),
    constraint profiles_suspension_together check ((suspended_at is null) = (suspended_reason is null)),

    -- `local` is not vanity: it is what BundleDecl.Author defaults to when a file says `bundle X` with
    -- no `by`. A user holding that handle would make every unattributed bundle look like theirs.
    constraint profiles_handle_reserved check (handle not in (
        'admin', 'administrator', 'moderator', 'mod', 'support', 'help', 'staff', 'team',
        'vein', 'veinscript', 'veinc', 'stdlib', 'local', 'you', 'anonymous', 'system',
        'root', 'api', 'www', 'app', 'docs', 'new', 'null', 'undefined'
    ))
);

comment on column public.profiles.handle is
    'Permanent. Stamped into projects.owner_handle and messages.author_handle at write time, so a '
    'published project keeps its author even after the account is deleted.';

-- ---------------------------------------------------------------------------------------------------
-- Handles are never reissued.
--
-- A profile row cascades away when its auth.users row does, which frees the handle — and that is a
-- hole rather than a feature. `alice/combat` stays in the corpus with a null owner after alice leaves;
-- if a new person could then register `alice`, they would find a project at their own address that
-- they cannot publish to, and everyone else would see alice's old work under the new alice's name.
--
-- So deleting an account retires its handle here. The table is written by the account-deletion path
-- (not built yet — it needs an Edge Function holding the service key), and read by the signup trigger
-- below from the day it exists.
-- ---------------------------------------------------------------------------------------------------

create table public.retired_handles (
    handle     citext primary key,
    retired_at timestamptz not null default now()
);

alter table public.retired_handles enable row level security;

-- Readable so a signup form can say "that handle is not available" before submitting; writable only
-- with a service key, which is what the deletion function will hold.
create policy retired_select_all on public.retired_handles
    for select using (true);

-- ---------------------------------------------------------------------------------------------------
-- is_moderator() — read by policies in every later file.
--
-- SECURITY DEFINER because a policy on `projects` that had to SELECT `profiles` under the caller's own
-- RLS would recurse the moment profiles' policies grow a condition. Defined once, here, so a change to
-- who counts as a moderator is a change in one place.
-- ---------------------------------------------------------------------------------------------------

create or replace function public.is_moderator()
returns boolean
language sql
stable
security definer
set search_path = public
as $$
    select exists (
        select 1 from public.profiles
        where id = auth.uid() and role in ('moderator', 'admin')
    );
$$;

revoke all on function public.is_moderator() from public;
grant execute on function public.is_moderator() to authenticated, anon;

-- is_active() — joined into every INSERT policy in this schema.
--
-- Read-and-sign-in stay open to a suspended account on purpose: locking someone out of their own
-- published work as well as out of writing punishes twice for one decision, and an account that cannot
-- see why it was suspended cannot appeal.
create or replace function public.is_active()
returns boolean
language sql
stable
security definer
set search_path = public
as $$
    select exists (
        select 1 from public.profiles
        where id = auth.uid() and suspended_at is null
    );
$$;

revoke all on function public.is_active() from public;
grant execute on function public.is_active() to authenticated, anon;

-- ---------------------------------------------------------------------------------------------------
-- A signup always produces a profile, and always with a name.
--
-- The handle comes from the signup metadata the website sends:
--     supabase.auth.signUp({ email, password, options: { data: { handle: 'alice' } } })
--
-- Raising here fails the whole signup, which is the intent — an account with no handle would be an
-- account that cannot publish or post, discovered later. To make a user from the Supabase dashboard,
-- fill in User Metadata with {"handle": "..."} or this will refuse it.
-- ---------------------------------------------------------------------------------------------------

create or replace function public.handle_new_user()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
declare
    wanted citext := lower(trim(coalesce(new.raw_user_meta_data ->> 'handle', '')));
    shown  text   := nullif(trim(coalesce(new.raw_user_meta_data ->> 'display_name', '')), '');
begin
    if wanted = '' then
        raise exception 'signup requires a handle (pass it as user metadata: {"handle": "..."})'
            using errcode = 'check_violation';
    end if;

    if exists (select 1 from public.retired_handles r where r.handle = wanted) then
        raise exception 'that handle belonged to a deleted account and is not reissued'
            using errcode = 'check_violation';
    end if;

    insert into public.profiles (id, handle, display_name)
    values (new.id, wanted, shown);

    return new;
end;
$$;

create trigger on_auth_user_created
    after insert on auth.users
    for each row execute function public.handle_new_user();

-- ---------------------------------------------------------------------------------------------------
-- What an account holder may NOT change about themselves.
--
-- `role` is the important one: a user who could set their own role to moderator could delete anyone's
-- bundle. Promotion is a deliberate statement from the SQL editor or a service key — see the note at
-- the bottom of this file.
--
-- `handle` is permanent for the same reason a published version is: it is copied into every project
-- and message at write time, and letting it move would leave those copies pointing at a name that is
-- now someone else's. Reverse this if you would rather allow renames — but then owner_handle and
-- author_handle become historical records rather than the current name, and the UI has to say so.
-- ---------------------------------------------------------------------------------------------------

create or replace function public.profiles_guard()
returns trigger
language plpgsql
as $$
begin
    if new.id is distinct from old.id then
        raise exception 'a profile cannot change owner';
    end if;

    if new.handle is distinct from old.handle then
        raise exception 'handle is permanent: it is stamped into published projects and posted messages';
    end if;

    if new.role is distinct from old.role and coalesce(auth.role(), '') <> 'service_role' then
        raise exception 'role is not self-service: promote from the SQL editor or with a service key';
    end if;

    -- A suspension nobody but its subject can lift is not a suspension.
    if (new.suspended_at is distinct from old.suspended_at
        or new.suspended_reason is distinct from old.suspended_reason)
       and coalesce(auth.role(), '') <> 'service_role'
       and not public.is_moderator() then
        raise exception 'only a moderator suspends or reinstates an account';
    end if;

    if new.suspended_at is distinct from old.suspended_at then
        new.suspended_by := case when new.suspended_at is null then null else auth.uid() end;
    end if;

    -- Editing SOMEONE ELSE'S profile is only ever a suspension. Without this the moderator update
    -- policy below would also let a moderator rewrite anyone's bio and display name, which is a
    -- different power than the one being granted.
    if old.id <> coalesce(auth.uid(), old.id)
       and coalesce(auth.role(), '') <> 'service_role'
       and (new.display_name is distinct from old.display_name
            or new.bio is distinct from old.bio
            or new.avatar_url is distinct from old.avatar_url) then
        raise exception 'a moderator may suspend an account, not rewrite it';
    end if;

    return new;
end;
$$;

create trigger profiles_guard_update
    before update on public.profiles
    for each row execute function public.profiles_guard();

-- ---------------------------------------------------------------------------------------------------
-- Policies
-- ---------------------------------------------------------------------------------------------------

alter table public.profiles enable row level security;

-- Public: a handle next to a published bundle has to resolve for someone who is not signed in.
create policy profiles_select_all on public.profiles
    for select using (true);

-- The trigger above is SECURITY DEFINER and does not need this; it is here so that a profile can also
-- be created by a client holding the user's own JWT, and never for anybody else's id.
create policy profiles_insert_self on public.profiles
    for insert with check (id = auth.uid());

create policy profiles_update_self on public.profiles
    for update using (id = auth.uid()) with check (id = auth.uid());

-- Suspension is the one edit somebody else may make to your profile. The guard trigger above narrows
-- this to the suspension columns alone — a moderator cannot rewrite your bio.
create policy profiles_update_moderator on public.profiles
    for update using (public.is_moderator()) with check (public.is_moderator());

-- No delete policy. A profile goes when its auth.users row goes, and nothing else removes it.

-- ---------------------------------------------------------------------------------------------------
-- The first moderator.
--
-- Left as a comment on purpose: seeding a handle that does not exist yet would make this migration
-- fail on a fresh project. Sign up on the site first, then run this once, in the SQL editor:
--
--     update public.profiles set role = 'admin' where handle = 'your-handle';
--
-- Until someone holds that role, is_moderator() is false for everyone and nothing in the corpus can
-- be deleted by anyone at all — which is a safe place to start rather than a problem.
-- ---------------------------------------------------------------------------------------------------
