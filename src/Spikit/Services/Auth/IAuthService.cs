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

    // EP-11.8 — true cuando la sesión está LoggedIn pero el último round-trip al
    // server (Init o post-Init refresh) falló por red y caímos al cache. El
    // OfflineRefreshWorker hace polling en background para salir de este modo.
    // El UI puede ignorarlo en V1; el flag está expuesto para tests + decisiones
    // futuras de feedback al usuario.
    bool IsOfflineMode { get; }

    // EP-11.8 — outcome del último InitializeAsync. App.xaml.cs lo usa para decidir
    // si tras un boot con tokens persistidos + State=LoggedOut conviene mostrar
    // SessionExpired (revoke server-side) o un mensaje de red caída.
    AuthInitOutcome LastInitializeOutcome { get; }

    // Notifica cuando State, CurrentProfile o CurrentEntitlement cambian. Idempotente
    // — listeners deciden qué leer en respuesta.
    event EventHandler? StateChanged;

    // Disparado por el SpikitUriDispatcher cuando llega un deep-link
    // `spikit://auth-pending?email=...` (cierre Q-9 de ADR-0008 — la página
    // spikit.dev/auth emite el redirect tras un signInWithOtp exitoso). El payload
    // es el email URL-decoded; el listener (típicamente LoginViewModel) lo usa para
    // mutar al estado WaitingForMagicLink mostrando el email exacto.
    //
    // El AuthService NO procesa este evento internamente (no toca tokens, no cambia
    // State). Sólo lo expone como canal de comunicación entre el dispatcher (que
    // recibe el URI) y los consumidores de UI. Si no hay listener, no pasa nada —
    // el dispatcher loguea + sigue.
    event EventHandler<string>? AuthPendingReceived;

    // Invocado por el SpikitUriDispatcher al recibir `auth-pending`. Centraliza el
    // disparo del evento `AuthPendingReceived` para que el dispatcher no tenga que
    // conocer el contrato interno del raise. La impl real solo invoca el delegate.
    //
    // TODO(refactor): exponer un método público "raise event" en una interface es un
    // smell — cualquiera del DI graph puede disparar AuthPendingReceived con un email
    // arbitrario. Patrón más limpio: mover el evento al `ISpikitUriDispatcher` (es
    // quien recibe el URI, es la fuente natural del evento) y que el LoginViewModel
    // se suscriba ahí. No urgente porque el surface lo consume solo el LoginVM hoy,
    // pero si EP-11.6 (logout) o EP-11.7 (offline) suman listeners, conviene migrar.
    void RaiseAuthPendingReceived(string email);

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

    // Fuerza un refresh del access_token usando el refresh_token, ignorando el chequeo
    // de expiry local. Para llamar cuando un endpoint upstream devuelve 401 con un
    // token que localmente todavía parecía válido (clock skew, revocación server-side,
    // etc.). Devuelve el nuevo access_token o null si el refresh falló (en ese caso
    // la sesión queda LoggedOut).
    Task<string?> ForceRefreshAccessTokenAsync(CancellationToken ct);

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
