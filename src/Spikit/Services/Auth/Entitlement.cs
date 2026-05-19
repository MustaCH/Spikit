namespace Spikit.Services.Auth;

// Snapshot del row `public.entitlements` que el cliente cachea localmente.
// Es solo lectura — el cliente nunca escribe directo en la tabla (RLS lo bloquea).
// Las mutaciones vienen del backend (signup trigger, Stripe webhook, daily_entitlement_sweep).
// Schema en ADR-0007 § 1.
public sealed record Entitlement(
    Tier Tier,
    DateTimeOffset? TrialEndsAt,
    DateTimeOffset? ProRenewsAt,
    DateTimeOffset? ByokGraceEndsAt,
    int MinutesUsedPeriod);
