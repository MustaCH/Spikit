namespace Spikit.Services.Auth;

// Cliente HTTP de los endpoints de Supabase Auth (`/auth/v1/*`). Tres operaciones:
// validar un access_token recién recibido, refrescarlo cuando expira, e invalidarlo
// en logout. Endpoints documentados en ADR-0007 § 9.
public interface ISupabaseAuthClient
{
    // GET /auth/v1/user con Bearer accessToken. Tira AuthTokenInvalidException si el
    // server responde 401/403 — caller decide entre intentar refresh o forzar re-login.
    // Tira AuthException para errores de red u otros 4xx/5xx.
    Task<UserProfile> ValidateAccessTokenAsync(string accessToken, CancellationToken ct);

    // POST /auth/v1/token?grant_type=refresh_token con body { refresh_token }. Tira
    // AuthRefreshFailedException si el refresh fue rechazado (401/400) — caller borra
    // tokens y muestra re-login. Tira AuthException para errores de red.
    Task<AccessTokenPair> RefreshAsync(string refreshToken, CancellationToken ct);

    // POST /auth/v1/logout con Bearer accessToken. Es best-effort: si falla por red o
    // server, no propaga — el cliente igual borra los tokens locales. La intención del
    // endpoint es invalidar el refresh_token server-side. Loguea internamente.
    Task LogoutAsync(string accessToken, CancellationToken ct);
}
