-- ============================================================
-- pg_cron: daily_entitlement_sweep
-- ============================================================
-- Función SQL que corre 1×/día (03:00 UTC) y aplica las transiciones
-- de tier que no son reactivas:
--   1. Trials vencidos        → tier='expired'
--   2. BYOK con grace vencido → tier='expired' + byok_grace_*=NULL
--   3. Período rolling 30d cumplido → minutes_used_period=0, period_started_at=now()
--
-- El disparo de emails de transición queda para EP-10.10 (Resend + pg_net).
--
-- Ticket: EP-10.9 | Origen: ADR-0007 § 6
-- ============================================================

create extension if not exists pg_cron;

create or replace function public.daily_entitlement_sweep()
returns void
language plpgsql
security definer
set search_path = pg_catalog, public
as $$
begin
  -- 1. Trials vencidos → expired
  update public.entitlements
     set tier = 'expired',
         updated_at = now()
   where tier = 'trial'
     and trial_ends_at < now();

  -- 2. BYOK con grace vencido → expired
  update public.entitlements
     set tier = 'expired',
         byok_grace_started_at = null,
         byok_grace_ends_at = null,
         updated_at = now()
   where tier = 'byok'
     and byok_grace_ends_at is not null
     and byok_grace_ends_at < now();

  -- 3. Reset del soft cap (ventana rolling de 30 días)
  update public.entitlements
     set minutes_used_period = 0,
         period_started_at = now(),
         updated_at = now()
   where period_started_at + interval '30 days' < now();
end;
$$;

-- Lock down: solo service_role puede invocar la función directamente.
-- (security definer corre con permisos del owner, pero igual restringimos EXECUTE).
revoke all on function public.daily_entitlement_sweep() from public;
revoke all on function public.daily_entitlement_sweep() from anon, authenticated;
grant execute on function public.daily_entitlement_sweep() to service_role;

-- Schedule idempotente: solo crea si no existe.
do $schedule$
begin
  if not exists (select 1 from cron.job where jobname = 'daily-entitlement-sweep') then
    perform cron.schedule(
      'daily-entitlement-sweep',
      '0 3 * * *',
      $cmd$ select public.daily_entitlement_sweep(); $cmd$
    );
  end if;
end;
$schedule$;
