-- ============================================================
-- Fix: net._http_response.content is text, not bytea
-- ============================================================
-- La migration anterior (20260519160224_email_dispatch_worker.sql) tenia
-- un convert_from(content, 'UTF8') que fallaba porque content ya es text.
-- Aplicado via MCP apply_migration; este archivo es la copia local
-- archivada para que las migrations queden trackables en orden.
--
-- Ticket: EP-10.10 follow-up devops
-- ============================================================

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
    select status_code, content
      into v_status_code, v_response_body
      from net._http_response
     where id = rec.pg_net_request_id;

    if v_status_code is null then
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
