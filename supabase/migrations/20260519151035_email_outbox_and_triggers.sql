-- ============================================================
-- Email outbox + triggers (EP-10.10)
-- ============================================================
-- Esta migration arma la parte SQL del sistema de emails transaccionales:
--   1. Tabla email_outbox: cola de emails pendientes de envío.
--   2. Trigger AFTER INSERT sobre auth.users → encola 'welcome'.
--   3. Trigger AFTER UPDATE sobre byok_whitelist → encola 'byok_revoked_grace_start'
--      y muta entitlements para activar/limpiar grace (cubre el gap reportado
--      en EP-10.2 sobre la sincronización byok_whitelist → entitlements).
--   4. Rewrite de daily_entitlement_sweep() para encolar los 6 emails de
--      cadence (trial T+7/T-1/T+15/T+30, BYOK grace T-7/expired) además de
--      mantener las 3 mutaciones de tier originales de EP-10.9.
--
-- El procesamiento de la outbox (lectura → POST a la Edge Function
-- send-transition-email → marcar como sent/failed) NO se implementa acá.
-- Queda como pendiente operativo para /devops via pg_net o worker externo.
--
-- Ticket: EP-10.10 | Origen: ADR-0007 § 7 + § 2 (transiciones BYOK)
-- ============================================================

-- ------------------------------------------------------------
-- 1. Tabla email_outbox
-- ------------------------------------------------------------
create table if not exists public.email_outbox (
  id          bigserial primary key,
  template    text not null,
  user_id     uuid references auth.users(id) on delete set null,
  to_email    text,
  vars        jsonb not null default '{}'::jsonb,
  status      text not null default 'pending', -- pending | sent | failed | skipped
  attempts    int not null default 0,
  last_error  text,
  created_at  timestamptz not null default now(),
  sent_at     timestamptz
);

create index if not exists email_outbox_pending_idx
  on public.email_outbox (created_at)
  where status = 'pending';

create index if not exists email_outbox_user_idx
  on public.email_outbox (user_id, created_at desc);

-- RLS: cero acceso desde anon/authenticated. Solo service_role.
alter table public.email_outbox enable row level security;
-- (sin policies → autenticación normal no puede ni leer la tabla)

revoke all on public.email_outbox from public;
revoke all on public.email_outbox from anon, authenticated;
grant select, insert, update on public.email_outbox to service_role;
grant usage, select on sequence public.email_outbox_id_seq to service_role;

-- ------------------------------------------------------------
-- 2. Trigger AFTER INSERT sobre auth.users → encolar welcome
-- ------------------------------------------------------------
create or replace function public._enqueue_welcome_email()
returns trigger
language plpgsql
security definer
set search_path = pg_catalog, public
as $$
begin
  if new.email is null then
    return new;
  end if;
  insert into public.email_outbox (template, user_id, to_email, vars)
  values (
    'welcome',
    new.id,
    new.email,
    jsonb_build_object('email', new.email)
  );
  return new;
end;
$$;

drop trigger if exists enqueue_welcome_email on auth.users;
create trigger enqueue_welcome_email
  after insert on auth.users
  for each row execute function public._enqueue_welcome_email();

-- ------------------------------------------------------------
-- 3. Trigger AFTER UPDATE sobre byok_whitelist
--    - status: active → revoked  → activar grace 30d + encolar email
--    - status: revoked → active  → limpiar grace
-- ------------------------------------------------------------
create or replace function public._on_byok_whitelist_status_change()
returns trigger
language plpgsql
security definer
set search_path = pg_catalog, public
as $$
declare
  v_user_id uuid;
  v_grace_ends timestamptz;
begin
  if old.status is not distinct from new.status then
    return new;
  end if;

  -- Buscar al user en auth.users por email (case-insensitive).
  select id into v_user_id
    from auth.users
   where lower(email) = lower(new.email)
   limit 1;

  if v_user_id is null then
    -- No hay user matcheable. Igualmente registramos el cambio en outbox
    -- como skipped para auditoría.
    insert into public.email_outbox (template, user_id, to_email, vars, status, last_error)
    values (
      case when new.status = 'revoked' then 'byok_revoked_grace_start' else 'byok_revoked_grace_start' end,
      null,
      new.email,
      jsonb_build_object('email', new.email),
      'skipped',
      'no auth.users row matched whitelist email'
    );
    return new;
  end if;

  if new.status = 'revoked' then
    v_grace_ends := now() + interval '30 days';
    update public.entitlements
       set byok_grace_started_at = now(),
           byok_grace_ends_at = v_grace_ends,
           updated_at = now()
     where user_id = v_user_id
       and tier = 'byok';

    insert into public.email_outbox (template, user_id, to_email, vars)
    values (
      'byok_revoked_grace_start',
      v_user_id,
      new.email,
      jsonb_build_object(
        'email', new.email,
        'grace_ends_at', to_char(v_grace_ends, 'DD/MM/YYYY')
      )
    );
  elsif new.status = 'active' then
    -- Reactivación: limpiar grace si existía.
    update public.entitlements
       set byok_grace_started_at = null,
           byok_grace_ends_at = null,
           updated_at = now()
     where user_id = v_user_id
       and tier = 'byok'
       and byok_grace_ends_at is not null;
  end if;

  return new;
end;
$$;

drop trigger if exists on_byok_whitelist_status_change on public.byok_whitelist;
create trigger on_byok_whitelist_status_change
  after update of status on public.byok_whitelist
  for each row execute function public._on_byok_whitelist_status_change();

-- ------------------------------------------------------------
-- 4. Rewrite de daily_entitlement_sweep
--    - Mantiene las 3 mutaciones originales (EP-10.9)
--    - Suma 6 enqueues de cadence en email_outbox
-- ------------------------------------------------------------
create or replace function public.daily_entitlement_sweep()
returns void
language plpgsql
security definer
set search_path = pg_catalog, public
as $$
declare
  rec record;
begin
  -- (1) Trial T+7 — notificar a mitad de trial. NO muta nada.
  for rec in
    select e.user_id, u.email, e.trial_ends_at
      from public.entitlements e
      join auth.users u on u.id = e.user_id
     where e.tier = 'trial'
       and e.trial_started_at + interval '7 days' < now()
       and e.trial_started_at + interval '7 days' >= now() - interval '1 day'
  loop
    insert into public.email_outbox (template, user_id, to_email, vars)
    values (
      'trial_t7',
      rec.user_id,
      rec.email,
      jsonb_build_object(
        'email', rec.email,
        'trial_ends_at', to_char(rec.trial_ends_at, 'DD/MM/YYYY')
      )
    );
  end loop;

  -- (2) Trial T-1 — recordatorio el día anterior al vencimiento. NO muta.
  for rec in
    select e.user_id, u.email, e.trial_ends_at
      from public.entitlements e
      join auth.users u on u.id = e.user_id
     where e.tier = 'trial'
       and e.trial_ends_at >= now()
       and e.trial_ends_at < now() + interval '24 hours'
  loop
    insert into public.email_outbox (template, user_id, to_email, vars)
    values (
      'trial_t1',
      rec.user_id,
      rec.email,
      jsonb_build_object(
        'email', rec.email,
        'trial_ends_at', to_char(rec.trial_ends_at, 'DD/MM/YYYY')
      )
    );
  end loop;

  -- (3) Trial vencido → expired + email T+15.
  for rec in
    select e.user_id, u.email
      from public.entitlements e
      join auth.users u on u.id = e.user_id
     where e.tier = 'trial'
       and e.trial_ends_at < now()
  loop
    update public.entitlements
       set tier = 'expired',
           updated_at = now()
     where user_id = rec.user_id;
    insert into public.email_outbox (template, user_id, to_email, vars)
    values (
      'trial_t15_expired',
      rec.user_id,
      rec.email,
      jsonb_build_object('email', rec.email)
    );
  end loop;

  -- (4) Trial dormant — 30d post expiración.
  for rec in
    select e.user_id, u.email
      from public.entitlements e
      join auth.users u on u.id = e.user_id
     where e.tier = 'expired'
       and e.trial_ends_at < now() - interval '30 days'
       and e.trial_ends_at >= now() - interval '31 days'
  loop
    insert into public.email_outbox (template, user_id, to_email, vars)
    values (
      'trial_t30_dormant',
      rec.user_id,
      rec.email,
      jsonb_build_object('email', rec.email)
    );
  end loop;

  -- (5) BYOK grace T-7 — aviso 7 días antes del fin del grace.
  for rec in
    select e.user_id, u.email, e.byok_grace_ends_at
      from public.entitlements e
      join auth.users u on u.id = e.user_id
     where e.tier = 'byok'
       and e.byok_grace_ends_at is not null
       and e.byok_grace_ends_at >= now() + interval '6 days'
       and e.byok_grace_ends_at < now() + interval '7 days'
  loop
    insert into public.email_outbox (template, user_id, to_email, vars)
    values (
      'byok_grace_t7',
      rec.user_id,
      rec.email,
      jsonb_build_object(
        'email', rec.email,
        'grace_ends_at', to_char(rec.byok_grace_ends_at, 'DD/MM/YYYY')
      )
    );
  end loop;

  -- (6) BYOK grace vencido → expired + email.
  for rec in
    select e.user_id, u.email
      from public.entitlements e
      join auth.users u on u.id = e.user_id
     where e.tier = 'byok'
       and e.byok_grace_ends_at is not null
       and e.byok_grace_ends_at < now()
  loop
    update public.entitlements
       set tier = 'expired',
           byok_grace_started_at = null,
           byok_grace_ends_at = null,
           updated_at = now()
     where user_id = rec.user_id;
    insert into public.email_outbox (template, user_id, to_email, vars)
    values (
      'byok_grace_expired',
      rec.user_id,
      rec.email,
      jsonb_build_object('email', rec.email)
    );
  end loop;

  -- (7) Reset del soft cap rolling 30d. Sin email.
  update public.entitlements
     set minutes_used_period = 0,
         period_started_at = now(),
         updated_at = now()
   where period_started_at + interval '30 days' < now();
end;
$$;

-- Re-grant tras el OR REPLACE (PG no resetea grants pero defensivo).
revoke all on function public.daily_entitlement_sweep() from public;
revoke all on function public.daily_entitlement_sweep() from anon, authenticated;
grant execute on function public.daily_entitlement_sweep() to service_role;
