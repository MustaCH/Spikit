using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Services.Auth;

namespace Spikit.Tests.Services.Auth;

// EP-11.8 — tests del background worker que sale del modo offline. Usamos un
// FakeTimeProvider que avanza manualmente para evitar esperar los 30s+ reales del
// schedule. El schedule también se inyecta corto (10ms) para que el loop progrese
// rápido sin pegarle al GetUtcNow real.
public class OfflineRefreshWorkerTests
{
    private readonly FakeAuthService _auth = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 05, 21, 12, 0, 0, TimeSpan.Zero));

    private static readonly IReadOnlyList<TimeSpan> TinySchedule = new[]
    {
        TimeSpan.FromMilliseconds(10),
        TimeSpan.FromMilliseconds(20),
        TimeSpan.FromMilliseconds(40),
        TimeSpan.FromMilliseconds(100),
    };

    private OfflineRefreshWorker BuildWorker() =>
        new(_auth, _time, TinySchedule, NullLogger<OfflineRefreshWorker>.Instance);

    [Fact]
    public async Task Stays_idle_when_auth_is_not_offline()
    {
        // Auth nunca está offline → worker no llama RefreshEntitlementAsync.
        _auth.IsOfflineMode = false;
        var worker = BuildWorker();
        using var cts = new CancellationTokenSource();

        var runTask = worker.StartAsync(cts.Token);
        await runTask;
        await Task.Delay(50);  // dejar tiempo a que el ExecuteAsync intente algo
        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, _auth.RefreshEntitlementCallCount);
    }

    [Fact]
    public async Task Retries_until_offline_mode_clears()
    {
        // Setup: auth arranca en offline. RefreshEntitlementAsync simula el flip
        // (apaga IsOfflineMode + retorna entitlement) en el 3er intento.
        _auth.IsOfflineMode = true;
        _auth.State = AuthSessionState.LoggedIn;
        _auth.RefreshEntitlementCallCount = 0;
        _auth.OnRefreshEntitlement = call =>
        {
            if (call >= 3)
            {
                _auth.IsOfflineMode = false;
                return new Entitlement(Tier.Pro, null, null, null, 0);
            }
            return null; // fail-silent, sigue offline
        };

        var worker = BuildWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await worker.StartAsync(cts.Token);
        await WaitUntilAsync(() => !_auth.IsOfflineMode, TimeSpan.FromSeconds(3));
        await worker.StopAsync(CancellationToken.None);

        Assert.False(_auth.IsOfflineMode);
        Assert.True(_auth.RefreshEntitlementCallCount >= 3,
            $"Esperaba >= 3 intentos antes del flip, hubo {_auth.RefreshEntitlementCallCount}");
    }

    [Fact]
    public async Task Stops_polling_when_state_becomes_LoggedOut_mid_retry()
    {
        // Edge case: el user hace logout durante el retry loop. La condición
        // `_auth.State != LoggedIn` debería cortar el loop sin más intentos.
        _auth.IsOfflineMode = true;
        _auth.State = AuthSessionState.LoggedIn;
        _auth.OnRefreshEntitlement = call =>
        {
            if (call == 1)
            {
                _auth.State = AuthSessionState.LoggedOut;
                _auth.IsOfflineMode = false; // logout limpia offline también
            }
            return null;
        };

        var worker = BuildWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await worker.StartAsync(cts.Token);
        await WaitUntilAsync(() => _auth.State == AuthSessionState.LoggedOut,
            TimeSpan.FromSeconds(2));
        await Task.Delay(100); // dejar margen para confirmar que no hay más intentos
        var countAfterLogout = _auth.RefreshEntitlementCallCount;
        await Task.Delay(150);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(countAfterLogout, _auth.RefreshEntitlementCallCount);
    }

    [Fact]
    public async Task Swallows_exceptions_during_refresh_and_keeps_retrying()
    {
        // Si RefreshEntitlementAsync tira (excepción no controlada), el worker debe
        // loguear + seguir al próximo tick — no morir.
        _auth.IsOfflineMode = true;
        _auth.State = AuthSessionState.LoggedIn;
        _auth.OnRefreshEntitlement = call =>
        {
            if (call < 2) throw new InvalidOperationException("boom");
            _auth.IsOfflineMode = false;
            return new Entitlement(Tier.Pro, null, null, null, 0);
        };

        var worker = BuildWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await worker.StartAsync(cts.Token);
        await WaitUntilAsync(() => !_auth.IsOfflineMode, TimeSpan.FromSeconds(3));
        await worker.StopAsync(CancellationToken.None);

        Assert.False(_auth.IsOfflineMode);
        Assert.True(_auth.RefreshEntitlementCallCount >= 2);
    }

    // Helpers ────────────────────────────────────────────────────────────────────

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    // Subset de IAuthService que el worker consume. Lo mantenemos mínimo para que
    // el test sea focalizado — el resto de la interface devuelve defaults.
    private sealed class FakeAuthService : IAuthService
    {
        public AuthSessionState State { get; set; } = AuthSessionState.LoggedIn;
        public UserProfile? CurrentProfile { get; set; }
        public Entitlement? CurrentEntitlement { get; set; }
        public bool IsOfflineMode { get; set; }
        public AuthInitOutcome LastInitializeOutcome { get; set; } = AuthInitOutcome.NotRun;

        public int RefreshEntitlementCallCount { get; set; }
        public Func<int, Entitlement?>? OnRefreshEntitlement { get; set; }

        public event EventHandler? StateChanged { add { } remove { } }
        public event EventHandler<string>? AuthPendingReceived { add { } remove { } }
        public void RaiseAuthPendingReceived(string email) { }

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StartLoginAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<AuthCallbackResult> HandleAuthCallbackAsync(
            IReadOnlyDictionary<string, string> queryParams, CancellationToken ct) =>
            Task.FromResult(new AuthCallbackResult(false, null, null, null));

        public Task LogoutAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string?> GetCurrentAccessTokenAsync(CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task<string?> ForceRefreshAccessTokenAsync(CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task<Entitlement?> RefreshEntitlementAsync(CancellationToken ct)
        {
            RefreshEntitlementCallCount++;
            var result = OnRefreshEntitlement?.Invoke(RefreshEntitlementCallCount);
            return Task.FromResult(result);
        }

        public Task<Entitlement?> RefreshEntitlementWithBackoffAsync(
            Func<Entitlement, bool> isAcceptable, CancellationToken ct) =>
            Task.FromResult<Entitlement?>(null);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
