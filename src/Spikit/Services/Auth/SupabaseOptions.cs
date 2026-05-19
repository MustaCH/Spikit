namespace Spikit.Services.Auth;

// Config del backend al que apunta el cliente WPF para auth + entitlement + Stripe.
// Los defaults reflejan la decisión "V1 sin custom domain": cliente habla directo con
// `*.supabase.co` (ver infra.md § "Custom domain"). Cuando se reactive `api.spikit.dev`
// (gating Pro plan + add-on, $35/mes) se cambia solo `BaseUrl` — el resto sigue igual.
public sealed class SupabaseOptions
{
    // Endpoint nativo del proyecto Supabase. Migrable a `https://api.spikit.dev` en
    // V1.x sin cambios de código.
    public string BaseUrl { get; set; } = "https://okomqtltwshgwruwulhv.supabase.co";

    // Publishable key (legacy "anon" en algunos endpoints, sb_publishable_* en otros).
    // Safe para shippear con el cliente — la auth real es server-side (RLS + Edge Functions
    // que verifican el JWT del user). Esta key sola no autoriza a leer/escribir nada.
    public string PublishableKey { get; set; } = "sb_publishable_QWuZ9_ysXPaBoUbZiZZxuw_uCNmgFor";

    // URL externa donde el usuario inicia el login (form de email → magic link).
    // La página intermediaria en spikit.dev/auth-callback redirige al deep-link
    // `spikit://auth-callback?...` después de que Supabase verifica el magic link.
    // Detalle del flow: ADR-0007 § 3.
    public string AuthLandingUrl { get; set; } = "https://spikit.dev/auth?return=spikit://auth-callback";
}
