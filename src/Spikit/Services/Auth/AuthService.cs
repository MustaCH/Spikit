using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Spikit.Services.Auth;

// Orchestrador de auth. Coordina IAuthTokenStore, IEntitlementCache, ISupabaseAuthClient,
// ISupabaseEntitlementClient e IBrowserLauncher. Mantiene state en memoria, expone vía
// IAuthService al resto de la app, persiste lo que tiene que sobrevivir reinicios en
// DPAPI. Thread-safe: el refresh de tokens está bajo SemaphoreSlim para evitar dos
// requests concurrentes invalidándose entre sí.
public sealed class AuthService : IAuthService, IDisposable
{
    // Ventana antes del ExpiresAt en la que ya consideramos el token vencido y forzamos
    // refresh. Evita race con un request HTTP en vuelo que toma 1-2s y le llega 401.
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(2);

    // Cadence de reintentos del refresh post-Stripe (ADR-0007 § 4.2). 5 intentos con
    // delay creciente antes de cada uno; total worst-case ~8.7s. Internamente
    // accesible para que los tests inyecten una cadence más corta.
    internal static readonly IReadOnlyList<TimeSpan> DefaultBackoffSchedule = new[]
    {
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
    };

    private readonly IAuthTokenStore _tokenStore;
    private readonly IEntitlementCache _entitlementCache;
    private readonly ISupabaseAuthClient _authClient;
    private readonly ISupabaseEntitlementClient _entitlementClient;
    private readonly IBrowserLauncher _browser;
    private readonly SupabaseOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AuthService> _logger;

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private AuthSessionState _state = AuthSessionState.LoggedOut;
    private UserProfile? _profile;

    public AuthService(
        IAuthTokenStore tokenStore,
        IEntitlementCache entitlementCache,
        ISupabaseAuthClient authClient,
        ISupabaseEntitlementClient entitlementClient,
        IBrowserLauncher browser,
        IOptions<SupabaseOptions> options,
        ILogger<AuthService> logger)
        : this(tokenStore, entitlementCache, authClient, entitlementClient, browser,
               options, TimeProvider.System, logger)
    {
    }

    // Constructor extendido para tests (inyectar TimeProvider fake — sin esto, los
    // tests que dependen de "ya pasó el expiry" tendrían que esperar 1+ hora real).
    public AuthService(
        IAuthTokenStore tokenStore,
        IEntitlementCache entitlementCache,
        ISupabaseAuthClient authClient,
        ISupabaseEntitlementClient entitlementClient,
        IBrowserLauncher browser,
        IOptions<SupabaseOptions> options,
        TimeProvider time,
        ILogger<AuthService> logger)
    {
        _tokenStore = tokenStore;
        _entitlementCache = entitlementCache;
        _authClient = authClient;
        _entitlementClient = entitlementClient;
        _browser = browser;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public AuthSessionState State => _state;
    public UserProfile? CurrentProfile => _profile;
    public Entitlement? CurrentEntitlement => _entitlementCache.ReadStale();

    public event EventHandler? StateChanged;

    public async Task InitializeAsync(CancellationToken ct)
    {
        var tokens = _tokenStore.Read();
        if (tokens is null)
        {
            _logger.LogDebug("Sin tokens en DPAPI — arranco LoggedOut");
            return;
        }

        try
        {
            var accessToken = await EnsureFreshAccessTokenInternalAsync(tokens, ct).ConfigureAwait(false);
            var profile = await _authClient.ValidateAccessTokenAsync(accessToken, ct).ConfigureAwait(false);
            SetLoggedIn(profile);

            // Best-effort: si el cache está fresh, no hace falta refetch. Si está vencido
            // o no existe, lo refrescamos en background sin bloquear startup.
            if (_entitlementCache.ReadFresh() is null)
            {
                _ = RefreshEntitlementAsync(ct);
            }
        }
        catch (AuthTokenInvalidException ex)
        {
            // Server rechazó el token incluso post-refresh — limpiamos y caemos a logged out.
            _logger.LogWarning(ex, "Init: server rechazó tokens — limpiando para forzar re-login");
            ClearAndLogout();
        }
        catch (AuthRefreshFailedException ex)
        {
            _logger.LogWarning(ex, "Init: refresh falló — limpiando para forzar re-login");
            ClearAndLogout();
        }
        catch (AuthException ex)
        {
            // Red / 5xx — preservamos tokens. La UI ve LoggedOut hasta el próximo arranque
            // con red, momento en el que se intenta de nuevo.
            _logger.LogWarning(ex, "Init: error transitorio, sigo offline. Tokens preservados");
        }
    }

    public Task StartLoginAsync(CancellationToken ct)
    {
        _logger.LogInformation("Abriendo browser para login: {Url}", _options.AuthLandingUrl);
        _browser.Open(_options.AuthLandingUrl);
        return Task.CompletedTask;
    }

    public async Task<AuthCallbackResult> HandleAuthCallbackAsync(
        IReadOnlyDictionary<string, string> queryParams,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(queryParams);

        // Supabase a veces incluye `error` / `error_description` en lugar de tokens
        // (magic link expirado, link ya consumido, etc.).
        if (queryParams.TryGetValue("error_description", out var errorDescription))
        {
            return new AuthCallbackResult(false, null, null, errorDescription);
        }

        if (!queryParams.TryGetValue("access_token", out var accessToken)
            || string.IsNullOrEmpty(accessToken)
            || !queryParams.TryGetValue("refresh_token", out var refreshToken)
            || string.IsNullOrEmpty(refreshToken))
        {
            return new AuthCallbackResult(false, null, null,
                "El callback no incluyó access_token / refresh_token");
        }

        var expiresIn = 3600;
        if (queryParams.TryGetValue("expires_in", out var raw)
            && int.TryParse(raw, out var parsedExpires) && parsedExpires > 0)
        {
            expiresIn = parsedExpires;
        }

        // Crítico: NO confiamos en el query string ciegamente. Validamos contra Supabase
        // para confirmar que el token es genuino y no fue plantado por un attacker via
        // un deep-link malicioso (ADR-0007 § 3 "Notas críticas").
        UserProfile profile;
        try
        {
            profile = await _authClient.ValidateAccessTokenAsync(accessToken, ct).ConfigureAwait(false);
        }
        catch (AuthTokenInvalidException)
        {
            return new AuthCallbackResult(false, null, null,
                "El access_token del callback no es válido (¿link expirado o plantado?)");
        }
        catch (AuthException ex)
        {
            return new AuthCallbackResult(false, null, null,
                $"No se pudo validar el callback: {ex.Message}");
        }

        var pair = new AccessTokenPair(
            accessToken, refreshToken,
            _time.GetUtcNow().AddSeconds(expiresIn));
        _tokenStore.Write(pair);

        Entitlement? entitlement = null;
        try
        {
            entitlement = await _entitlementClient.FetchAsync(accessToken, ct).ConfigureAwait(false);
            _entitlementCache.Write(entitlement);
        }
        catch (AuthException ex)
        {
            // Login fue exitoso, solo falló el fetch del entitlement. Sesión queda logueada
            // — el UI puede mostrar "cargando plan…" y reintentar después.
            _logger.LogWarning(ex, "Callback OK pero fetch entitlement falló — sesión queda activa");
        }

        SetLoggedIn(profile);
        return new AuthCallbackResult(true, profile, entitlement, null);
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        var tokens = _tokenStore.Read();

        // Limpiar local primero — incluso si la llamada al server falla, el user queda
        // logged out de hecho. Cumplir el intent del UI.
        _tokenStore.Clear();
        _entitlementCache.Clear();
        SetLoggedOut();

        if (tokens is not null)
        {
            await _authClient.LogoutAsync(tokens.AccessToken, ct).ConfigureAwait(false);
        }
    }

    public async Task<string?> GetCurrentAccessTokenAsync(CancellationToken ct)
    {
        var tokens = _tokenStore.Read();
        if (tokens is null) return null;

        if (IsStillFresh(tokens)) return tokens.AccessToken;

        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-leer adentro del lock: otra task pudo haber refrescado.
            tokens = _tokenStore.Read();
            if (tokens is null) return null;
            if (IsStillFresh(tokens)) return tokens.AccessToken;

            try
            {
                var refreshed = await _authClient.RefreshAsync(tokens.RefreshToken, ct).ConfigureAwait(false);
                _tokenStore.Write(refreshed);
                return refreshed.AccessToken;
            }
            catch (AuthRefreshFailedException ex)
            {
                _logger.LogWarning(ex, "Refresh rechazado por server — limpio sesión");
                ClearAndLogout();
                return null;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task<Entitlement?> RefreshEntitlementAsync(CancellationToken ct)
    {
        var accessToken = await GetCurrentAccessTokenAsync(ct).ConfigureAwait(false);
        if (accessToken is null) return null;

        try
        {
            var entitlement = await _entitlementClient.FetchAsync(accessToken, ct).ConfigureAwait(false);
            _entitlementCache.Write(entitlement);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return entitlement;
        }
        catch (AuthException ex)
        {
            _logger.LogWarning(ex, "RefreshEntitlement falló — cache queda como estaba");
            return null;
        }
    }

    public Task<Entitlement?> RefreshEntitlementWithBackoffAsync(
        Func<Entitlement, bool> isAcceptable, CancellationToken ct) =>
        RefreshEntitlementWithBackoffAsync(isAcceptable, DefaultBackoffSchedule, ct);

    // Overload internal para que los tests pasen un schedule con delays cortos sin
    // esperar los 8.7s del default.
    internal async Task<Entitlement?> RefreshEntitlementWithBackoffAsync(
        Func<Entitlement, bool> isAcceptable,
        IReadOnlyList<TimeSpan> backoffSchedule,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(isAcceptable);
        ArgumentNullException.ThrowIfNull(backoffSchedule);

        Entitlement? last = null;
        for (var attempt = 0; attempt < backoffSchedule.Count; attempt++)
        {
            try
            {
                await Task.Delay(backoffSchedule[attempt], ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return last;
            }

            var fetched = await RefreshEntitlementAsync(ct).ConfigureAwait(false);
            if (fetched is not null)
            {
                last = fetched;
                if (isAcceptable(fetched))
                {
                    _logger.LogInformation(
                        "RefreshEntitlementWithBackoff: aceptable en intento {Attempt}/{Total}",
                        attempt + 1, backoffSchedule.Count);
                    return fetched;
                }
            }
        }

        _logger.LogInformation(
            "RefreshEntitlementWithBackoff: agotó {Total} intentos sin alcanzar el predicado",
            backoffSchedule.Count);
        return last;
    }

    public void Dispose() => _refreshLock.Dispose();

    private bool IsStillFresh(AccessTokenPair tokens) =>
        _time.GetUtcNow() + RefreshBuffer < tokens.ExpiresAt;

    private async Task<string> EnsureFreshAccessTokenInternalAsync(
        AccessTokenPair tokens, CancellationToken ct)
    {
        if (IsStillFresh(tokens)) return tokens.AccessToken;

        var refreshed = await _authClient.RefreshAsync(tokens.RefreshToken, ct).ConfigureAwait(false);
        _tokenStore.Write(refreshed);
        return refreshed.AccessToken;
    }

    private void ClearAndLogout()
    {
        _tokenStore.Clear();
        _entitlementCache.Clear();
        SetLoggedOut();
    }

    private void SetLoggedIn(UserProfile profile)
    {
        _state = AuthSessionState.LoggedIn;
        _profile = profile;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetLoggedOut()
    {
        _state = AuthSessionState.LoggedOut;
        _profile = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
