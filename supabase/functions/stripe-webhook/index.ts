// Edge Function: stripe-webhook
// POST /functions/v1/stripe-webhook
//   stripe-signature: <Stripe-generated signature>
//   Body: raw JSON event (NOT json-parsed before signature verify)
//
// Recibe eventos de Stripe, valida firma con STRIPE_WEBHOOK_SECRET, y muta
// public.entitlements segun el tipo de evento. Idempotente por diseno:
// todos los handlers son UPDATE...WHERE, nunca INSERT. Stripe puede reenviar
// el mismo evento N veces y el resultado es identico.
//
// Endpoint NO requiere JWT (deploy con verify_jwt=false). La autenticidad
// la valida la firma criptografica de Stripe.
//
// Ticket: EP-10.7  |  Origen: ADR-0007 seccion 4.4

import 'jsr:@supabase/functions-js/edge-runtime.d.ts';
import { createClient } from 'jsr:@supabase/supabase-js@2';
import Stripe from 'npm:stripe@^14';

import { sendTemplateEmail } from '../_shared/email/client.ts';

const SUPABASE_URL = Deno.env.get('SUPABASE_URL')!;
const SERVICE_ROLE_KEY = Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')!;
const STRIPE_SECRET_KEY = Deno.env.get('STRIPE_SECRET_KEY');
const STRIPE_WEBHOOK_SECRET = Deno.env.get('STRIPE_WEBHOOK_SECRET');

Deno.serve(async (req: Request) => {
  if (req.method !== 'POST') {
    return new Response('method not allowed', { status: 405 });
  }

  if (!STRIPE_SECRET_KEY || !STRIPE_WEBHOOK_SECRET) {
    console.error('stripe secrets not configured', {
      has_secret_key: !!STRIPE_SECRET_KEY,
      has_webhook_secret: !!STRIPE_WEBHOOK_SECRET,
    });
    return new Response('server misconfigured', { status: 500 });
  }

  const sig = req.headers.get('stripe-signature');
  if (!sig) {
    console.warn('missing stripe-signature header');
    return new Response('missing signature', { status: 400 });
  }

  // Read raw body BEFORE any parsing. Signature is computed on raw bytes.
  const rawBody = await req.text();

  const stripe = new Stripe(STRIPE_SECRET_KEY, { apiVersion: '2024-06-20' });

  let event: Stripe.Event;
  try {
    // constructEventAsync uses crypto.subtle, required on Deno runtime
    event = await stripe.webhooks.constructEventAsync(
      rawBody,
      sig,
      STRIPE_WEBHOOK_SECRET
    );
  } catch (e) {
    console.error('signature verification failed', String(e));
    return new Response('invalid signature', { status: 400 });
  }

  const sb = createClient(SUPABASE_URL, SERVICE_ROLE_KEY);

  try {
    switch (event.type) {
      case 'customer.subscription.created': {
        const sub = event.data.object as Stripe.Subscription;
        const customerId = typeof sub.customer === 'string' ? sub.customer : sub.customer.id;
        const periodEnd = extractCurrentPeriodEnd(sub);
        const { error } = await sb
          .from('entitlements')
          .update({
            tier: 'pro',
            stripe_subscription_id: sub.id,
            pro_renews_at: periodEnd ? new Date(periodEnd * 1000).toISOString() : null,
            byok_grace_started_at: null,
            byok_grace_ends_at: null,
            updated_at: new Date().toISOString(),
          })
          .eq('stripe_customer_id', customerId);
        if (error) {
          console.error('subscription.created UPDATE failed', { sub_id: sub.id, customer: customerId, error });
          return new Response('db error', { status: 500 });
        }
        console.log('subscription.created applied', { sub_id: sub.id, customer: customerId, period_end: periodEnd });
        break;
      }

      case 'customer.subscription.updated': {
        const sub = event.data.object as Stripe.Subscription;
        const periodEnd = extractCurrentPeriodEnd(sub);
        const { error } = await sb
          .from('entitlements')
          .update({
            pro_renews_at: periodEnd ? new Date(periodEnd * 1000).toISOString() : null,
            updated_at: new Date().toISOString(),
          })
          .eq('stripe_subscription_id', sub.id);
        if (error) {
          console.error('subscription.updated UPDATE failed', { sub_id: sub.id, error });
          return new Response('db error', { status: 500 });
        }
        if (sub.cancel_at_period_end) {
          console.log('subscription scheduled to cancel at period end', { sub_id: sub.id });
        }
        if (sub.status !== 'active' && sub.status !== 'trialing') {
          console.log('subscription status non-active; tier not modified until deletion', {
            sub_id: sub.id,
            status: sub.status,
          });
        }
        break;
      }

      case 'customer.subscription.deleted': {
        const sub = event.data.object as Stripe.Subscription;
        const customerId = typeof sub.customer === 'string' ? sub.customer : sub.customer.id;
        const { data: updated, error } = await sb
          .from('entitlements')
          .update({
            tier: 'expired',
            stripe_subscription_id: null,
            pro_renews_at: null,
            updated_at: new Date().toISOString(),
          })
          .eq('stripe_subscription_id', sub.id)
          .select('user_id');
        if (error) {
          console.error('subscription.deleted UPDATE failed', { sub_id: sub.id, error });
          return new Response('db error', { status: 500 });
        }
        console.log('subscription.deleted applied', { sub_id: sub.id });
        await dispatchSubscriptionCanceledEmail(sb, customerId, updated?.[0]?.user_id);
        break;
      }

      case 'invoice.payment_failed': {
        const invoice = event.data.object as Stripe.Invoice;
        const customerId = typeof invoice.customer === 'string' ? invoice.customer : invoice.customer?.id;
        console.warn('invoice payment failed', {
          customer: customerId,
          invoice: invoice.id,
          amount_due: invoice.amount_due,
          attempt_count: invoice.attempt_count,
        });
        await dispatchPaymentFailedEmail(sb, customerId, invoice);
        break;
      }

      default:
        console.log('unhandled event type, ignoring', { type: event.type, id: event.id });
    }
  } catch (e) {
    console.error('handler threw', { type: event.type, error: String(e) });
    // Return 500 so Stripe retries. All handlers are idempotent (UPDATE...WHERE).
    return new Response('handler error', { status: 500 });
  }

  return new Response('ok', { status: 200 });
});

// Stripe API >= 2025-x moved current_period_end from the Subscription root
// to each Subscription Item. We try the items path first, then fall back to
// the legacy root field for compatibility with older API versions.
function extractCurrentPeriodEnd(sub: Stripe.Subscription): number | null {
  const itemEnd = (sub as unknown as {
    items?: { data?: Array<{ current_period_end?: number }> };
  }).items?.data?.[0]?.current_period_end;
  if (typeof itemEnd === 'number') return itemEnd;
  const rootEnd = (sub as unknown as { current_period_end?: number }).current_period_end;
  if (typeof rootEnd === 'number') return rootEnd;
  console.warn('subscription has no current_period_end in items or root', { sub_id: sub.id });
  return null;
}

// Email dispatchers — both swallow errors. Stripe doesn't need to retry the
// whole webhook because the email failed; the DB mutation already happened.
async function dispatchPaymentFailedEmail(
  sb: ReturnType<typeof createClient>,
  customerId: string | undefined,
  invoice: Stripe.Invoice
): Promise<void> {
  if (!customerId) return;
  try {
    const userEmail = await resolveEmailByStripeCustomer(sb, customerId);
    if (!userEmail) {
      console.warn('payment_failed: no user email for stripe_customer_id', { customerId });
      return;
    }
    const amount = invoice.amount_due ?? 0;
    const currency = (invoice.currency ?? 'usd').toUpperCase();
    const amountFormatted = `${(amount / 100).toFixed(2)} ${currency}`;
    const result = await sendTemplateEmail('stripe_payment_failed', userEmail, {
      email: userEmail,
      attempt_count: invoice.attempt_count ?? 1,
      amount_due_formatted: amountFormatted,
    });
    if (!result.ok) {
      console.error('payment_failed email send failed', { customerId, error: result.error });
    }
  } catch (e) {
    console.error('payment_failed dispatcher threw', String(e));
  }
}

async function dispatchSubscriptionCanceledEmail(
  sb: ReturnType<typeof createClient>,
  customerId: string | undefined,
  userIdHint: string | undefined
): Promise<void> {
  if (!customerId && !userIdHint) return;
  try {
    let userEmail: string | null = null;
    if (customerId) {
      userEmail = await resolveEmailByStripeCustomer(sb, customerId);
    }
    if (!userEmail && userIdHint) {
      const { data } = await sb.auth.admin.getUserById(userIdHint);
      userEmail = data?.user?.email ?? null;
    }
    if (!userEmail) {
      console.warn('subscription.deleted: no user email resolvable', { customerId, userIdHint });
      return;
    }
    const result = await sendTemplateEmail('stripe_subscription_canceled', userEmail, {
      email: userEmail,
    });
    if (!result.ok) {
      console.error('subscription_canceled email send failed', { customerId, error: result.error });
    }
  } catch (e) {
    console.error('subscription_canceled dispatcher threw', String(e));
  }
}

async function resolveEmailByStripeCustomer(
  sb: ReturnType<typeof createClient>,
  customerId: string
): Promise<string | null> {
  const { data, error } = await sb
    .from('entitlements')
    .select('user_id')
    .eq('stripe_customer_id', customerId)
    .maybeSingle();
  if (error || !data?.user_id) return null;
  const { data: u } = await sb.auth.admin.getUserById(data.user_id);
  return u?.user?.email ?? null;
}
