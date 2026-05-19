namespace Spikit.Services.Auth;

// Estados de monetización de un user, espejo de la enum `public.tier` del backend.
// Fuente de verdad: ADR-0007 § 1 (schema entitlements) + § 2 (transiciones).
public enum Tier
{
    Trial,
    Pro,
    Byok,
    Expired,
}
