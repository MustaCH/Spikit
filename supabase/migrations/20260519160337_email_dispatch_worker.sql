-- ============================================================
-- Email dispatch worker (EP-10.10 follow-up, owner /devops)
-- ============================================================
-- Procesa la cola public.email_outbox encolada por:
--   - daily_entitlement_sweep (6 cadence emails)
--   - trigger byok_whitelist UPDATE (byok_revoked_grace_start)
-- y dispara los emails via Edge Function send-transition-email.
--
-- Pieza 1: extension pg_net habilitada (extension de Supabase para HTTP async
--          desde SQL/cron).
-- Pieza 2: secret vault.email_dispatch_token con un random base64 generado
--          server-side. NUNCA aparece en migration files ni en logs. Nacho
--          tiene que copiarlo de vault.decrypted_secrets a Edge Function
--          Secrets (env var INTERNAL_DISPATCH_KEY) - ver runbook en
--          docs/infra.md § Email worker.
-- Pieza 3: columnas dispatched_at + pg_net_request_id en email_outbox para
--          que reconcile pueda matchear la response async de pg_net contra
--          la fila que la disparo.
-- Pieza 4: _dispatch_outbox_batch(p_limit) - lee N rows pending con FOR
--          UPDATE SKIP LOCKED, dispara HTTP POST con net.http_post, marca
--          como 'sending' + attempts++ + dispatched_at=now() + request_id.
-- Pieza 5: _reconcile_outbox_results() - lee net._http_response WHERE id
--          matchea fila 'sending', actualiza status segun HTTP code:
--           2xx           -> 'sent' + sent_at=now()
--           otro y attempts>=3 -> 'failed' + last_error
--           otro y attempts<3  -> 'pending' (retry) + last_error
-- Pieza 6: 2 cron jobs cada minuto (dispatch + reconcile separados para
--          que un fallo de pg_net en un job no bloquee el otro).
--
-- Idempotente: re-aplicar la migration NO regenera el Vault secret y NO
-- duplica los cron jobs.
-- ============================================================

create extension if not exists pg_net with schema extensions;

-- Vault: generar secret server-side si no existe. El valor random nunca sale
-- a un output; Nacho lo lee desde vault.decrypted_secrets en su sesion para
-- copiarlo a Edge Function Secrets.
do $$
begin
  if not exists (select 1 from vault.secrets where name = 'email_dispatch_token') then
    perform vault.create_secret(
      encode(extensions.gen_random_bytes(32), 'base64'),
      'email_dispatch_token',
      'Shared secret matching INTERNAL_DISPATCH_KEY env var in Edge Function send-transition-email. Used by email worker (_dispatch_outbox_batch) to authenticate.'
    );
  end if;
end;
$$;

-- Columnas de tracking del worker
alter table public.email_outbox
  add column if not exists dispatched_at timestamptz,
  add column if not exists pg_net_request_id bigint;

create index if not exists email_outbox_sending_idx
  on public.email_outbox (pg_net_request_id)
  where status = 'sending';

-- ------------------------------------------------------------
-- Dispatcher
-- ------------------------------------------------------------
create or replace function public._dispatch_outbox_batch(p_limit int default 50)
returns int
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_count int := 0;
  v_token text;
  v_url text := 'https://okomqtltwshgwruwulhv.supabase.co/functions/v1/send-transition-email';
  rec record;
  v_request_id bigint;
begin
  select decrypted_secret into v_token
    from vault.decrypted_secrets
   where name = 'email_dispatch_token'
   limit 1;

  if v_token is null or length(v_token) < 16 then
    raise warning 'email_dispatch_token not configured in Vault - worker idle';
    return 0;
  end if;

  for rec in
    select id, template, user_id, to_email, vars, attempts
      from public.email_outbox
     where status = 'pending'
       and attempts < 3
     order by created_at
     limit p_limit
     for update skip locked
  loop
    select net.http_post(
      url := v_url,
      headers := jsonb_build_object(
        'Authorization', 'Bearer ' || v_token,
        'Content-Type', 'application/json'
      ),
      body := jsonb_build_object(
        'template', rec.template,
        'user_id', rec.user_id,
        'to_email', rec.to_email,
        'vars', rec.vars
      ),
      timeout_milliseconds := 10000
    ) into v_request_id;

    update public.email_outbox
       set status = 'sending',
           attempts = rec.attempts + 1,
           dispatched_at = now(),
           pg_net_request_id = v_request_id
     where id = rec.id;

    v_count := v_count + 1;
  end loop;

  return v_count;
end;
$fn$;

revoke all on function public._dispatch_outbox_batch(int) from public;
revoke all on function public._dispatch_outbox_batch(int) from anon, authenticated;
grant execute on function public._dispatch_outbox_batch(int) to service_role;

-- ------------------------------------------------------------
-- Reconciler
-- ------------------------------------------------------------
create or replace function public._reconcile_outbox_results()
returns int
language plpgsql
security definer
set search_path = pg_catalog, public
as $fn$
declare
  v_count int := 0;
  rec record;
  v_status_code int;
  v_response_body text;
begin
  for rec in
    select eo.id, eo.attempts, eo.pg_net_request_id
      from public.email_outbox eo
     where eo.status = 'sending'
       and eo.pg_net_request_id is not null
       and eo.dispatched_at < now() - interval '5 seconds'
  loop
    select status_code, convert_from(content, 'UTF8')
      into v_status_code, v_response_body
      from net._http_response
     where id = rec.pg_net_request_id;

    if v_status_code is null then
      -- pg_net response no llego todavia (o expiro). Si paso mucho tiempo lo retornamos a pending.
      if exists (
        select 1 from public.email_outbox
         where id = rec.id and dispatched_at < now() - interval '5 minutes'
      ) then
        update public.email_outbox
           set status = case when attempts >= 3 then 'failed' else 'pending' end,
               last_error = 'pg_net response not received within 5min'
         where id = rec.id;
      end if;
      continue;
    end if;

    if v_status_code between 200 and 299 then
      update public.email_outbox
         set status = 'sent', sent_at = now(), last_error = null
       where id = rec.id;
    elsif rec.attempts >= 3 then
      update public.email_outbox
         set status = 'failed',
             last_error = 'HTTP ' || v_status_code::text || ': ' || coalesce(left(v_response_body, 500), '')
       where id = rec.id;
    else
      update public.email_outbox
         set status = 'pending',
             last_error = 'HTTP ' || v_status_code::text || ': ' || coalesce(left(v_response_body, 500), '')
       where id = rec.id;
    end if;

    v_count := v_count + 1;
  end loop;

  return v_count;
end;
$fn$;

revoke all on function public._reconcile_outbox_results() from public;
revoke all on function public._reconcile_outbox_results() from anon, authenticated;
grant execute on function public._reconcile_outbox_results() to service_role;

-- ------------------------------------------------------------
-- Cron schedules (idempotentes)
-- ------------------------------------------------------------
do $sched$
begin
  if not exists (select 1 from cron.job where jobname = 'email-outbox-dispatch') then
    perform cron.schedule(
      'email-outbox-dispatch',
      '* * * * *',
      $cmd$ select public._dispatch_outbox_batch(50) $cmd$
    );
  end if;
  if not exists (select 1 from cron.job where jobname = 'email-outbox-reconcile') then
    perform cron.schedule(
      'email-outbox-reconcile',
      '* * * * *',
      $cmd$ select public._reconcile_outbox_results() $cmd$
    );
  end if;
end;
$sched$;
