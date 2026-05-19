// Templates de email transaccional para Spikit.
// Los textos son placeholders intencionales — /email-marketing los va a reescribir
// con copy final. Lo que congelamos acá es: nombres de template (templateId),
// subjects, variables esperadas y el shape del HTML.
//
// Cuando /email-marketing cierre el copy, reemplazar el contenido de subject y
// renderHtml por template. NO renombrar templateIds — están referenciados desde
// triggers PG y desde código de Edge Functions.

export type EmailTemplateId =
  | 'welcome'
  | 'trial_t7'
  | 'trial_t1'
  | 'trial_t15_expired'
  | 'trial_t30_dormant'
  | 'byok_revoked_grace_start'
  | 'byok_grace_t7'
  | 'byok_grace_expired'
  | 'stripe_payment_failed'
  | 'stripe_subscription_canceled'
  | 'soft_cap_alert_internal';

export interface EmailTemplateDef {
  subject: (vars: Record<string, unknown>) => string;
  renderHtml: (vars: Record<string, unknown>) => string;
  // Variables esperadas (documentación — no se valida en runtime estrictamente).
  expectedVars: readonly string[];
  // 'to_user' = se envia al user; 'to_internal' = se envia a nuestro inbox (hello@spikit.dev).
  recipient: 'to_user' | 'to_internal';
}

const FROM_ADDRESS = 'Nacho de Spikit <hello@spikit.dev>';
const INTERNAL_ALERTS_TO = 'hello@spikit.dev';

function basicLayout(title: string, bodyHtml: string, footerExtra = ''): string {
  return `<!doctype html>
<html lang="es">
<head><meta charset="utf-8"><title>${escapeHtml(title)}</title></head>
<body style="font-family: -apple-system, system-ui, sans-serif; max-width: 560px; margin: 0 auto; padding: 32px 16px; color: #1a1a1a;">
  <h1 style="font-size: 20px; margin: 0 0 16px;">${escapeHtml(title)}</h1>
  ${bodyHtml}
  <hr style="border: none; border-top: 1px solid #eee; margin: 32px 0 16px;">
  <p style="font-size: 12px; color: #888; margin: 0;">
    Spikit · dictado por voz para Windows<br>
    ${footerExtra ? footerExtra + '<br>' : ''}
    Si no esperabas este mail, podés ignorarlo.
  </p>
</body>
</html>`;
}

function escapeHtml(s: unknown): string {
  return String(s ?? '')
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

export const TEMPLATES: Record<EmailTemplateId, EmailTemplateDef> = {
  welcome: {
    recipient: 'to_user',
    expectedVars: ['email'],
    subject: () => "You're in",
    renderHtml: (v) =>
      basicLayout(
        'Bienvenido a Spikit',
        `<p>Hola,</p>
         <p>Tu cuenta <b>${escapeHtml(v.email)}</b> ya está lista.</p>
         <p>Abrí Spikit, configurá tu hotkey y empezá a dictar. Tenés 14 días de trial.</p>`
      ),
  },

  trial_t7: {
    recipient: 'to_user',
    expectedVars: ['email', 'trial_ends_at'],
    subject: () => 'Cómo va el primer halftime',
    renderHtml: (v) =>
      basicLayout(
        'Llevás 7 días con Spikit',
        `<p>Estás a mitad del trial. Te queda hasta el <b>${escapeHtml(v.trial_ends_at)}</b>.</p>
         <p>Si te está sirviendo, dejame saber qué estás dictando — me ayuda a mejorar el producto.</p>`
      ),
  },

  trial_t1: {
    recipient: 'to_user',
    expectedVars: ['email', 'trial_ends_at'],
    subject: () => 'Te queda 1 día',
    renderHtml: (v) =>
      basicLayout(
        'Tu trial vence mañana',
        `<p>Mañana (<b>${escapeHtml(v.trial_ends_at)}</b>) termina tu trial de 14 días.</p>
         <p>Si querés seguir, podés pasar a Pro desde Settings → Plan en la app.</p>`
      ),
  },

  trial_t15_expired: {
    recipient: 'to_user',
    expectedVars: ['email'],
    subject: () => 'Tu trial terminó',
    renderHtml: () =>
      basicLayout(
        'Tu trial terminó',
        `<p>Pasaron 14 días desde que arrancaste con Spikit.</p>
         <p>La app sigue instalada pero ya no podés dictar contra el proxy gestionado.</p>
         <p>Para reactivar: Settings → Plan → Pasar a Pro.</p>`
      ),
  },

  trial_t30_dormant: {
    recipient: 'to_user',
    expectedVars: ['email'],
    subject: () => 'Te dejamos los settings guardados',
    renderHtml: () =>
      basicLayout(
        'Tu cuenta sigue ahí',
        `<p>Pasaron 30 días desde tu trial. Si volvés a Spikit, tus settings (hotkey, idioma, etc.) están como los dejaste.</p>
         <p>Cuando quieras, podés pasar a Pro y arrancar de nuevo sin reconfigurar nada.</p>`
      ),
  },

  byok_revoked_grace_start: {
    recipient: 'to_user',
    expectedVars: ['email', 'grace_ends_at'],
    subject: () => 'Cambio en tu acceso BYOK',
    renderHtml: (v) =>
      basicLayout(
        'Tu acceso BYOK fue revocado',
        `<p>A partir de hoy entrás en un período de gracia de 30 días.</p>
         <p>Tu acceso vence el <b>${escapeHtml(v.grace_ends_at)}</b>. Si querés seguir usando Spikit después de esa fecha, podés pasar a Pro desde la app.</p>`
      ),
  },

  byok_grace_t7: {
    recipient: 'to_user',
    expectedVars: ['email', 'grace_ends_at'],
    subject: () => 'Te quedan 7 días de tu acceso BYOK',
    renderHtml: (v) =>
      basicLayout(
        'Tu acceso BYOK termina en 7 días',
        `<p>El <b>${escapeHtml(v.grace_ends_at)}</b> tu acceso BYOK queda inactivo.</p>
         <p>Si querés evitar la interrupción, podés pasar a Pro desde Settings → Plan.</p>`
      ),
  },

  byok_grace_expired: {
    recipient: 'to_user',
    expectedVars: ['email'],
    subject: () => 'Tu acceso BYOK terminó',
    renderHtml: () =>
      basicLayout(
        'Tu acceso BYOK terminó',
        `<p>Tu período de gracia de 30 días terminó. La app ya no podrá dictar hasta que pases a Pro.</p>
         <p>Settings → Plan → Pasar a Pro.</p>`
      ),
  },

  stripe_payment_failed: {
    recipient: 'to_user',
    expectedVars: ['email', 'attempt_count', 'amount_due_formatted'],
    subject: () => 'Tu cobro de Spikit falló',
    renderHtml: (v) =>
      basicLayout(
        'No pudimos cobrar tu suscripción',
        `<p>Intentamos cobrar <b>${escapeHtml(v.amount_due_formatted)}</b> y no funcionó (intento ${escapeHtml(v.attempt_count)}).</p>
         <p>Stripe va a reintentar automáticamente en las próximas horas. Si querés evitarlo, actualizá tu tarjeta desde Settings → Plan → Gestionar suscripción.</p>`
      ),
  },

  stripe_subscription_canceled: {
    recipient: 'to_user',
    expectedVars: ['email'],
    subject: () => 'Tu suscripción terminó',
    renderHtml: () =>
      basicLayout(
        'Tu suscripción Pro terminó',
        `<p>Tu suscripción a Spikit Pro fue cancelada.</p>
         <p>La app sigue instalada y tu cuenta intacta — podés reactivar Pro cuando quieras desde Settings → Plan.</p>`
      ),
  },

  soft_cap_alert_internal: {
    recipient: 'to_internal',
    expectedVars: ['user_id', 'user_email', 'minutes_used_period'],
    subject: (v) => `Usage alert: ${v.user_email ?? v.user_id}`,
    renderHtml: (v) =>
      basicLayout(
        'Soft cap excedido',
        `<p><b>User:</b> ${escapeHtml(v.user_email)}</p>
         <p><b>User ID:</b> ${escapeHtml(v.user_id)}</p>
         <p><b>Minutos usados (período rolling 30d):</b> ${escapeHtml(v.minutes_used_period)}</p>
         <p>El user excedió el cap interno de 500 min/30d. Revisar manualmente: legítimo (power user) o bot.</p>`
      ),
  },
};

export function getFromAddress(): string {
  return FROM_ADDRESS;
}

export function getInternalAlertsAddress(): string {
  return INTERNAL_ALERTS_TO;
}

export function listTemplateIds(): EmailTemplateId[] {
  return Object.keys(TEMPLATES) as EmailTemplateId[];
}
