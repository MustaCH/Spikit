// Edge Function: transcribe
// POST /functions/v1/transcribe
//   Authorization: Bearer <user-JWT>
//   Content-Type: multipart/form-data
//   Body: audio=<WAV PCM 16kHz mono 16-bit>
//
// Proxy de OpenAI Whisper para usuarios trial/pro. BYOK NO usa este endpoint
// (su key esta en el cliente). Tracking de soft cap anti-abuse (500 min / 30d).
//
// Ticket: EP-10.5  |  Origen: ADR-0007 seccion 5

import 'jsr:@supabase/functions-js/edge-runtime.d.ts';
import { createClient } from 'jsr:@supabase/supabase-js@2';

const SUPABASE_URL = Deno.env.get('SUPABASE_URL')!;
const ANON_KEY = Deno.env.get('SUPABASE_ANON_KEY')!;
const SERVICE_ROLE_KEY = Deno.env.get('SUPABASE_SERVICE_ROLE_KEY')!;
const OPENAI_API_KEY = Deno.env.get('OPENAI_API_KEY');
const RESEND_API_KEY = Deno.env.get('RESEND_API_KEY');
const ABUSE_ALERT_TO = Deno.env.get('ABUSE_ALERT_TO') ?? 'hello@spikit.dev';
const ABUSE_ALERT_FROM = Deno.env.get('ABUSE_ALERT_FROM') ?? 'alerts@spikit.dev';

const SOFT_CAP_MIN = 500;
const OPENAI_URL = 'https://api.openai.com/v1/audio/transcriptions';
// Modelo de transcripcion para usuarios Trial/Pro. Cambiado de 'whisper-1' a
// 'gpt-4o-transcribe' (2026-05-20) — mismo endpoint, mismo precio ($0.006/min),
// WER significativamente mejor segun model card de OpenAI (oct-2024) y benchmark
// FLEURS. Mejor handling de acentos, code-switching es-en y vocabulario tecnico,
// que son los casos dominantes en el target de Spikit (devs argentinos).
// Si aparece regresion en produccion, revertir a 'whisper-1' es seguro (BYOK
// sigue exponiendo ambos modelos en el dropdown de Settings → Provider).
// Nombre de la constante queda como WHISPER_MODEL por legacy del codigo;
// renombrar a TRANSCRIPTION_MODEL es mejora cosmetica para sprint normal.
const WHISPER_MODEL = 'gpt-4o-transcribe';

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

  const sb = createClient(SUPABASE_URL, SERVICE_ROLE_KEY);

  const { data: ent, error: entErr } = await sb
    .from('entitlements')
    .select('*')
    .eq('user_id', user.id)
    .maybeSingle();

  if (entErr) {
    console.error('entitlements read failed', { user_id: user.id, error: entErr });
    return jsonResp({ error: 'db_error' }, 500);
  }
  if (!ent) {
    return jsonResp({ error: 'entitlement_not_found', hint: 'call /entitlement first' }, 403);
  }

  if (ent.tier === 'expired') {
    return jsonResp({ error: 'subscription_required' }, 402);
  }
  if (ent.tier === 'byok') {
    console.warn('byok user hit /transcribe', { user_id: user.id });
    return jsonResp({ error: 'wrong_endpoint_for_byok' }, 400);
  }
  // tier is 'trial' or 'pro' here.

  let audioFile: File;
  try {
    const form = await req.formData();
    const audio = form.get('audio');
    if (!(audio instanceof File)) {
      return jsonResp({ error: 'missing_audio_field' }, 400);
    }
    audioFile = audio;
  } catch (e) {
    return jsonResp({ error: 'invalid_multipart', detail: String(e) }, 400);
  }

  let durationSec: number;
  try {
    const buf = await audioFile.arrayBuffer();
    durationSec = wavDurationSec(buf);
  } catch (e) {
    return jsonResp({ error: 'invalid_wav', detail: String(e) }, 400);
  }

  const minutesThisRequest = Math.ceil(durationSec / 60);
  const newTotal = ent.minutes_used_period + minutesThisRequest;

  if (newTotal > SOFT_CAP_MIN) {
    sendAbuseAlert(user.email ?? '(no email)', user.id, newTotal).catch((e) =>
      console.error('abuse alert dispatch failed', e)
    );
  }

  if (!OPENAI_API_KEY) {
    console.error('OPENAI_API_KEY not configured in Vault');
    return jsonResp({ error: 'server_misconfigured' }, 500);
  }

  const openaiForm = new FormData();
  openaiForm.append('file', audioFile, audioFile.name || 'audio.wav');
  openaiForm.append('model', WHISPER_MODEL);
  openaiForm.append('response_format', 'json');

  let openaiResp: Response;
  try {
    openaiResp = await fetch(OPENAI_URL, {
      method: 'POST',
      headers: { Authorization: `Bearer ${OPENAI_API_KEY}` },
      body: openaiForm,
    });
  } catch (e) {
    console.error('openai network error', e);
    return jsonResp({ error: 'upstream_unreachable' }, 502);
  }

  if (!openaiResp.ok) {
    const upstreamBody = await openaiResp.text();
    console.error('openai non-2xx', { status: openaiResp.status, body: upstreamBody });
    return jsonResp(
      { error: 'openai_error', upstream_status: openaiResp.status },
      openaiResp.status
    );
  }

  let text: string;
  try {
    const json = await openaiResp.json();
    text = typeof json.text === 'string' ? json.text : '';
  } catch (e) {
    console.error('openai response parse failed', e);
    return jsonResp({ error: 'upstream_invalid_response' }, 502);
  }

  // Atomic increment of the counter. Read-modify-write would race under
  // concurrent requests of the same user; this is best-effort but still
  // last-write-wins. For V1 the cap is soft (alert only), so eventual
  // accuracy is OK.
  const { error: updErr } = await sb
    .from('entitlements')
    .update({
      minutes_used_period: ent.minutes_used_period + minutesThisRequest,
      updated_at: new Date().toISOString(),
    })
    .eq('user_id', user.id);

  if (updErr) {
    console.error('counter update failed; returning transcription anyway', updErr);
  }

  return jsonResp({ text }, 200);
});

function wavDurationSec(buf: ArrayBuffer): number {
  const view = new DataView(buf);
  if (view.byteLength < 44) throw new Error('buffer too short for WAV');

  const tag = (off: number) =>
    String.fromCharCode(
      view.getUint8(off),
      view.getUint8(off + 1),
      view.getUint8(off + 2),
      view.getUint8(off + 3)
    );

  if (tag(0) !== 'RIFF' || tag(8) !== 'WAVE') {
    throw new Error('not a RIFF/WAVE file');
  }

  let offset = 12;
  let sampleRate = 0;
  let numChannels = 0;
  let bitsPerSample = 0;
  let dataSize = 0;

  while (offset + 8 <= view.byteLength) {
    const id = tag(offset);
    const size = view.getUint32(offset + 4, true);
    if (id === 'fmt ') {
      numChannels = view.getUint16(offset + 10, true);
      sampleRate = view.getUint32(offset + 12, true);
      bitsPerSample = view.getUint16(offset + 22, true);
    } else if (id === 'data') {
      dataSize = size;
      break;
    }
    offset += 8 + size;
  }

  if (!sampleRate || !numChannels || !bitsPerSample || !dataSize) {
    throw new Error('missing fmt or data chunk');
  }
  return dataSize / (sampleRate * numChannels * (bitsPerSample / 8));
}

async function sendAbuseAlert(
  email: string,
  userId: string,
  newTotal: number
): Promise<void> {
  const payload = {
    user_id: userId,
    email,
    minutes_used_period: newTotal,
    cap: SOFT_CAP_MIN,
  };
  if (!RESEND_API_KEY) {
    console.warn('soft_cap_exceeded (RESEND not configured, logging only)', payload);
    return;
  }
  const resp = await fetch('https://api.resend.com/emails', {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${RESEND_API_KEY}`,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({
      from: ABUSE_ALERT_FROM,
      to: ABUSE_ALERT_TO,
      subject: `Spikit usage alert: ${email} (${newTotal} min)`,
      text:
        `User ${userId} (${email}) used ${newTotal} minutes in current period.\n` +
        `Soft cap: ${SOFT_CAP_MIN} minutes / 30 days.\n` +
        `Decide: legitimate power user or abuse -> manual review in Supabase Studio.`,
    }),
  });
  if (!resp.ok) {
    console.error('resend non-2xx for abuse alert', {
      status: resp.status,
      body: await resp.text(),
      ...payload,
    });
  }
}

function jsonResp(body: unknown, status: number): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...CORS_HEADERS },
  });
}
