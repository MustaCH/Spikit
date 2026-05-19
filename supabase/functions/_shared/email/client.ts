// Cliente compartido para mandar emails via Resend desde cualquier Edge Function.
// Importar y llamar `sendTemplateEmail(...)` — encapsula:
//  - leer RESEND_API_KEY del env
//  - renderizar el template (subject + html) a partir del templateId + vars
//  - rutear el "to" según recipient ('to_user' usa to_email arg, 'to_internal'
//    ignora to_email y usa hello@spikit.dev)
//  - retornar { ok, id, error } sin tirar excepciones para que el caller
//    pueda decidir si retry/log/etc.

import {
  TEMPLATES,
  type EmailTemplateId,
  getFromAddress,
  getInternalAlertsAddress,
} from './templates.ts';

const RESEND_ENDPOINT = 'https://api.resend.com/emails';

export interface SendEmailResult {
  ok: boolean;
  id?: string;
  status?: number;
  error?: string;
}

export async function sendTemplateEmail(
  templateId: EmailTemplateId,
  toEmail: string | null,
  vars: Record<string, unknown> = {}
): Promise<SendEmailResult> {
  const apiKey = Deno.env.get('RESEND_API_KEY');
  if (!apiKey) {
    return { ok: false, error: 'RESEND_API_KEY not configured' };
  }

  const tpl = TEMPLATES[templateId];
  if (!tpl) {
    return { ok: false, error: `unknown template: ${templateId}` };
  }

  const recipient =
    tpl.recipient === 'to_internal' ? getInternalAlertsAddress() : toEmail;
  if (!recipient) {
    return { ok: false, error: 'no recipient (template expects to_user but toEmail is null)' };
  }

  const payload = {
    from: getFromAddress(),
    to: [recipient],
    subject: tpl.subject(vars),
    html: tpl.renderHtml(vars),
    tags: [
      { name: 'template', value: templateId },
      { name: 'recipient_kind', value: tpl.recipient },
    ],
  };

  let resp: Response;
  try {
    resp = await fetch(RESEND_ENDPOINT, {
      method: 'POST',
      headers: {
        Authorization: `Bearer ${apiKey}`,
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(payload),
    });
  } catch (e) {
    return { ok: false, error: `network: ${String(e)}` };
  }

  let body: unknown;
  try {
    body = await resp.json();
  } catch {
    body = null;
  }

  if (!resp.ok) {
    return {
      ok: false,
      status: resp.status,
      error: typeof body === 'object' && body && 'message' in body
        ? String((body as { message: unknown }).message)
        : `status ${resp.status}`,
    };
  }

  const id =
    typeof body === 'object' && body && 'id' in body
      ? String((body as { id: unknown }).id)
      : undefined;

  return { ok: true, id, status: resp.status };
}
