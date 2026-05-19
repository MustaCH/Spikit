-- ============================================================
-- Drop welcome trigger on auth.users
-- ============================================================
-- El welcome se envia ahora directamente desde el Edge Function
-- entitlement al crear la fila de entitlements por primera vez
-- (ADR-0007 § 7 explicitamente permite "trigger on auth.users insert
-- (Postgres trigger) o desde el endpoint entitlement al primer GET").
--
-- Optar por entitlement como fuente unica simplifica el flow:
--  - 1 sola fuente de envio → cero duplicacion
--  - el smoke E2E funciona sin worker pg_net (entitlement corre same-runtime)
--  - los OTROS templates (trial cadence, BYOK, payment) siguen encolandose
--    en email_outbox via el cron y el trigger byok_whitelist, esperando al
--    worker que /devops va a sumar.
--
-- Ticket: EP-10.10
-- ============================================================

drop trigger if exists enqueue_welcome_email on auth.users;
drop function if exists public._enqueue_welcome_email();
