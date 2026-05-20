// Edge Function: send-transition-email
// POST /functions/v1/send-transition-email
//   Authorization: Bearer <SUPABASE_SERVICE_ROLE_KEY | INTERNAL_DISPATCH_KEY>
//   Content-Type: application/json
//   Body: { "template": "<templateId>", "user_id"?: "<uuid>", "to_email"?: "<email>", "vars"?: {...} }
//
// Endpoint interno (no expuesto al cliente desktop). Acepta dos secrets:
//  - SUPABASE_SERVICE_ROLE_KEY: callers same-runtime (entitlement v6 welcome,
//    stripe-webhook payment_failed/canceled). Ya tienen acceso al service_role
//    via Deno.env, lo reusan directo.
//  - INTERNAL_DISPATCH_KEY: callers externos via HTTP, p.ej. el worker pg_net
//    (_dispatch_outbox_batch). El service_role NO debe exponerse a Postgres /
//    Vault — el INTERNAL_DISPATCH_KEY es un random base64 dedicado para ese
//    caller. Sincronizado contra vault.email_dispatch_token.
//
// Resolucion del destinatario:
//  - Templates con recipient='to_internal' van siempre a hello@spikit.dev
//    (toEmail/user_id se ignora).
//  - Templates con recipient='to_user' resuelven el email del user:
//     a) si se paso to_email en el body, se usa directo (mas eficiente).
//     b) si no, se busca via SUPABASE admin API a partir de user_id.
//     c) si ninguno disponible -> 400.
//
// Ticket: EP-10.10  |  Origen: ADR-0007 secciones 7 + 9

import 'jsr:@supabase/functions-js/edge-runtime.d.ts';
import { createClient } from 'jsr:@supabase/supabase-js@2';

import { sendTemplateEmail } from '../_shared/email/client.ts';
import { TEMPLATES, type EmailTemplateId } from '../_shared/email/templates.ts';

const SUPABASE_URL = Deno.env.get('SUPABASE_URL')!;
const SERVICE_ROLE_KEY = Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')!;
const INTERNAL_DISPATCH_KEY = Deno.env.get('INTERNAL_DISPATCH_KEY');

const CORS_HEADERS = {
  'Access-Control-Allow-Origin': '*',
  'Access-Control-Allow-Methods': 'POST, OPTIONS',
  'Access-Control-Allow-Headers': 'authorization, content-type, apikey',
};

Deno.serve(async (req: Request) => {
  if (req.method === 'OPTIONS') return new Response(null, { status: 204, headers: CORS_HEADERS });
  if (req.method !== 'POST') return jsonResp({ error: 'method_not_allowed' }, 405);

  // Internal-only auth: accept either SERVICE_ROLE_KEY (same-runtime) or
  // INTERNAL_DISPATCH_KEY (external callers like the pg_net worker).
  const authHeader = req.headers.get('Authorization');
  const matchesServiceRole = authHeader === `Bearer ${SERVICE_ROLE_KEY}`;
  const matchesInternalDispatch =
    !!INTERNAL_DISPATCH_KEY && authHeader === `Bearer ${INTERNAL_DISPATCH_KEY}`;
  if (!authHeader || (!matchesServiceRole && !matchesInternalDispatch)) {
    return jsonResp({ error: 'unauthorized' }, 401);
  }

  let body: { template?: string; user_id?: string; to_email?: string; vars?: Record<string, unknown> };
  try {
    body = await req.json();
  } catch {
    return jsonResp({ error: 'invalid_json' }, 400);
  }

  const templateId = body.template as EmailTemplateId | undefined;
  if (!templateId || !(templateId in TEMPLATES)) {
    return jsonResp(
      { error: 'invalid_template', allowed: Object.keys(TEMPLATES) },
      400
    );
  }

  const tpl = TEMPLATES[templateId];
  const vars = body.vars ?? {};

  // Resolve destination email.
  let toEmail: string | null = null;
  if (tpl.recipient === 'to_user') {
    if (body.to_email) {
      toEmail = body.to_email;
    } else if (body.user_id) {
      const sb = createClient(SUPABASE_URL, SERVICE_ROLE_KEY);
      const { data, error } = await sb.auth.admin.getUserById(body.user_id);
      if (error || !data.user) {
        return jsonResp({ error: 'user_not_found', user_id: body.user_id }, 404);
      }
      if (!data.user.email) {
        return jsonResp({ error: 'user_has_no_email', user_id: body.user_id }, 400);
      }
      toEmail = data.user.email;
    } else {
      return jsonResp(
        { error: 'missing_recipient', hint: 'pass either to_email or user_id for to_user templates' },
        400
      );
    }
  }

  const result = await sendTemplateEmail(templateId, toEmail, vars);
  if (!result.ok) {
    console.error('resend send failed', { templateId, toEmail, error: result.error, status: result.status });
    return jsonResp({ ok: false, error: result.error, status: result.status }, 502);
  }

  console.log('email sent', { templateId, to: toEmail, resend_id: result.id });
  return jsonResp({ ok: true, id: result.id, template: templateId }, 200);
});

function jsonResp(body: unknown, status: number): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...CORS_HEADERS },
  });
}
