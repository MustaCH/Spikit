namespace Spikit.Services.Auth;

// Fachada de auth que el resto de la app consume. Encapsula el ciclo completo:
// arrancar el flow de login (browser), recibir el callback del deep-link, validar
// el access_token, persistir tokens en DPAPI, popular el cache de entitlement,
// refrescar el access_token cuando expira, y logout.
//
// Surface intencionadamente chico: el WhisperApiTranscriptionService refactor
// (EP-10.11) solo va a llamar GetCurrentAccessTokenAsync; el UI de Account / Plan
// (EP-10.12) lee State + CurrentProfile + CurrentEntitlement.
public interface IAuthService
{
    AuthSessionState State { get; }
    UserProfile? CurrentProfile { get; }

    // Última snapshot conocida del entitlement (puede estar stale). Para UI bind.
    Entitlement? CurrentEntitlement { get; }

    // Notifica cuando State, CurrentProfile o CurrentEntitlement cambian. Idempotente
    // — listeners deciden qué leer en respuesta.
    event EventHandler? StateChanged;

    // Llamado en startup. Lee tokens de DPAPI, intenta validar (con refresh si hace
    // falta) y poblar Profile + Entitlement. Si todo falla, queda LoggedOut. Errores
    // de red son tolerados — los tokens se conservan para reintentar en el próximo
    // arranque.
    Task InitializeAsync(CancellationToken ct);

    // Abre el browser default en la landing de login. El flow del callback continúa
    // cuando llega un `spikit://auth-callback?...` via argv (otra sesión).
    Task StartLoginAsync(CancellationToken ct);

    // Procesa los params recibidos del deep-link `spikit://auth-callback?...`.
    // Si el callback es válido y el access_token chequea contra Supabase, persiste
    // tokens + fetch de entitlement, y la sesión queda LoggedIn.
    Task<AuthCallbackResult> HandleAuthCallbackAsync(
        IReadOnlyDictionary<string, string> queryParams,
        CancellationToken ct);

    // Borra tokens locales + cache de entitlement + intenta invalidar server-side el
    // refresh token (best-effort). La sesión queda LoggedOut después.
    Task LogoutAsync(CancellationToken ct);

    // Devuelve un access_token válido para llamar Edge Functions. Refresca si está
    // cerca del expiry. null si no hay sesión o si el refresh falla (en ese caso
    // ya hizo logout silencioso).
    Task<string?> GetCurrentAccessTokenAsync(CancellationToken ct);

    // Fuerza un re-fetch del entitlement contra el backend, actualizando el cache.
    // Para usar después de Stripe Checkout / Portal (ADR-0007 § 4.2). Devuelve null
    // si no hay sesión activa o el fetch falló — en ambos casos el cache queda como
    // estaba (stale).
    Task<Entitlement?> RefreshEntitlementAsync(CancellationToken ct);

    // Refresca el entitlement con backoff exponencial hasta que el predicado se cumple
    // o se agotan los reintentos. Mitiga la race condition de ADR-0007 § 4.2: entre el
    // deep-link de retorno de Stripe Checkout (cliente piensa "ya pagué") y el webhook
    // que actualiza `entitlements.tier` (server-side), hay milisegundos donde el cache
    // del cliente todavía ve el tier viejo. Reintentos cada 200ms/500ms/1s/2s/5s.
    //
    // Devuelve el último Entitlement obtenido (sea o no aceptable según el predicado), o
    // null si nunca pudo refrescar (sin sesión / fetch falla las 5 veces).
    Task<Entitlement?> RefreshEntitlementWithBackoffAsync(
        Func<Entitlement, bool> isAcceptable,
        CancellationToken ct);
}
