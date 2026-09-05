-- 0003_projects.sql — one row per publishable folder. The mutable half of a published project.
--
-- The immutable half is 0004_versions.sql. What lives here is what an owner may still change after
-- publishing: the summary, the tags, whether it is listed. Not the code, and not who wrote it.
--
-- THE ADDRESS IS THE POINT OF THIS TABLE. `alice/combat` in the corpus and `*alice.Combat` in the
-- language name the same thing, mechanically, and that is enforced rather than hoped for. It is the
-- one precondition for `load` ever resolving against the corpus — which is not built, and cannot be
-- built at all if the two addressing schemes are allowed to drift now.

create table public.projects (
    id            uuid primary key default gen_random_uuid(),

    -- ON DELETE SET NULL, and this is the single most load-bearing clause in the schema.
    --
    -- profiles cascades from auth.users. If this cascaded too, one person closing their account would
    -- delete every bundle a hundred other projects had come to depend on — the exact outcome the
    -- no-owner-delete rule exists to prevent. An orphaned project stays readable, still compiles, and
    -- is visibly unowned.
    owner         uuid references public.profiles(id) on delete set null,

    -- Which is why the handle is copied rather than joined: the name has to outlive the account.
    --
    -- THERE IS NO SEPARATE `author` COLUMN. Every bundle in a published package must be
    -- `by <this handle>` — you cannot publish code attributed to someone else — so an `author` column
    -- would be this one written twice, and two copies of a fact drift. The `by` line in the source is
    -- checked at publish time; the Workbench OFFERS to fix a `by you` rather than rewriting it.
    owner_handle  citext not null,

    -- Lowercase, url-safe. For a bundle this is not free — see projects_slug_is_the_bundle_name below.
    slug          text not null,

    -- The bundle or app name as written, case intact: `Combat`.
    name          text not null,

    -- The same three values ProjectScaffold's ProjectKind produces.
    kind          text not null,

    summary       text,
    tags          text[] not null default '{}',

    -- public | unlisted. There is deliberately no `private`: a project switched private after
    -- publication vanishes for everyone already using it, which is deletion wearing a different word
    -- and walks straight around the moderator.
    --
    -- And unlisted is NOT secret. Published source is searchable whatever its visibility — unlisted
    -- only means "off the browse page". The publish dialog says that in those words, because a person
    -- who reads `unlisted` as `private` will publish something they should not have.
    visibility    text not null default 'public',

    -- Denormalised from the newest version, maintained by a trigger in 0004. A listing of a thousand
    -- projects should not need a join or a lateral to show a version number and a licence.
    latest_seq    int not null default 0,
    latest_license text,

    -- Maintained by the trigger in 0009. The one discovery signal: deliberate, spam-resistant, and it
    -- means something a publish date does not.
    star_count    int not null default 0,

    created_at    timestamptz not null default now(),
    updated_at    timestamptz not null default now(),

    constraint projects_slug_shape       check (slug ~ '^[a-z0-9][a-z0-9._-]{0,63}$'),
    constraint projects_kind_known       check (kind in ('scratch', 'bundle', 'solution')),
    constraint projects_visibility_known check (visibility in ('public', 'unlisted')),
    constraint projects_name_len         check (length(name) between 1 and 64),
    constraint projects_summary_len      check (summary is null or length(summary) <= 200),
    constraint projects_tags_sane        check (cardinality(tags) <= 8),
    constraint projects_counts_sane      check (latest_seq >= 0 and star_count >= 0),

    -- The address rule, for the one kind that can ever be a `load` target. `bundle Combat by alice`
    -- publishes to alice/combat and nowhere else, so `*alice.Combat` names a row.
    --
    -- A solution takes its slug from the app name and a scratch project from its folder name; neither
    -- is loadable, so neither has to be a language address. Two rules would be one too many if the
    -- second one mattered — it does not.
    constraint projects_slug_is_the_bundle_name check (kind <> 'bundle' or slug = lower(name)),

    -- alice/combat and bob/combat coexist, which is exactly how *Author.Bundle already disambiguates
    -- in the language. Scoped to the handle rather than the owner uuid so the address survives the
    -- account being deleted.
    unique (owner_handle, slug)
);

create index projects_owner_idx   on public.projects (owner);
create index projects_listing_idx on public.projects (visibility, updated_at desc);
create index projects_stars_idx   on public.projects (star_count desc, updated_at desc);
create index projects_kind_idx    on public.projects (kind);
create index projects_tags_idx    on public.projects using gin (tags);

-- ---------------------------------------------------------------------------------------------------
-- What cannot change after the first publish.
--
-- pg_trigger_depth() > 1 means we were reached from another trigger — 0004's version hook updating
-- latest_seq and latest_license, or 0009's star counter. Those are the only writers those columns have.
-- ---------------------------------------------------------------------------------------------------

create or replace function public.projects_guard()
returns trigger
language plpgsql
as $$
begin
    if new.owner_handle is distinct from old.owner_handle
       or new.slug is distinct from old.slug
       or new.name is distinct from old.name then
        raise exception 'name/handle/slug is a project''s address: publish under a new slug instead';
    end if;

    if new.kind is distinct from old.kind then
        raise exception 'kind is set by what was published, not edited afterwards';
    end if;

    if new.owner is distinct from old.owner and coalesce(auth.role(), '') <> 'service_role' then
        raise exception 'a project cannot be handed to another account through the API';
    end if;

    if pg_trigger_depth() <= 1
       and (new.latest_seq is distinct from old.latest_seq
            or new.latest_license is distinct from old.latest_license
            or new.star_count is distinct from old.star_count) then
        raise exception 'latest_seq, latest_license and star_count are derived; write the source table';
    end if;

    new.updated_at := now();
    return new;
end;
$$;

create trigger projects_guard_update
    before update on public.projects
    for each row execute function public.projects_guard();

-- ---------------------------------------------------------------------------------------------------
-- Policies
-- ---------------------------------------------------------------------------------------------------

alter table public.projects enable row level security;

-- Readable by anyone, unlisted included: unlisted means "not in the listing", and listings filter
-- `visibility = 'public'` in the QUERY. A policy that hid unlisted rows would break the direct link
-- that is the whole point of the state.
create policy projects_select_all on public.projects
    for select using (true);

-- owner_handle must be the publisher's actual handle, checked against profiles rather than trusted
-- from the client — otherwise anyone could publish under anyone's name.
create policy projects_insert_own on public.projects
    for insert with check (
        owner = auth.uid()
        and public.is_active()
        and owner_handle = (select p.handle from public.profiles p where p.id = auth.uid())
    );

create policy projects_update_own on public.projects
    for update using (owner = auth.uid() and public.is_active())
    with check (owner = auth.uid());

-- The only delete in the corpus, and it belongs to a person who reviewed a request. See 0006.
create policy projects_delete_moderator on public.projects
    for delete using (public.is_moderator());
