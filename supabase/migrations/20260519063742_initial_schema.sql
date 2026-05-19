-- ============================================================
-- Initial schema — entitlements + byok_whitelist + audit
-- Source: ADR-0007 § 1 "Schema de datos"
-- Ticket:  EP-10.2
-- ============================================================

-- ============================================================
-- entitlements
-- ============================================================
create type public.tier as enum ('trial', 'pro', 'byok', 'expired');

create table public.entitlements (
  user_id                uuid primary key references auth.users(id) on delete cascade,
  tier                   public.tier not null,
  trial_started_at       timestamptz,
  trial_ends_at          timestamptz,
  stripe_customer_id     text unique,
  stripe_subscription_id text unique,
  pro_renews_at          timestamptz,
  byok_grace_started_at  timestamptz,
  byok_grace_ends_at     timestamptz,
  minutes_used_period    integer not null default 0,
  period_started_at      timestamptz not null default now(),
  updated_at             timestamptz not null default now()
);

create index entitlements_trial_ends_at_idx
  on public.entitlements (trial_ends_at)
  where tier = 'trial';

create index entitlements_byok_grace_ends_at_idx
  on public.entitlements (byok_grace_ends_at)
  where tier = 'byok' and byok_grace_ends_at is not null;

-- RLS: el usuario lee solo su propia fila. INSERT/UPDATE solo vía service-role
-- key desde Edge Functions (que bypasea RLS).
alter table public.entitlements enable row level security;

create policy entitlements_select_own on public.entitlements
  for select using (auth.uid() = user_id);

-- ============================================================
-- byok_whitelist
-- ============================================================
create type public.whitelist_status as enum ('active', 'revoked');

create table public.byok_whitelist (
  id          uuid primary key default gen_random_uuid(),
  email       text not null unique,
  status      public.whitelist_status not null default 'active',
  invited_at  timestamptz not null default now(),
  revoked_at  timestamptz,
  notes       text,
  created_by  text not null,
  updated_at  timestamptz not null default now()
);

create index byok_whitelist_email_idx on public.byok_whitelist (lower(email));

-- RLS: cero acceso desde clientes con anon/auth role. Solo service-role
-- (Edge Functions + Nacho editando desde Supabase Studio).
alter table public.byok_whitelist enable row level security;
-- (sin policies → nadie con auth normal puede ni siquiera ver la tabla)

-- ============================================================
-- byok_whitelist_audit
-- ============================================================
create table public.byok_whitelist_audit (
  id          bigserial primary key,
  changed_at  timestamptz not null default now(),
  changed_by  text not null,            -- email o 'system' para acciones del cron
  action      text not null,            -- 'invite' | 'revoke' | 'reactivate' | 'note_update'
  email       text not null,
  old_status  public.whitelist_status,
  new_status  public.whitelist_status,
  notes       text
);

alter table public.byok_whitelist_audit enable row level security;
-- (sin policies → audit-only, accesible solo vía service-role)

-- ============================================================
-- Audit trigger: registra INSERT y status UPDATE en byok_whitelist
-- ============================================================
create or replace function public.tg_byok_whitelist_audit()
returns trigger language plpgsql as $$
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
                case when new.status = 'revoked' then 'revoke' else 'reactivate' end,
                new.email, old.status, new.status, new.notes);
    end if;
  end if;
  return new;
end;
$$;

create trigger byok_whitelist_audit_trigger
  after insert or update on public.byok_whitelist
  for each row execute function public.tg_byok_whitelist_audit();
