using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Spikit.Services.Auth;

namespace Spikit.Tests.Services.Auth;

public class AuthServiceTests
{
    private readonly FakeTokenStore _tokens = new();
    private readonly FakeEntitlementCache _cache = new();
    private readonly FakeAuthClient _authClient = new();
    private readonly FakeEntitlementClient _entitlementClient = new();
    private readonly FakeBrowser _browser = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 05, 19, 12, 00, 00, TimeSpan.Zero));
    private readonly SupabaseOptions _options = new()
    {
        AuthLandingUrl = "https://spikit.dev/auth?return=spikit://auth-callback",
    };

    private AuthService BuildService() => new(
        _tokens, _cache, _authClient, _entitlementClient, _browser,
        Options.Create(_options), _time, NullLogger<AuthService>.Instance);

    private static AccessTokenPair PairExpiringAt(DateTimeOffset at) =>
        new("access-x", "refresh-x", at);

    private static UserProfile SampleProfile() => new("user-1", "nacho@spikit.dev");

    private static Entitlement SampleEntitlement() => new(
        Tier.Pro, null,
        ProRenewsAt: new DateTimeOffset(2026, 06, 19, 0, 0, 0, TimeSpan.Zero),
        ByokGraceEndsAt: null,
        MinutesUsedPeriod: 0);

    // ──────────────────────────────────── InitializeAsync ────────────────────────

    [Fact]
    public async Task InitializeAsync_no_tokens_stays_LoggedOut()
    {
        var svc = BuildService();

        await svc.InitializeAsync(CancellationToken.None);

        Assert.Equal(AuthSessionState.LoggedOut, svc.State);
        Assert.Null(svc.CurrentProfile);
    }

    [Fact]
    public async Task InitializeAsync_fresh_tokens_validates_and_logs_in()
    {
        _tokens.Pair = PairExpiringAt(_time.GetUtcNow().AddHours(1));
        _authClient.ProfileForValidate = SampleProfile();
        _entitlementClient.NextResult = SampleEntitlement();
        var svc = BuildService();

        await svc.InitializeAsync(CancellationToken.None);
        // Background entitlement fetch puede haber sido encolado — esperamos un yield
        // para que tests deterministicen.
        await Task.Yield();

        Assert.Equal(AuthSessionState.LoggedIn, svc.State);
        Assert.Equal("nacho@spikit.dev", svc.CurrentProfile!.Email);
    }

    [Fact]
    public async Task InitializeAsync_expired_access_refreshes_then_validates()
    {
        _tokens.Pair = PairExpiringAt(_time.GetUtcNow().AddSeconds(-10));
        _authClient.NextRefreshResult = new AccessTokenPair("new-access", "new-refresh",
            _time.GetUtcNow().AddHours(1));
        _authClient.ProfileForValidate = SampleProfile();
        var svc = BuildService();

        await svc.InitializeAsync(CancellationToken.None);

        Assert.Equal(AuthSessionState.LoggedIn, svc.State);
        Assert.Equal("new-access", _tokens.Pair!.AccessToken);
        Assert.Equal(1, _authClient.RefreshCallCount);
        Assert.Equal("new-access", _authClient.LastValidatedToken);
    }

    [Fact]
    public async Task InitializeAsync_token_invalid_after_validate_clears_session()
    {
        _tokens.Pair = PairExpiringAt(_time.GetUtcNow().AddHours(1));
        _authClient.ValidateThrows = new AuthTokenInvalidException("rejected");
        var svc = BuildService();

        await svc.InitializeAsync(CancellationToken.None);

        Assert.Equal(AuthSessionState.LoggedOut, svc.State);
        Assert.Null(_tokens.Pair);
    }

    [Fact]
    public async Task InitializeAsync_refresh_failed_clears_session()
    {
        _tokens.Pair = PairExpiringAt(_time.GetUtcNow().AddSeconds(-10));
        _authClient.RefreshThrows = new AuthRefreshFailedException("expired");
        var svc = BuildService();

        await svc.InitializeAsync(CancellationToken.None);

        Assert.Equal(AuthSessionState.LoggedOut, svc.State);
        Assert.Null(_tokens.Pair);
    }

    [Fact]
    public async Task InitializeAsync_network_error_without_cache_preserves_tokens_and_LoggedOut()
    {
        // EP-11.8 — sin cache válido el fallback offline se rechaza; los tokens se
        // preservan para reintentar en el próximo arranque (no es revoke).
        var originalPair = PairExpiringAt(_time.GetUtcNow().AddHours(1));
        _tokens.Pair = originalPair;
        _authClient.ValidateThrows = new AuthException("network glitch");
        _cache.OfflineFallbackEntitlement = null;
        var svc = BuildService();

        await svc.InitializeAsync(CancellationToken.None);

        Assert.Equal(AuthSessionState.LoggedOut, svc.State);
        Assert.False(svc.IsOfflineMode);
        Assert.Equal(AuthInitOutcome.NetworkFailure, svc.LastInitializeOutcome);
        Assert.Same(originalPair, _tokens.Pair);
    }

    // ──────────────────── EP-11.8: offline fallback ──────────────────────────────

    [Fact]
    public async Task InitializeAsync_network_error_with_valid_cache_enters_offline_mode()
    {
        // JWT decodificable → fallback OK. Auth queda LoggedIn con IsOfflineMode=true
        // y el profile viene de las claims del JWT, no de un round-trip al server.
        var jwt = TestJwt.Build(sub: "user-offline", email: "nacho@spikit.dev");
        var originalPair = new AccessTokenPair(jwt, "refresh-x",
            _time.GetUtcNow().AddHours(1));
        _tokens.Pair = originalPair;
        _authClient.ValidateThrows = new AuthException("network glitch");
        _cache.OfflineFallbackEntitlement = SampleEntitlement();
        var svc = BuildService();

        await svc.InitializeAsync(CancellationToken.None);

        Assert.Equal(AuthSessionState.LoggedIn, svc.State);
        Assert.True(svc.IsOfflineMode);
        Assert.Equal(AuthInitOutcome.NetworkFailure, svc.LastInitializeOutcome);
        Assert.Equal("nacho@spikit.dev", svc.CurrentProfile!.Email);
        Assert.Same(originalPair, _tokens.Pair);
    }

    [Fact]
    public async Task InitializeAsync_network_error_with_unparseable_jwt_stays_LoggedOut()
    {
        // Cache existe pero el JWT no parsea a sub+email — rechazamos el offline mode
        // (la UI no podría mostrar "estás logueado como X").
        _tokens.Pair = new AccessTokenPair("not-a-jwt", "refresh-x",
            _time.GetUtcNow().AddHours(1));
        _authClient.ValidateThrows = new AuthException("network glitch");
        _cache.OfflineFallbackEntitlement = SampleEntitlement();
        var svc = BuildService();

        await svc.InitializeAsync(CancellationToken.None);

        Assert.Equal(AuthSessionState.LoggedOut, svc.State);
        Assert.False(svc.IsOfflineMode);
    }

    [Fact]
    public async Task InitializeAsync_success_keeps_offline_mode_off()
    {
        _tokens.Pair = PairExpiringAt(_time.GetUtcNow().AddHours(1));
        _authClient.ProfileForValidate = SampleProfile();
        _cache.OfflineFallbackEntitlement = SampleEntitlement();
        var svc = BuildService();

        await svc.InitializeAsync(CancellationToken.None);

        Assert.False(svc.IsOfflineMode);
        Assert.Equal(AuthInitOutcome.Success, svc.LastInitializeOutcome);
    }

    [Fact]
    public async Task InitializeAsync_token_invalid_records_SessionRevoked_outcome()
    {
        _tokens.Pair = PairExpiringAt(_time.GetUtcNow().AddHours(1));
        _authClient.ValidateThrows = new AuthTokenInvalidException("rejected");
        var svc = BuildService();

        await svc.InitializeAsync(CancellationToken.None);

        Assert.Equal(AuthInitOutcome.SessionRevoked, svc.LastInitializeOutcome);
    }

    [Fact]
    public async Task RefreshEntitlementAsync_success_clears_offline_mode()
    {
        // Setup: arrancar en offline mode.
        var jwt = TestJwt.Build(sub: "u1", email: "x@y.com");
        _tokens.Pair = new AccessTokenPair(jwt, "r", _time.GetUtcNow().AddHours(1));
        _authClient.ValidateThrows = new AuthException("network glitch");
        _cache.OfflineFallbackEntitlement = SampleEntitlement();
        var svc = BuildService();
        await svc.InitializeAsync(CancellationToken.None);
        Assert.True(svc.IsOfflineMode);

        // Ahora simulamos que el server volvió: limpiamos el throw y damos un entitlement.
        _authClient.ValidateThrows = null;
        _entitlementClient.NextResult = SampleEntitlement();

        var result = await svc.RefreshEntitlementAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(svc.IsOfflineMode);
    }

    [Fact]
    public async Task LogoutAsync_clears_offline_mode()
    {
        var jwt = TestJwt.Build(sub: "u1", email: "x@y.com");
        _tokens.Pair = new AccessTokenPair(jwt, "r", _time.GetUtcNow().AddHours(1));
        _authClient.ValidateThrows = new AuthException("net");
        _cache.OfflineFallbackEntitlement = SampleEntitlement();
        var svc = BuildService();
        await svc.InitializeAsync(CancellationToken.None);
        Assert.True(svc.IsOfflineMode);

        await svc.LogoutAsync(CancellationToken.None);

        Assert.False(svc.IsOfflineMode);
        Assert.Equal(AuthSessionState.LoggedOut, svc.State);
    }

    // ──────────────────────────────────── StartLoginAsync ─────────────────────────

    [Fact]
    public async Task StartLoginAsync_opens_browser_with_landing_url()
    {
        var svc = BuildService();

        await svc.StartLoginAsync(CancellationToken.None);

        Assert.Equal(_options.AuthLandingUrl, _browser.LastUrl);
    }

    // ─────────────────────────────── HandleAuthCallbackAsync ──────────────────────

    [Fact]
    public async Task HandleAuthCallbackAsync_happy_path_persists_tokens_and_entitlement()
    {
        _authClient.ProfileForValidate = SampleProfile();
        _entitlementClient.NextResult = SampleEntitlement();
        var svc = BuildService();

        var result = await svc.HandleAuthCallbackAsync(new Dictionary<string, string>
        {
            ["access_token"] = "got-access",
            ["refresh_token"] = "got-refresh",
            ["expires_in"] = "3600",
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("nacho@spikit.dev", result.Profile!.Email);
        Assert.Equal(Tier.Pro, result.Entitlement!.Tier);
        Assert.Equal("got-access", _tokens.Pair!.AccessToken);
        Assert.Equal(_time.GetUtcNow().AddSeconds(3600), _tokens.Pair.ExpiresAt);
        Assert.Equal(AuthSessionState.LoggedIn, svc.State);
        Assert.Same(_entitlementClient.NextResult, _cache.LastWrite);
    }

    [Fact]
    public async Task HandleAuthCallbackAsync_error_description_returns_failure()
    {
        var svc = BuildService();

        var result = await svc.HandleAuthCallbackAsync(new Dictionary<string, string>
        {
            ["error"] = "access_denied",
            ["error_description"] = "Magic link expired",
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("Magic link expired", result.ErrorReason);
        Assert.Null(_tokens.Pair);
    }

    [Fact]
    public async Task HandleAuthCallbackAsync_missing_tokens_returns_failure()
    {
        var svc = BuildService();

        var result = await svc.HandleAuthCallbackAsync(new Dictionary<string, string>
        {
            ["access_token"] = "x",
            // missing refresh_token
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(_tokens.Pair);
    }

    [Fact]
    public async Task HandleAuthCallbackAsync_validate_fails_returns_failure_and_does_not_persist()
    {
        _authClient.ValidateThrows = new AuthTokenInvalidException("plantado");
        var svc = BuildService();

        var result = await svc.HandleAuthCallbackAsync(new Dictionary<string, string>
        {
            ["access_token"] = "a", ["refresh_token"] = "b",
        }, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Null(_tokens.Pair);
        Assert.Equal(AuthSessionState.LoggedOut, svc.State);
    }

    [Fact]
    public async Task HandleAuthCallbackAsync_validate_ok_but_entitlement_fetch_fails_still_logs_in()
    {
        _authClient.ProfileForValidate = SampleProfile();
        _entitlementClient.NextException = new AuthException("entitlement endpoint 500");
        var svc = BuildService();

        var result = await svc.HandleAuthCallbackAsync(new Dictionary<string, string>
        {
            ["access_token"] = "a", ["refresh_token"] = "b",
        }, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Null(result.Entitlement);
        Assert.Equal(AuthSessionState.LoggedIn, svc.State);
        Assert.NotNull(_tokens.Pair);
    }

    [Fact]
    public async Task HandleAuthCallbackAsync_uses_default_expires_in_when_absent()
    {
        _authClient.ProfileForValidate = SampleProfile();
        _entitlementClient.NextResult = SampleEntitlement();
        var svc = BuildService();

        await svc.HandleAuthCallbackAsync(new Dictionary<string, string>
        {
            ["access_token"] = "a", ["refresh_token"] = "b",
        }, CancellationToken.None);

        // Default 3600s.
        Assert.Equal(_time.GetUtcNow().AddSeconds(3600), _tokens.Pair!.ExpiresAt);
    }

    // ────────────────────────────────────── LogoutAsync ───────────────────────────

    [Fact]
    public async Task LogoutAsync_clears_state_and_calls_server()
    {
        _tokens.Pair = PairExpiringAt(_time.GetUtcNow().AddHours(1));
        _cache.LastWrite = SampleEntitlement();
        _authClient.ProfileForValidate = SampleProfile();
        var svc = BuildService();
        await svc.InitializeAsync(CancellationToken.None);
        await Task.Yield();

        await svc.LogoutAsync(CancellationToken.None);

        Assert.Equal(AuthSessionState.LoggedOut, svc.State);
        Assert.Null(svc.CurrentProfile);
        Assert.Null(_tokens.Pair);
        Assert.True(_cache.Cleared);
        Assert.Equal(1, _authClient.LogoutCallCount);
    }

    [Fact]
    public async Task LogoutAsync_no_tokens_still_clears_state_and_skips_server_call()
    {
        var svc = BuildService();

        await svc.LogoutAsync(CancellationToken.None);

        Assert.Equal(AuthSessionState.LoggedOut, svc.State);
        Assert.Equal(0, _authClient.LogoutCallCount);
    }

    // ───────────────────────────── GetCurrentAccessTokenAsync ─────────────────────

    [Fact]
    public async Task GetCurrentAccessTokenAsync_returns_null_when_no_tokens()
    {
        var svc = BuildService();

        var token = await svc.GetCurrentAccessTokenAsync(CancellationToken.None);

        Assert.Null(token);
    }

    [Fact]
    public async Task GetCurrentAccessTokenAsync_returns_existing_when_fresh()
    {
        _tokens.Pair = PairExpiringAt(_time.GetUtcNow().AddHours(1));
        var svc = BuildService();

        var token = await svc.GetCurrentAccessTokenAsync(CancellationToken.None);

        Assert.Equal("access-x", token);
        Assert.Equal(0, _authClient.RefreshCallCount);
    }

    [Fact]
    public async Task GetCurrentAccessTokenAsync_refreshes_when_within_buffer_window()
    {
        // Buffer es 2 minutos antes del expiry.
        _tokens.Pair = PairExpiringAt(_time.GetUtcNow().AddMinutes(1));
        _authClient.NextRefreshResult = new AccessTokenPair("fresh-access", "fresh-refresh",
            _time.GetUtcNow().AddHours(1));
        var svc = BuildService();

        var token = await svc.GetCurrentAccessTokenAsync(CancellationToken.None);

        Assert.Equal("fresh-access", token);
        Assert.Equal("fresh-access", _tokens.Pair!.AccessToken);
        Assert.Equal(1, _authClient.RefreshCallCount);
    }

    [Fact]
    public async Task GetCurrentAccessTokenAsync_refresh_failed_clears_and_returns_null()
    {
        _tokens.Pair = PairExpiringAt(_time.GetUtcNow().AddSeconds(-10));
        _authClient.RefreshThrows = new AuthRefreshFailedException("expired");
        var svc = BuildService();

        var token = await svc.GetCurrentAccessTokenAsync(CancellationToken.None);

        Assert.Null(token);
        Assert.Null(_tokens.Pair);
        Assert.Equal(AuthSessionState.LoggedOut, svc.State);
    }

    // ────────────────────────────── ForceRefreshAccessTokenAsync ──────────────────

    [Fact]
    public async Task ForceRefreshAccessTokenAsync_calls_refresh_even_when_token_is_fresh()
    {
        // El token local está fresco (1h hasta expirar) pero el caller pide force-refresh
        // — ej. el server devolvió 401 con un token que parecía válido.
        _tokens.Pair = PairExpiringAt(_time.GetUtcNow().AddHours(1));
        _authClient.NextRefreshResult = new AccessTokenPair("brand-new", "brand-new-refresh",
            _time.GetUtcNow().AddHours(1));
        var svc = BuildService();

        var token = await svc.ForceRefreshAccessTokenAsync(CancellationToken.None);

        Assert.Equal("brand-new", token);
        Assert.Equal("brand-new", _tokens.Pair!.AccessToken);
        Assert.Equal(1, _authClient.RefreshCallCount);
    }

    [Fact]
    public async Task ForceRefreshAccessTokenAsync_returns_null_when_no_session()
    {
        var svc = BuildService();

        var token = await svc.ForceRefreshAccessTokenAsync(CancellationToken.None);

        Assert.Null(token);
        Assert.Equal(0, _authClient.RefreshCallCount);
    }

    [Fact]
    public async Task ForceRefreshAccessTokenAsync_clears_session_when_refresh_fails()
    {
        _tokens.Pair = PairExpiringAt(_time.GetUtcNow().AddHours(1));
        _authClient.RefreshThrows = new AuthRefreshFailedException("revoked");
        var svc = BuildService();

        var token = await svc.ForceRefreshAccessTokenAsync(CancellationToken.None);

        Assert.Null(token);
        Assert.Null(_tokens.Pair);
        Assert.Equal(AuthSessionState.LoggedOut, svc.State);
    }

    // ───────────────────────────── RefreshEntitlementAsync ────────────────────────

    [Fact]
    public async Task RefreshEntitlementAsync_no_session_returns_null()
    {
        var svc = BuildService();

        var ent = await svc.RefreshEntitlementAsync(CancellationToken.None);

        Assert.Null(ent);
    }

    [Fact]
    public async Task RefreshEntitlementAsync_updates_cache_and_fires_StateChanged()
    {
        _tokens.Pair = PairExpiringAt(_time.GetUtcNow().AddHours(1));
        _entitlementClient.NextResult = SampleEntitlement();
        var svc = BuildService();
        var stateChangedCount = 0;
        svc.StateChanged += (_, _) => stateChangedCount++;

        var ent = await svc.RefreshEntitlementAsync(CancellationToken.None);

        Assert.NotNull(ent);
        Assert.Equal(Tier.Pro, ent!.Tier);
        Assert.Same(ent, _cache.LastWrite);
        Assert.Equal(1, stateChangedCount);
    }

    [Fact]
    public async Task RefreshEntitlementAsync_fetch_fails_returns_null_preserves_cache()
    {
        _tokens.Pair = PairExpiringAt(_time.GetUtcNow().AddHours(1));
        _cache.LastWrite = SampleEntitlement(); // hay algo cacheado de antes
        _entitlementClient.NextException = new AuthException("network");
        var svc = BuildService();

        var ent = await svc.RefreshEntitlementAsync(CancellationToken.None);

        Assert.Null(ent);
        Assert.NotNull(_cache.LastWrite); // sigue ahí
    }

    // ──────────────────────────── RefreshEntitlementWithBackoff ───────────────────

    // Schedule corto para que los tests corran rápido — el real es 200/500/1s/2s/5s.
    private static readonly IReadOnlyList<TimeSpan> FastBackoff = new[]
    {
        TimeSpan.FromMilliseconds(1),
        TimeSpan.FromMilliseconds(1),
        TimeSpan.FromMilliseconds(1),
        TimeSpan.FromMilliseconds(1),
        TimeSpan.FromMilliseconds(1),
    };

    [Fact]
    public async Task RefreshEntitlementWithBackoffAsync_returns_first_acceptable_result()
    {
        _tokens.Pair = PairExpiringAt(_time.GetUtcNow().AddHours(1));
        _entitlementClient.NextResult = new Entitlement(Tier.Pro, null,
            _time.GetUtcNow().AddMonths(1), null, 0);
        var svc = BuildService();

        var ent = await svc.RefreshEntitlementWithBackoffAsync(
            e => e.Tier == Tier.Pro, FastBackoff, CancellationToken.None);

        Assert.NotNull(ent);
        Assert.Equal(Tier.Pro, ent!.Tier);
        Assert.Equal(1, _entitlementClient.FetchCallCount);
    }

    [Fact]
    public async Task RefreshEntitlementWithBackoffAsync_keeps_retrying_until_predicate_matches()
    {
        _tokens.Pair = PairExpiringAt(_time.GetUtcNow().AddHours(1));
        // Devuelve tier=Trial las primeras 2 veces, después Pro.
        _entitlementClient.SetupSequence(
            new Entitlement(Tier.Trial, _time.GetUtcNow().AddDays(7), null, null, 0),
            new Entitlement(Tier.Trial, _time.GetUtcNow().AddDays(7), null, null, 0),
            new Entitlement(Tier.Pro, null, _time.GetUtcNow().AddMonths(1), null, 0));
        var svc = BuildService();

        var ent = await svc.RefreshEntitlementWithBackoffAsync(
            e => e.Tier == Tier.Pro, FastBackoff, CancellationToken.None);

        Assert.NotNull(ent);
        Assert.Equal(Tier.Pro, ent!.Tier);
        Assert.Equal(3, _entitlementClient.FetchCallCount);
    }

    [Fact]
    public async Task RefreshEntitlementWithBackoffAsync_exhausts_retries_returns_last_value()
    {
        _tokens.Pair = PairExpiringAt(_time.GetUtcNow().AddHours(1));
        _entitlementClient.NextResult = new Entitlement(Tier.Trial,
            _time.GetUtcNow().AddDays(7), null, null, 0);
        var svc = BuildService();

        var ent = await svc.RefreshEntitlementWithBackoffAsync(
            e => e.Tier == Tier.Pro, FastBackoff, CancellationToken.None);

        Assert.NotNull(ent);
        Assert.Equal(Tier.Trial, ent!.Tier);
        Assert.Equal(FastBackoff.Count, _entitlementClient.FetchCallCount);
    }

    [Fact]
    public async Task RefreshEntitlementWithBackoffAsync_returns_null_when_no_session()
    {
        var svc = BuildService();

        var ent = await svc.RefreshEntitlementWithBackoffAsync(
            _ => true, FastBackoff, CancellationToken.None);

        Assert.Null(ent);
        Assert.Equal(0, _entitlementClient.FetchCallCount);
    }

    [Fact]
    public async Task RefreshEntitlementWithBackoffAsync_returns_early_on_cancellation()
    {
        _tokens.Pair = PairExpiringAt(_time.GetUtcNow().AddHours(1));
        _entitlementClient.NextResult = new Entitlement(Tier.Trial,
            _time.GetUtcNow().AddDays(7), null, null, 0);
        var svc = BuildService();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var ent = await svc.RefreshEntitlementWithBackoffAsync(
            e => e.Tier == Tier.Pro, FastBackoff, cts.Token);

        Assert.Null(ent);
        Assert.Equal(0, _entitlementClient.FetchCallCount);
    }

    // ────────────────────────────────────── Helpers / Fakes ─────────────────────

    // EP-11.8 — JWT mínimo construido a mano para tests del offline fallback. Solo
    // poblamos las claims que JwtClaimsExtractor consume (sub + email). La firma es
    // un placeholder — JwtClaimsExtractor no la verifica (eso lo haría el server).
    private static class TestJwt
    {
        public static string Build(string sub, string email)
        {
            static string B64Url(string s)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(s);
                return Convert.ToBase64String(bytes)
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }

            var header = B64Url("{\"alg\":\"HS256\",\"typ\":\"JWT\"}");
            var payload = B64Url($"{{\"sub\":\"{sub}\",\"email\":\"{email}\"}}");
            var signature = "fake-signature";
            return $"{header}.{payload}.{signature}";
        }
    }

    private sealed class FakeTokenStore : IAuthTokenStore
    {
        public AccessTokenPair? Pair { get; set; }
        public AccessTokenPair? Read() => Pair;
        public void Write(AccessTokenPair pair) => Pair = pair;
        public void Clear() => Pair = null;
    }

    private sealed class FakeEntitlementCache : IEntitlementCache
    {
        public Entitlement? LastWrite { get; set; }
        public bool Cleared { get; private set; }

        // EP-11.8: simulación del cache para el fallback offline. Por default null
        // (cache caducó o nunca existió → fallback debería rechazar). Los tests que
        // quieren validar el path "offline activado" setean esto a un Entitlement.
        public Entitlement? OfflineFallbackEntitlement { get; set; }

        public Entitlement? ReadFresh() => LastWrite;
        public Entitlement? ReadStale() => LastWrite;
        public Entitlement? ReadStaleWithin(TimeSpan maxAge) => OfflineFallbackEntitlement;
        public void Write(Entitlement entitlement) { LastWrite = entitlement; Cleared = false; }
        public void Clear() { LastWrite = null; Cleared = true; }
    }

    private sealed class FakeAuthClient : ISupabaseAuthClient
    {
        public UserProfile? ProfileForValidate { get; set; }
        public Exception? ValidateThrows { get; set; }
        public AccessTokenPair? NextRefreshResult { get; set; }
        public Exception? RefreshThrows { get; set; }
        public string? LastValidatedToken { get; private set; }
        public int RefreshCallCount { get; private set; }
        public int LogoutCallCount { get; private set; }

        public Task<UserProfile> ValidateAccessTokenAsync(string accessToken, CancellationToken ct)
        {
            LastValidatedToken = accessToken;
            if (ValidateThrows is not null) throw ValidateThrows;
            return Task.FromResult(ProfileForValidate
                ?? throw new InvalidOperationException("Test no seteó ProfileForValidate"));
        }

        public Task<AccessTokenPair> RefreshAsync(string refreshToken, CancellationToken ct)
        {
            RefreshCallCount++;
            if (RefreshThrows is not null) throw RefreshThrows;
            return Task.FromResult(NextRefreshResult
                ?? throw new InvalidOperationException("Test no seteó NextRefreshResult"));
        }

        public Task LogoutAsync(string accessToken, CancellationToken ct)
        {
            LogoutCallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEntitlementClient : ISupabaseEntitlementClient
    {
        public Entitlement? NextResult { get; set; }
        public Exception? NextException { get; set; }
        public int FetchCallCount { get; private set; }

        private Queue<Entitlement>? _sequence;

        public void SetupSequence(params Entitlement[] sequence)
        {
            _sequence = new Queue<Entitlement>(sequence);
        }

        public Task<Entitlement> FetchAsync(string accessToken, CancellationToken ct)
        {
            FetchCallCount++;
            if (NextException is not null) throw NextException;
            if (_sequence is not null && _sequence.Count > 0)
            {
                return Task.FromResult(_sequence.Dequeue());
            }
            return Task.FromResult(NextResult
                ?? throw new InvalidOperationException("Test no seteó NextResult ni NextException"));
        }
    }

    private sealed class FakeBrowser : IBrowserLauncher
    {
        public string? LastUrl { get; private set; }
        public void Open(string url) => LastUrl = url;
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
