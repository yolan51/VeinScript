-- 0010_publish_rpc.sql — publishing, as ONE call.
--
-- THE BUG THIS EXISTS TO PREVENT. A publish made of separate PostgREST calls is
--     insert project → insert version → insert file × N
-- and there is no transaction around it. Drop the connection after file 7 of 20 and the corpus holds
-- a version that claims to be a project and is missing thirteen files. Nothing marks it broken. The
-- compile gate — error_count = 0 — becomes a statement about a package that was never fully stored,
-- which is worse than no gate at all, because it reads as a guarantee.
--
-- So a publish is one function call, one transaction, all or nothing.
--
-- SECURITY INVOKER (the default, stated here because it is load-bearing): every insert below still
-- goes through Row Level Security as the caller. This function is a transaction boundary, not a
-- privilege escalation — it can do nothing the caller could not have done one statement at a time.
--
-- WHAT THE DATABASE WILL NOT CHECK. That every `bundle X by Y` inside the package says `by <your
-- handle>` needs a VeinScript parser, so it stays in the Workbench, where it can also OFFER to fix the
-- line. What is enforced here is the half that matters for attribution: owner_handle is read from your
-- profile and never taken from the client.

-- The one thing in this schema that can touch a published version, and all it can touch is two derived
-- integers. It exists because publish_version below is SECURITY INVOKER — deliberately — and is
-- therefore subject to project_versions having no UPDATE policy. Weakening that policy to let a
-- publish write its own counters would trade the immutability guarantee for a convenience.
create or replace function public.version_set_counts(v_id uuid, v_files int, v_bytes bigint)
returns void
language sql
security definer
set search_path = public
as $$
    update public.project_versions
       set file_count = v_files, total_bytes = v_bytes
     where id = v_id;
$$;

revoke all on function public.version_set_counts(uuid, int, bigint) from public;
grant execute on function public.version_set_counts(uuid, int, bigint) to authenticated;

create or replace function public.publish_version(
    p_slug             text,
    p_name             text,
    p_kind             text,
    p_entry_path       text,
    p_license          text,
    p_files            jsonb,                    -- [{"path": "...", "content": "..."}, …]
    p_notes            text default null,
    p_summary          text default null,
    -- null, not '{}': on a re-publish these coalesce onto what is already stored, and an empty array
    -- default would silently clear a project's tags every time you shipped a version without them.
    p_tags             text[] default null,
    p_visibility       text default null,
    p_compiler_version text default null,
    p_warning_count    int default 0
)
returns jsonb
language plpgsql
security invoker
as $$
declare
    me           uuid   := auth.uid();
    my_handle    citext;
    proj         public.projects%rowtype;
    new_version  uuid;
    seq_assigned int;
    total        bigint := 0;
    written      int    := 0;
    f            jsonb;
    body         text;
begin
    if me is null then
        raise exception 'sign in to publish' using errcode = 'insufficient_privilege';
    end if;

    select p.handle into my_handle from public.profiles p where p.id = me;
    if my_handle is null then
        raise exception 'this account has no profile' using errcode = 'insufficient_privilege';
    end if;

    -- ---- what a single publish may carry ----------------------------------------------------------
    --
    -- Caps rather than trust. A runaway packager that walked out of the project folder would otherwise
    -- try to upload a whole disk, and the failure would arrive as a timeout with no explanation.

    if jsonb_typeof(p_files) <> 'array' then
        raise exception 'files must be a JSON array of {path, content}';
    end if;

    if jsonb_array_length(p_files) = 0 then
        raise exception 'a version with no files is not a version';
    end if;

    if jsonb_array_length(p_files) > 400 then
        raise exception 'a publish carries at most 400 files (this one had %)', jsonb_array_length(p_files);
    end if;

    -- ---- the project ------------------------------------------------------------------------------

    select * into proj
      from public.projects
     where owner_handle = my_handle and slug = p_slug;

    if not found then
        insert into public.projects (owner, owner_handle, slug, name, kind, summary, tags, visibility)
        values (me, my_handle, p_slug, p_name, p_kind, p_summary,
                coalesce(p_tags, '{}'), coalesce(p_visibility, 'public'))
        returning * into proj;

    else
        if proj.owner is distinct from me then
            -- Reachable only if the handle was retired and reissued; see retired_handles in 0002.
            raise exception 'the project %/% is not yours', my_handle, p_slug
                using errcode = 'insufficient_privilege';
        end if;

        if proj.kind is distinct from p_kind or proj.name is distinct from p_name then
            raise exception 'this project is % named %; a different one needs a different slug',
                proj.kind, proj.name;
        end if;

        -- Metadata travels with the publish, because that is when a person is actually thinking about
        -- how to describe it. Nulls leave what is already there alone.
        update public.projects
           set summary    = coalesce(p_summary, summary),
               tags       = coalesce(p_tags, tags),
               visibility = coalesce(p_visibility, visibility)
         where id = proj.id;
    end if;

    -- ---- the version ------------------------------------------------------------------------------
    --
    -- error_count is not a parameter. The gate is `versions_clean check (error_count = 0)`, and a
    -- client that could pass the number could pass a zero.

    insert into public.project_versions
        (project_id, entry_path, license, notes, compiler_version, warning_count)
    values
        (proj.id, p_entry_path, p_license, p_notes, p_compiler_version, greatest(coalesce(p_warning_count, 0), 0))
    returning id, seq into new_version, seq_assigned;

    -- ---- the files --------------------------------------------------------------------------------
    --
    -- bytes and sha256 are COMPUTED HERE rather than accepted. A hash the client supplies describes
    -- what the client meant to send; this one describes what is actually stored, and those differ
    -- exactly when it matters — a truncated read, a line-ending rewrite, a mangled encoding.
    --
    -- sha256() is a PostgreSQL built-in (11+), deliberately in preference to pgcrypto's digest():
    -- Supabase installs extensions into the `extensions` schema and a plain PostgreSQL into `public`,
    -- so a qualified call to digest() is right on exactly one of them. convert_to(…, 'UTF8') makes the
    -- hash a hash of bytes rather than of whatever the server encoding happens to be.

    for f in select value from jsonb_array_elements(p_files) loop
        body := f ->> 'content';

        if body is null or (f ->> 'path') is null then
            raise exception 'every file needs a path and content';
        end if;

        total := total + octet_length(body);
        if total > 8 * 1024 * 1024 then
            raise exception 'a publish carries at most 8 MB of source';
        end if;

        insert into public.project_files (version_id, path, content, bytes, sha256)
        values (new_version,
                f ->> 'path',
                body,
                octet_length(body),
                encode(sha256(convert_to(body, 'UTF8')), 'hex'));

        written := written + 1;
    end loop;

    -- The counters describe what this transaction actually wrote, not what a client claimed.
    perform public.version_set_counts(new_version, written, total);

    return jsonb_build_object(
        'project_id',  proj.id,
        'version_id',  new_version,
        'seq',         seq_assigned,
        'slug',        p_slug,
        'owner_handle', my_handle,
        'file_count',  written,
        'total_bytes', total
    );
end;
$$;

grant execute on function public.publish_version(
    text, text, text, text, text, jsonb, text, text, text[], text, text, int
) to authenticated;
