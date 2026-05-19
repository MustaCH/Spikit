// Edge Function: entitlement
// GET /functions/v1/entitlement
//   Authorization: Bearer <user-JWT>
//
// Returns the user's entitlement row. If the row does not exist (first call
// after signup), creates it according to the signup logic from ADR-0007 § 2:
//   - email in byok_whitelist with status='active' → tier='byok'
//   - otherwise                                    → tier='trial' (14 days)
//
// Ticket: EP-10.3

import 'jsr:@supabase/functions-js/edge-runtime.d.ts';
import { createClient } from 'jsr:@supabase/supabase-js@2';

const SUPABASE_URL = Deno.env.get('SUPABASE_URL')!;
const ANON_KEY = Deno.env.get('SUPABASE_ANON_KEY')!;
const SERVICE_ROLE_KEY = Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')!;

const CORS_HEADERS = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Methods': 'GET, OPTIONS',
  'Access-Control-Allow-Headers': 'authorization, content-type, apikey',
};

const TRIAL_DURATION_MS = 14 * 24 * 60 * 60 * 1000;

Deno.serve(async (req: Request) => {
  if (req.method === 'OPTIONS') {
    return new Response(null, { status: 204, headers: CORS_HEADERS });
  }
  if (req.method !== 'GET') {
    return jsonResponse({ error: 'method_not_allowed' }, 405);
  }

  const authHeader = req.headers.get('Authorization');
  if (!authHeader?.startsWith('Bearer ')) {
    return jsonResponse({ error: 'missing_authorization' }, 401);
  }
  const jwt = authHeader.slice('Bearer '.length);

  const supabaseAuth = createClient(SUPABASE_URL, ANON_KEY, {
    global: { headers: { Authorization: `Bearer ${jwt}` } },
  });
  const { data: userData, error: userErr } = await supabaseAuth.auth.getUser(jwt);
  if (userErr || !userData.user) {
    return jsonResponse({ error: 'invalid_jwt' }, 401);
  }
  const user = userData.user;

  const supabase = createClient(SUPABASE_URL, SERVICE_ROLE_KEY);

  const { data: existing, error: readErr } = await supabase
    .from('entitlements')
    .select('*')
    .eq('user_id', user.id)
    .maybeSingle();

  if (readErr) {
    console.error('entitlements read failed', { user_id: user.id, error: readErr });
    return jsonResponse({ error: 'db_error' }, 500);
  }
  if (existing) {
    return jsonResponse(existing, 200);
  }

  if (!user.email) {
    return jsonResponse({ error: 'user_has_no_email' }, 500);
  }

  const { data: whitelistMatch, error: wlErr } = await supabase
    .from('byok_whitelist')
    .select('id')
    .ilike('email', user.email)
    .eq('status', 'active')
    .maybeSingle();

  if (wlErr) {
    console.error('whitelist read failed', { user_id: user.id, error: wlErr });
    return jsonResponse({ error: 'db_error' }, 500);
  }

  const now = new Date();
  const trialEnds = new Date(now.getTime() + TRIAL_DURATION_MS);

  const insertPayload = whitelistMatch
    ? { user_id: user.id, tier: 'byok' as const }
    : {
        user_id: user.id,
        tier: 'trial' as const,
        trial_started_at: now.toISOString(),
        trial_ends_at: trialEnds.toISOString(),
      };

  const { data: created, error: insertErr } = await supabase
    .from('entitlements')
    .insert(insertPayload)
    .select('*')
    .single();

  if (insertErr) {
    console.error('entitlements insert failed', { user_id: user.id, error: insertErr });
    return jsonResponse({ error: 'db_error' }, 500);
  }

  return jsonResponse(created, 200);
});

function jsonResponse(body: unknown, status: number): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...CORS_HEADERS },
  });
}
