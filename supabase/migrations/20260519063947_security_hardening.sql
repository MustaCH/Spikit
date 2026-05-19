-- ============================================================
-- Security hardening sobre la migración inicial.
-- Aplica recomendaciones del database linter de Supabase post-migración:
--   - 0011 function_search_path_mutable (WARN)
--   - 0003 auth_rls_initplan          (WARN)
-- Ticket: EP-10.2
-- ============================================================

-- 1. Fijar search_path de la función del trigger para evitar function hijacking
--    desde schemas privados. La función ya usa nombres schema-qualified
--    (public.byok_whitelist_audit), así que search_path vacío es seguro.
create or replace function public.tg_byok_whitelist_audit()
returns trigger
language plpgsql
set search_path = ''
as $$
begin
  if TG_OP = 'INSERT' then
    insert into public.byok_whitelist_audit
      (changed_by, action, email, old_status, new_status, notes)
      values (coalesce(new.created_by, 'unknown'), 'invite', new.email, null, new.status, new.notes);
  elsif TG_OP = 'UPDATE' then
    if old.status is distinct from new.status then
      insert into public.byok_whitelist_audit
        (changed_by, action, email, old_status, new_status, notes)
        values (coalesce(new.created_by, 'unknown'),
                case when new.status = 'revoked'::public.whitelist_status then 'revoke' else 'reactivate' end,
                new.email, old.status, new.status, new.notes);
    end if;
  end if;
  return new;
end;
$$;

-- 2. Recrear la policy envolviendo auth.uid() en un subselect para que se
--    evalúe una sola vez por query en lugar de una por fila.
--    Ref: https://supabase.com/docs/guides/database/postgres/row-level-security#call-functions-with-select
drop policy if exists entitlements_select_own on public.entitlements;

create policy entitlements_select_own on public.entitlements
  for select using ((select auth.uid()) = user_id);
