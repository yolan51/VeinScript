-- 0005_files.sql — the source itself.
--
-- Text columns, not Supabase Storage. .vein files are small, and a corpus held in `text` is a corpus
-- you can SEARCH — which is the actual payoff of "a database of source code" rather than a pile of
-- downloadable archives.

create table public.project_files (
    id         uuid primary key default gen_random_uuid(),
    version_id uuid not null references public.project_versions(id) on delete cascade,

    -- Relative to the project folder, forward slashes. The check is not tidiness: this path is used
    -- to write files back onto someone's disk when they save a copy, and `../../.ssh/authorized_keys`
    -- is a real path. Refuse it at the point of storage, so no restorer has to remember to.
    path       text not null,

    content    text not null,
    bytes      int  not null,

    -- So a re-publish can show what actually changed, and a restore can prove it got the same bytes.
    sha256     text not null,

    constraint files_path_relative check (
        path ~ '^[A-Za-z0-9._][A-Za-z0-9 ._/-]*$'    -- no backslash, no colon, no leading slash or dot-dot
        and path !~ '(^|/)\.\.(/|$)'
        and length(path) <= 400
    ),
    constraint files_content_size check (length(content) <= 524288),   -- 512 KB
    constraint files_bytes_sane   check (bytes >= 0 and bytes <= 524288),
    constraint files_sha_shape    check (sha256 ~ '^[0-9a-f]{64}$'),

    unique (version_id, path)
);

create index files_version_idx on public.project_files (version_id, path);

-- Full-text over every published file. This is what makes the corpus searchable rather than merely
-- stored — "who else has written a `hear @FileLoaded`" is one query, not a download loop.
create index files_content_fts_idx on public.project_files
    using gin (to_tsvector('simple', content));

-- ---------------------------------------------------------------------------------------------------
-- WHERE THE COUNTERS COME FROM, and why not from here.
--
-- An earlier draft incremented project_versions.file_count from a per-row trigger on this table. That
-- is only correct if every file of a publish actually arrives — and a publish made of N separate
-- PostgREST calls can stop at file 7 of 20, leaving a version that reports 7 files and looks entirely
-- healthy. The counters would have been faithfully wrong.
--
-- So the counting moved to publish_version() in 0010, which writes the version and all of its files in
-- ONE transaction and counts what it actually wrote. There is no per-file trigger here on purpose.
-- ---------------------------------------------------------------------------------------------------

-- ---------------------------------------------------------------------------------------------------
-- Policies
-- ---------------------------------------------------------------------------------------------------

alter table public.project_files enable row level security;

create policy files_select_readable on public.project_files
    for select using (
        exists (select 1 from public.project_versions v where v.id = version_id)
    );

create policy files_insert_owner on public.project_files
    for insert with check (
        public.is_active()
        and exists (
            select 1
              from public.project_versions v
              join public.projects p on p.id = v.project_id
             where v.id = version_id and p.owner = auth.uid()
        )
    );

-- NO UPDATE POLICY — a published file is a published file.

create policy files_delete_moderator on public.project_files
    for delete using (public.is_moderator());
