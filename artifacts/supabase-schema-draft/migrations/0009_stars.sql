-- 0009_stars.sql — the one discovery signal.
--
-- Without it the corpus sorts by whoever published most recently, which rewards noise. A star is
-- deliberate, costs the person something to give, and cannot be inflated by a client loop the way a
-- view or download counter can.
--
-- Note what a star is NOT: it is not corpus content. Unstarring is a real DELETE — the no-delete rule
-- protects published source that other people build on, and nobody's build depends on your opinion.

create table public.stars (
    user_id    uuid not null references public.profiles(id) on delete cascade,
    project_id uuid not null references public.projects(id) on delete cascade,
    created_at timestamptz not null default now(),

    primary key (user_id, project_id)
);

-- "Projects I starred", which is the reading list half of the feature.
create index stars_user_idx on public.stars (user_id, created_at desc);

-- ---------------------------------------------------------------------------------------------------
-- projects.star_count is derived.
--
-- SECURITY DEFINER because the person starring your project has no update policy on it — and should
-- not. The count is maintained past RLS, and the guard in 0003 lets it through on pg_trigger_depth.
-- ---------------------------------------------------------------------------------------------------

create or replace function public.stars_recount()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
    if tg_op = 'INSERT' then
        update public.projects set star_count = star_count + 1 where id = new.project_id;
        return new;
    else
        update public.projects set star_count = greatest(star_count - 1, 0) where id = old.project_id;
        return old;
    end if;
end;
$$;

create trigger stars_recount_write
    after insert or delete on public.stars
    for each row execute function public.stars_recount();

-- ---------------------------------------------------------------------------------------------------
-- Policies
-- ---------------------------------------------------------------------------------------------------

alter table public.stars enable row level security;

-- Public: "who starred this" is part of what makes a star worth giving.
create policy stars_select_all on public.stars
    for select using (true);

create policy stars_insert_self on public.stars
    for insert with check (user_id = auth.uid() and public.is_active());

create policy stars_delete_self on public.stars
    for delete using (user_id = auth.uid());

-- No update policy: a star has nothing to change. Unstar and star again.
