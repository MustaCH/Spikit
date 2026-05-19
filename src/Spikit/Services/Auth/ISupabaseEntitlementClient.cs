namespace Spikit.Services.Auth;

// Cliente HTTP del Edge Function `entitlement` (GET /functions/v1/entitlement).
// Se llama después del login para popular el cache + cada 24h para refrescarlo + después
// de cualquier flujo Stripe (Checkout / Portal) para forzar re-fetch. La Edge Function
// además crea la fila inicial si no existe (signup → trial/byok). Detalle ADR-0007 § 8.
public interface ISupabaseEntitlementClient
{
    // Devuelve el entitlement actual del user. Tira AuthTokenInvalidException si el
    // JWT está expirado/inválido (caller intenta refresh). AuthException para otros
    // errores de red o server.
    Task<Entitlement> FetchAsync(string accessToken, CancellationToken ct);
}
