-- 0099_checks.sql — proof that the policies hold. Not a migration: nothing here is left behind.
--
-- The anon key ships inside the Workbench, so RLS is the entire security model. A schema whose
-- policies were never exercised is a schema whose policies are a guess.
--
-- Run this whole file in the Supabase SQL editor after 0001-0010. It makes three accounts, has them
-- try everything the rules forbid, and ROLLS BACK at the end — so it is safe to run as often as you
-- like, including against a live database.
--
-- Every check prints `ok:` when the database refused. Any check that does not print stopped the script
-- with `FAIL:` — read the message, it names what got through.

begin;

-- ---------------------------------------------------------------------------------------------------
-- Three accounts. Going through auth.users on purpose: the signup trigger is part of what is tested,
-- including its refusal to make an account with no handle.
-- ---------------------------------------------------------------------------------------------------

insert into auth.users (instance_id, id, aud, role, email, encrypted_password,
                        raw_app_meta_data, raw_user_meta_data, created_at, updated_at)
values
  ('00000000-0000-0000-0000-000000000000', '11111111-1111-1111-1111-111111111111',
   'authenticated', 'authenticated', 'alice@example.test', '',
   '{}'::jsonb, '{"handle":"alice"}'::jsonb, now(), now()),
  ('00000000-0000-0000-0000-000000000000', '22222222-2222-2222-2222-222222222222',
   'authenticated', 'authenticated', 'bob@example.test', '',
   '{}'::jsonb, '{"handle":"bob"}'::jsonb, now(), now()),
  ('00000000-0000-0000-0000-000000000000', '33333333-3333-3333-3333-333333333333',
   'authenticated', 'authenticated', 'mod@example.test', '',
   '{}'::jsonb, '{"handle":"referee"}'::jsonb, now(), now());

update public.profiles set role = 'moderator' where handle = 'referee';

do $$
begin
    if (select count(*) from public.profiles where handle in ('alice','bob','referee')) <> 3 then
        raise exception 'FAIL: the signup trigger did not create every profile';
    end if;
    raise notice 'ok: a signup creates a profile carrying its handle';
end $$;

do $$
begin
    begin
        insert into auth.users (instance_id, id, aud, role, email, encrypted_password,
                                raw_app_meta_data, raw_user_meta_data, created_at, updated_at)
        values ('00000000-0000-0000-0000-000000000000', '44444444-4444-4444-4444-444444444444',
                'authenticated', 'authenticated', 'nameless@example.test', '',
                '{}'::jsonb, '{}'::jsonb, now(), now());
        raise exception 'FAIL: an account was created with no handle';
    exception when check_violation then
        raise notice 'ok: signup without a handle is refused';
    end;
end $$;

-- ---------------------------------------------------------------------------------------------------
-- Alice publishes, through the RPC, as herself, under RLS.
-- ---------------------------------------------------------------------------------------------------

set local role authenticated;
set local request.jwt.claims = '{"sub":"11111111-1111-1111-1111-111111111111","role":"authenticated"}';

do $$
declare r jsonb;
begin
    r := public.publish_version(
        p_slug       => 'combat',
        p_name       => 'Combat',
        p_kind       => 'bundle',
        p_entry_path => 'combat/combat.vein',
        p_license    => 'MIT',
        p_files      => jsonb_build_array(
            jsonb_build_object('path', 'combat/combat.vein', 'content', 'bundle Combat by alice { }'),
            jsonb_build_object('path', 'combat/shards/Boot.vein', 'content', 'shard Boot { run once { } }')
        ),
        p_notes      => 'first cut',
        p_summary    => 'Hit points and damage.',
        p_compiler_version => '0.1.0',
        p_warning_count    => 2
    );

    if (r ->> 'seq')::int <> 1 then raise exception 'FAIL: first version was not seq 1'; end if;
    if (r ->> 'file_count')::int <> 2 then raise exception 'FAIL: file_count wrong'; end if;
    raise notice 'ok: publish_version writes a project, a version and its files in one call';

    if (select latest_seq from public.projects where owner_handle = 'alice') <> 1 then
        raise exception 'FAIL: projects.latest_seq did not follow the version';
    end if;
    if (select total_bytes from public.project_versions
         where project_id = (r ->> 'project_id')::uuid) <> 53 then
        raise exception 'FAIL: total_bytes was not computed from what was stored';
    end if;
    raise notice 'ok: derived columns are computed by the database, not sent by the client';

    if (select count(*) from public.project_files where sha256 !~ '^[0-9a-f]{64}$') > 0 then
        raise exception 'FAIL: a sha256 was not computed';
    end if;
    raise notice 'ok: every stored file carries a hash of what was actually stored';
end $$;

-- G1, the reason this RPC exists: a publish that fails partway leaves NOTHING.
do $$
declare before_p int; before_v int; before_f int;
begin
    select count(*) into before_p from public.projects;
    select count(*) into before_v from public.project_versions;
    select count(*) into before_f from public.project_files;

    begin
        perform public.publish_version(
            p_slug => 'combat', p_name => 'Combat', p_kind => 'bundle',
            p_entry_path => 'combat/combat.vein', p_license => 'MIT',
            p_files => jsonb_build_array(
                jsonb_build_object('path', 'combat/ok.vein', 'content', 'x'),
                jsonb_build_object('path', 'combat/bad.vein')          -- no content: refused mid-loop
            ));
        raise exception 'FAIL: a publish with a malformed file was accepted';
    exception when others then
        null;
    end;

    if (select count(*) from public.projects) <> before_p
       or (select count(*) from public.project_versions) <> before_v
       or (select count(*) from public.project_files) <> before_f then
        raise exception 'FAIL: a half-finished publish left rows behind';
    end if;
    raise notice 'ok: a publish that fails partway leaves nothing behind';
end $$;

do $$
begin
    begin
        perform public.publish_version(
            p_slug => 'notthename', p_name => 'Combat2', p_kind => 'bundle',
            p_entry_path => 'x.vein', p_license => 'MIT',
            p_files => jsonb_build_array(jsonb_build_object('path','x.vein','content','x')));
        raise exception 'FAIL: a bundle published to a slug that is not its name';
    exception when check_violation then
        raise notice 'ok: a bundle can only publish to alice/<lowercased bundle name>';
    end;
end $$;

do $$
begin
    begin
        perform public.publish_version(
            p_slug => 'combat', p_name => 'Combat', p_kind => 'bundle',
            p_entry_path => 'x.vein', p_license => 'GPL-3.0',
            p_files => jsonb_build_array(jsonb_build_object('path','x.vein','content','x')));
        raise exception 'FAIL: a copyleft licence was accepted';
    exception when check_violation then
        raise notice 'ok: the licence list is closed';
    end;
end $$;

-- ---------------------------------------------------------------------------------------------------
-- Bob may read everything and change nothing of Alice's.
-- ---------------------------------------------------------------------------------------------------

set local request.jwt.claims = '{"sub":"22222222-2222-2222-2222-222222222222","role":"authenticated"}';

do $$
declare n int;
begin
    select count(*) into n from public.project_files;
    if n <> 2 then raise exception 'FAIL: published files were not readable (got %)', n; end if;
    raise notice 'ok: the corpus is readable';
end $$;

do $$
declare n int;
begin
    update public.projects set summary = 'hijacked' where owner_handle = 'alice';
    get diagnostics n = row_count;
    if n <> 0 then raise exception 'FAIL: another account edited a project'; end if;

    update public.project_versions set notes = 'tampered';
    get diagnostics n = row_count;
    if n <> 0 then raise exception 'FAIL: a published version was updated'; end if;

    update public.project_files set content = 'malware';
    get diagnostics n = row_count;
    if n <> 0 then raise exception 'FAIL: a published file was rewritten'; end if;

    raise notice 'ok: published content is immutable to everyone';
end $$;

do $$
declare n int;
begin
    delete from public.project_versions;
    get diagnostics n = row_count;
    if n <> 0 then raise exception 'FAIL: a non-moderator deleted a version'; end if;

    delete from public.projects;
    get diagnostics n = row_count;
    if n <> 0 then raise exception 'FAIL: a non-moderator deleted a project'; end if;

    raise notice 'ok: nothing in the corpus is deletable without a moderator';
end $$;

do $$
begin
    begin
        perform public.publish_version(
            p_slug => 'combat', p_name => 'Combat', p_kind => 'bundle',
            p_entry_path => 'x.vein', p_license => 'MIT',
            p_files => jsonb_build_array(jsonb_build_object('path','x.vein','content','x')));
        raise exception 'FAIL: bob published into alice/combat';
    exception when insufficient_privilege or unique_violation or check_violation then
        raise notice 'ok: you cannot publish into someone else''s project';
    end;
end $$;

do $$
begin
    begin
        insert into public.projects (owner, owner_handle, slug, name, kind)
        values (auth.uid(), 'alice', 'stolen', 'Stolen', 'bundle');
        raise exception 'FAIL: a project was published under another handle';
    exception when insufficient_privilege then
        raise notice 'ok: you cannot publish under another handle';
    end;
end $$;

do $$
begin
    begin
        update public.profiles set role = 'admin' where id = auth.uid();
        raise exception 'FAIL: an account promoted itself';
    exception when raise_exception then
        raise notice 'ok: role is not self-service';
    end;

    begin
        update public.profiles set handle = 'alice2' where id = auth.uid();
        raise exception 'FAIL: a handle was changed after copies of it were stamped into rows';
    exception when raise_exception then
        raise notice 'ok: a handle is permanent';
    end;

    -- Written against his OWN row on purpose. Aiming at alice's row would be filtered by the update
    -- policy and update zero rows without raising, which would prove the policy and not the trigger —
    -- and it is the trigger that has to hold when the policy lets someone through.
    begin
        update public.profiles set suspended_at = now(), suspended_reason = 'by myself'
         where id = auth.uid();
        raise exception 'FAIL: an account wrote its own suspension state';
    exception when raise_exception then
        raise notice 'ok: only a moderator suspends or reinstates';
    end;
end $$;

-- Bob stars Alice's project; the count follows.
insert into public.stars (user_id, project_id)
select auth.uid(), id from public.projects where owner_handle = 'alice';

do $$
begin
    if (select star_count from public.projects where owner_handle = 'alice') <> 1 then
        raise exception 'FAIL: star_count did not follow the stars table';
    end if;
    raise notice 'ok: a star moves the denormalised count';
end $$;

-- ---------------------------------------------------------------------------------------------------
-- Yanking: the owner withdraws, only a moderator lifts.
-- ---------------------------------------------------------------------------------------------------

do $$
declare n int;
begin
    insert into public.version_yanks (version_id, reason)
    select id, 'bob should not be able to do this' from public.project_versions limit 1;
    raise exception 'FAIL: a stranger yanked someone else''s version';
exception
    when insufficient_privilege then raise notice 'ok: only the owner or a moderator yanks a version';
end $$;

set local request.jwt.claims = '{"sub":"11111111-1111-1111-1111-111111111111","role":"authenticated"}';

insert into public.version_yanks (version_id, reason)
select id, 'this release leaks the staging key' from public.project_versions limit 1;

do $$
declare n int;
begin
    if (select count(*) from public.project_versions v
         join public.version_yanks y on y.version_id = v.id
        where y.lifted_at is null) <> 1 then
        raise exception 'FAIL: the yank was not recorded';
    end if;
    raise notice 'ok: an owner can withdraw a release without deleting it';

    update public.version_yanks set lifted_at = now();
    get diagnostics n = row_count;
    if n <> 0 then raise exception 'FAIL: an owner lifted their own yank'; end if;
    raise notice 'ok: only a moderator lifts a yank';

    -- The owner DOES hold an update policy on projects, so this one reaches the guard trigger rather
    -- than being filtered away — which is the point: a derived column has to be unwritable even to
    -- someone the policy admits.
    begin
        update public.projects set star_count = 9999 where owner_handle = 'alice';
        raise exception 'FAIL: a derived count was written by hand';
    exception when raise_exception then
        raise notice 'ok: derived counts cannot be written by hand';
    end;
end $$;

-- ---------------------------------------------------------------------------------------------------
-- Chat: signed in, as yourself, and never hard-deleted.
-- ---------------------------------------------------------------------------------------------------

insert into public.messages (room_id, author, author_handle, body)
select id, auth.uid(), 'alice', 'first post' from public.rooms where slug = 'general';

do $$
begin
    begin
        insert into public.messages (room_id, author, author_handle, body)
        select id, '22222222-2222-2222-2222-222222222222', 'bob', 'not me'
          from public.rooms where slug = 'general';
        raise exception 'FAIL: a message was posted as another user';
    exception when insufficient_privilege then
        raise notice 'ok: you can only post as yourself';
    end;
end $$;

do $$
declare n int;
begin
    delete from public.messages;
    get diagnostics n = row_count;
    if n <> 0 then raise exception 'FAIL: a message was hard-deleted, breaking the poll cursor'; end if;
    raise notice 'ok: message removal is a soft update, not a delete';
end $$;

-- ---------------------------------------------------------------------------------------------------
-- The moderator: suspend, and see what suspension actually stops.
-- ---------------------------------------------------------------------------------------------------

set local request.jwt.claims = '{"sub":"33333333-3333-3333-3333-333333333333","role":"authenticated"}';

update public.profiles
   set suspended_at = now(), suspended_reason = 'testing'
 where handle = 'alice';

do $$
begin
    if (select suspended_by from public.profiles where handle = 'alice')
       <> '33333333-3333-3333-3333-333333333333'::uuid then
        raise exception 'FAIL: suspended_by was not stamped';
    end if;
    raise notice 'ok: a moderator can suspend, and the decision is attributed';

    begin
        update public.profiles set bio = 'rewritten by a moderator' where handle = 'alice';
        raise exception 'FAIL: a moderator rewrote someone''s profile';
    exception when raise_exception then
        raise notice 'ok: a moderator may suspend an account, not rewrite it';
    end;
end $$;

set local request.jwt.claims = '{"sub":"11111111-1111-1111-1111-111111111111","role":"authenticated"}';

do $$
declare n int;
begin
    -- Still reads.
    select count(*) into n from public.messages;
    if n < 1 then raise exception 'FAIL: a suspended account lost read access'; end if;

    begin
        insert into public.messages (room_id, author, author_handle, body)
        select id, auth.uid(), 'alice', 'still here' from public.rooms where slug = 'general';
        raise exception 'FAIL: a suspended account posted';
    exception when insufficient_privilege then
        raise notice 'ok: a suspended account reads but cannot write';
    end;
end $$;

-- ---------------------------------------------------------------------------------------------------
-- Signed out: the corpus is public, the chat is not.
-- ---------------------------------------------------------------------------------------------------

set local request.jwt.claims = '';
set local role anon;

do $$
declare n int;
begin
    select count(*) into n from public.messages;
    if n <> 0 then raise exception 'FAIL: a signed-out reader saw the chat'; end if;

    select count(*) into n from public.projects;
    if n <> 1 then raise exception 'FAIL: the corpus was not readable signed out (got %)', n; end if;

    raise notice 'ok: the corpus is public, the chat is not';
end $$;

reset role;
rollback;

-- Expected output, in order:
--   ok: a signup creates a profile carrying its handle
--   ok: signup without a handle is refused
--   ok: publish_version writes a project, a version and its files in one call
--   ok: derived columns are computed by the database, not sent by the client
--   ok: every stored file carries a hash of what was actually stored
--   ok: a publish that fails partway leaves nothing behind
--   ok: a bundle can only publish to alice/<lowercased bundle name>
--   ok: the licence list is closed
--   ok: the corpus is readable
--   ok: published content is immutable to everyone
--   ok: nothing in the corpus is deletable without a moderator
--   ok: you cannot publish into someone else's project
--   ok: you cannot publish under another handle
--   ok: role is not self-service
--   ok: a handle is permanent
--   ok: only a moderator suspends or reinstates
--   ok: a star moves the denormalised count
--   ok: only the owner or a moderator yanks a version
--   ok: an owner can withdraw a release without deleting it
--   ok: only a moderator lifts a yank
--   ok: derived counts cannot be written by hand
--   ok: you can only post as yourself
--   ok: message removal is a soft update, not a delete
--   ok: a moderator can suspend, and the decision is attributed
--   ok: a moderator may suspend an account, not rewrite it
--   ok: a suspended account reads but cannot write
--   ok: the corpus is public, the chat is not
