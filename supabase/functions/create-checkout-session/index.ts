// Edge Function: create-checkout-session
// POST /functions/v1/create-checkout-session
//   Authorization: Bearer <user-JWT>
//   Content-Type: application/json
//   Body: { "lookup_key": "pro_monthly" | "pro_yearly" }
//
// Crea (o reusa) un Stripe Customer asociado al user.id, abre una Checkout
// Session de subscription, y devuelve la URL hosted para que el cliente abra
// en el browser. El upgrade efectivo de tier lo hace el webhook (EP-10.7).
//
// Ticket: EP-10.6  |  Origen: ADR-0007 seccion 4.2

import 'jsr:@supabase/functions-js/edge-runtime.d.ts';
import { createClient } from 'jsr:@supabase/supabase-js@2';
import Stripe from 'npm:stripe@^14';

const SUPABASE_URL = Deno.env.get('SUPABASE_URL')!;
const ANON_KEY = Deno.env.get('SUPABASE_ANON_KEY')!;
const SERVICE_ROLE_KEY = Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')!;
const STRIPE_SECRET_KEY = Deno.env.get('STRIPE_SECRET_KEY');

const SUCCESS_URL = 'spikit://billing-return?status=success';
const CANCEL_URL = 'spikit://billing-return?status=cancel';
const ALLOWED_LOOKUP_KEYS = ['pro_monthly', 'pro_yearly'] as const;
type LookupKey = (typeof ALLOWED_LOOKUP_KEYS)[number];

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
  if (!user.email) {
    return jsonResp({ error: 'user_has_no_email' }, 500);
  }

  let body: { lookup_key?: string };
  try {
    body = await req.json();
  } catch {
    return jsonResp({ error: 'invalid_json' }, 400);
  }
  const lookupKey = body.lookup_key;
  if (!lookupKey || !ALLOWED_LOOKUP_KEYS.includes(lookupKey as LookupKey)) {
    return jsonResp(
      { error: 'invalid_lookup_key', allowed: ALLOWED_LOOKUP_KEYS },
      400
    );
  }

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

  const stripe = new Stripe(STRIPE_SECRET_KEY, { apiVersion: '2024-06-20' });

  let customerId = ent.stripe_customer_id;
  if (!customerId) {
    try {
      const customer = await stripe.customers.create({
        email: user.email,
        metadata: { user_id: user.id },
      });
      customerId = customer.id;
    } catch (e) {
      console.error('stripe customer create failed', e);
      return jsonResp({ error: 'stripe_customer_create_failed' }, 502);
    }

    const { error: updErr } = await sb
      .from('entitlements')
      .update({ stripe_customer_id: customerId, updated_at: new Date().toISOString() })
      .eq('user_id', user.id);
    if (updErr) {
      console.error('entitlements update stripe_customer_id failed', {
        user_id: user.id,
        customer_id: customerId,
        error: updErr,
      });
      // Don't fail the request — the customer was created in Stripe. Next call
      // will see it under stripe.customers.list({email}) if we needed to recover,
      // but we'd rather not orphan it. For V1 we accept the risk.
    }
  }

  let priceId: string;
  try {
    const prices = await stripe.prices.list({
      lookup_keys: [lookupKey],
      active: true,
      limit: 1,
    });
    if (prices.data.length === 0) {
      console.error('no active price for lookup_key', { lookupKey });
      return jsonResp(
        { error: 'price_not_found', lookup_key: lookupKey },
        500
      );
    }
    priceId = prices.data[0].id;
  } catch (e) {
    console.error('stripe prices.list failed', e);
    return jsonResp({ error: 'stripe_price_lookup_failed' }, 502);
  }

  let session: Stripe.Checkout.Session;
  try {
    session = await stripe.checkout.sessions.create({
      mode: 'subscription',
      customer: customerId,
      line_items: [{ price: priceId, quantity: 1 }],
      success_url: SUCCESS_URL,
      cancel_url: CANCEL_URL,
      // Defensive: also store user_id on the subscription metadata so the
      // webhook can fall back to it if stripe_customer_id ever desyncs from
      // the entitlements row.
      subscription_data: {
        metadata: { user_id: user.id },
      },
    });
  } catch (e) {
    console.error('stripe checkout.sessions.create failed', e);
    return jsonResp({ error: 'stripe_session_create_failed' }, 502);
  }

  if (!session.url) {
    console.error('stripe returned session without url', { session_id: session.id });
    return jsonResp({ error: 'stripe_session_missing_url' }, 502);
  }

  return jsonResp({ url: session.url }, 200);
});

function jsonResp(body: unknown, status: number): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...CORS_HEADERS },
  });
}
