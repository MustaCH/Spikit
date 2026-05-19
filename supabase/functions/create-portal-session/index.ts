// Edge Function: create-portal-session
// POST /functions/v1/create-portal-session
//   Authorization: Bearer <user-JWT>
//
// Crea una Stripe Customer Portal Session para que el usuario gestione su
// suscripción (cambio de tarjeta, cancelación, ver invoices, switch
// monthly <-> yearly). Devuelve la URL hosted del Portal para que el cliente
// la abra en el browser.
//
// Ticket: EP-10.8  |  Origen: ADR-0007 seccion 4.3

import 'jsr:@supabase/functions-js/edge-runtime.d.ts';
import { createClient } from 'jsr:@supabase/supabase-js@2';
import Stripe from 'npm:stripe@^14';

const SUPABASE_URL = Deno.env.get('SUPABASE_URL')!;
const ANON_KEY = Deno.env.get('SUPABASE_ANON_KEY')!;
const SERVICE_ROLE_KEY = Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')!;
const STRIPE_SECRET_KEY = Deno.env.get('STRIPE_SECRET_KEY');

const RETURN_URL = 'spikit://billing-return';

const CORS_HEADERS = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Methods': 'POST, OPTIONS',
  'Access-Control-Allow-Headers': 'authorization, content-type, apikey',
};

Deno.serve(async (req: Request) => {
  if (req.method === 'OPTIONS') return new Response(null, { status: 204, headers: CORS_HEADERS });
  if (req.method !== 'POST') return jsonResp({ error: 'method_not_allowed' }, 405);

  const authHeader = req.headers.get('Authorization');
  if (!authHeader?.startsWith('Bearer ')) {
    return jsonResp({ error: 'missing_authorization' }, 401);
  }
  const jwt = authHeader.slice('Bearer '.length);

  const sbAuth = createClient(SUPABASE_URL, ANON_KEY, {
    global: { headers: { Authorization: `Bearer ${jwt}` } },
  });
  const { data: userData, error: userErr } = await sbAuth.auth.getUser(jwt);
  if (userErr || !userData.user) {
    return jsonResp({ error: 'invalid_jwt' }, 401);
  }
  const user = userData.user;

  if (!STRIPE_SECRET_KEY) {
    console.error('STRIPE_SECRET_KEY not configured in Vault');
    return jsonResp({ error: 'server_misconfigured' }, 500);
  }

  const sb = createClient(SUPABASE_URL, SERVICE_ROLE_KEY);

  const { data: ent, error: entErr } = await sb
    .from('entitlements')
    .select('user_id, stripe_customer_id')
    .eq('user_id', user.id)
    .maybeSingle();
  if (entErr) {
    console.error('entitlements read failed', { user_id: user.id, error: entErr });
    return jsonResp({ error: 'db_error' }, 500);
  }
  if (!ent) {
    return jsonResp({ error: 'entitlement_not_found', hint: 'call /entitlement first' }, 403);
  }
  if (!ent.stripe_customer_id) {
    return jsonResp({ error: 'no_active_subscription' }, 400);
  }

  const stripe = new Stripe(STRIPE_SECRET_KEY, { apiVersion: '2024-06-20' });

  let session: Stripe.BillingPortal.Session;
  try {
    session = await stripe.billingPortal.sessions.create({
      customer: ent.stripe_customer_id,
      return_url: RETURN_URL,
    });
  } catch (e) {
    console.error('stripe billingPortal.sessions.create failed', e);
    return jsonResp({ error: 'stripe_portal_create_failed' }, 502);
  }

  if (!session.url) {
    console.error('stripe returned portal session without url', { session_id: session.id });
    return jsonResp({ error: 'stripe_portal_missing_url' }, 502);
  }

  return jsonResp({ url: session.url }, 200);
});

function jsonResp(body: unknown, status: number): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...CORS_HEADERS },
  });
}
