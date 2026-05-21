using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Services.Auth;
using Spikit.ViewModels.Auth;

namespace Spikit.Tests.ViewModels.Auth;

public class LoginViewModelTests
{
    // Pre-canned objects reusables
    private static readonly UserProfile SampleProfile = new("user-1", "nacho@spikit.dev");

    private static Entitlement SampleEntitlement() => new(
        Tier.Pro, null,
        ProRenewsAt: new DateTimeOffset(2026, 06, 19, 0, 0, 0, TimeSpan.Zero),
        ByokGraceEndsAt: null,
        MinutesUsedPeriod: 0);

    private static LoginViewModel MakeVm(FakeAuthService? auth = null) =>
        new(auth ?? new FakeAuthService(), NullLogger<LoginViewModel>.Instance);

    // ────────────────────────── Bootstrap + EnterIdle ──────────────────────────

    [Fact]
    public void Bootstrap_starts_in_Idle_FirstLaunch()
    {
        var vm = MakeVm();

        Assert.Equal(LoginState.Idle, vm.State);
        Assert.Equal(LoginIdleVariant.FirstLaunch, vm.IdleVariant);
        Assert.True(vm.IsIdle);
        Assert.False(vm.IsWaitingForMagicLink);
        Assert.Contains("magic link", vm.IdleBodyText);
    }

    [Fact]
    public void EnterIdle_SessionExpired_swaps_body_copy_only()
    {
        var vm = MakeVm();

        vm.EnterIdle(LoginIdleVariant.SessionExpired);

        Assert.Equal(LoginState.Idle, vm.State);
        Assert.Equal(LoginIdleVariant.SessionExpired, vm.IdleVariant);
        Assert.StartsWith("Tu sesión expiró", vm.IdleBodyText);
    }

    [Fact]
    public async Task EnterIdle_resets_error_state_and_support_hint()
    {
        // Forzar 3 errores acumulados via HandleAuthCallback fallido x3
        var auth = new FakeAuthService
        {
            NextCallbackResult = new AuthCallbackResult(false, null, null, "Magic link expired"),
        };
        var vm = MakeVm(auth);

        for (int i = 0; i < 3; i++)
        {
            await vm.HandleAuthCallbackAsync(EmptyParams(), CancellationToken.None);
        }
        Assert.True(vm.ShowSupportHint);

        vm.EnterIdle();

        Assert.False(vm.ShowSupportHint);
        Assert.Null(vm.ErrorReason);
    }

    // ────────────────────────── HandleAuthPending ──────────────────────────────

    [Theory]
    [InlineData("nacho@spikit.dev")]
    [InlineData("user+tag@example.com")]
    [InlineData("name.surname@sub.dom.tld")]
    public void HandleAuthPending_with_valid_email_transitions_to_waiting_and_seeds_cooldown(string email)
    {
        var vm = MakeVm();

        vm.HandleAuthPending(email);

        Assert.Equal(LoginState.WaitingForMagicLink, vm.State);
        Assert.Equal(email, vm.PendingEmail);
        Assert.Equal(60, vm.ResendCooldownSec);
        Assert.True(vm.IsResendCoolingDown);
        Assert.Equal("Reenviar en 60s", vm.ResendButtonLabel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-arroba")]
    [InlineData("two@@signs.com")]
    [InlineData("trailing.dot@dom.")]
    [InlineData("a@b.x")]                    // TLD < 2
    [InlineData("<script>@evil.com")]        // injection-flavored bait
    public void HandleAuthPending_with_invalid_email_is_ignored(string? email)
    {
        var vm = MakeVm();
        vm.EnterIdle(LoginIdleVariant.FirstLaunch);

        vm.HandleAuthPending(email);

        Assert.Equal(LoginState.Idle, vm.State);
        Assert.Null(vm.PendingEmail);
    }

    [Fact]
    public void Resend_button_label_updates_after_TickCooldown()
    {
        var vm = MakeVm();
        vm.HandleAuthPending("nacho@spikit.dev");

        for (int i = 0; i < 5; i++) vm.TickCooldown();

        Assert.Equal(55, vm.ResendCooldownSec);
        Assert.Equal("Reenviar en 55s", vm.ResendButtonLabel);
    }

    [Fact]
    public void TickCooldown_until_zero_resets_button_label_and_enables_resend()
    {
        var vm = MakeVm();
        vm.HandleAuthPending("nacho@spikit.dev");

        for (int i = 0; i < 60; i++) vm.TickCooldown();

        Assert.Equal(0, vm.ResendCooldownSec);
        Assert.False(vm.IsResendCoolingDown);
        Assert.Equal("Reenviar email", vm.ResendButtonLabel);
        Assert.True(vm.ResendMagicLinkCommand.CanExecute(null));
    }

    // ────────────────────────── HandleAuthCallbackAsync (success) ──────────────

    [Fact]
    public async Task HandleAuthCallbackAsync_success_runs_through_states_to_RequestClose()
    {
        var auth = new FakeAuthService
        {
            NextCallbackResult = new AuthCallbackResult(true, SampleProfile, SampleEntitlement(), null),
        };
        var vm = MakeVm(auth);
        var closeRequested = false;
        vm.RequestClose += (_, _) => closeRequested = true;

        await vm.HandleAuthCallbackAsync(SampleCallbackParams(), CancellationToken.None);

        // Tras completar el flow exitoso (Validating → LoadingEntitlement → Success →
        // beat 200ms → RequestClose), el state final visible es Success.
        Assert.Equal(LoginState.Success, vm.State);
        Assert.True(closeRequested);
        Assert.Equal(1, auth.HandleAuthCallbackCount);
        Assert.Equal("abc", auth.LastCallbackParams!["access_token"]);
    }

    [Fact]
    public async Task HandleAuthCallbackAsync_success_resets_consecutive_errors()
    {
        var auth = new FakeAuthService
        {
            NextCallbackResult = new AuthCallbackResult(false, null, null, "Magic link expired"),
        };
        var vm = MakeVm(auth);
        // 3 errores acumulados → ShowSupportHint=true
        for (int i = 0; i < 3; i++)
        {
            await vm.HandleAuthCallbackAsync(EmptyParams(), CancellationToken.None);
        }
        Assert.True(vm.ShowSupportHint);

        // Ahora éxito → debería resetear
        auth.NextCallbackResult = new AuthCallbackResult(true, SampleProfile, SampleEntitlement(), null);
        await vm.HandleAuthCallbackAsync(SampleCallbackParams(), CancellationToken.None);

        Assert.False(vm.ShowSupportHint);
    }

    // ────────────────────────── HandleAuthCallbackAsync (errores) ──────────────

    [Fact]
    public async Task HandleAuthCallbackAsync_invalid_token_result_transitions_to_ErrorValidating()
    {
        var auth = new FakeAuthService
        {
            NextCallbackResult = new AuthCallbackResult(false, null, null, "Magic link expired"),
        };
        var vm = MakeVm(auth);

        await vm.HandleAuthCallbackAsync(EmptyParams(), CancellationToken.None);

        Assert.Equal(LoginState.ErrorValidating, vm.State);
        Assert.Equal("Magic link expired", vm.ErrorReason);
        Assert.False(vm.ShowSupportHint);   // 1 error solo
    }

    [Fact]
    public async Task HandleAuthCallbackAsync_three_consecutive_validation_errors_show_support_hint()
    {
        var auth = new FakeAuthService
        {
            NextCallbackResult = new AuthCallbackResult(false, null, null, "Magic link expired"),
        };
        var vm = MakeVm(auth);

        for (int i = 0; i < 3; i++)
        {
            await vm.HandleAuthCallbackAsync(EmptyParams(), CancellationToken.None);
        }

        Assert.True(vm.ShowSupportHint);
    }

    [Fact]
    public async Task HandleAuthCallbackAsync_HttpRequestException_transitions_to_ErrorNetwork()
    {
        var auth = new FakeAuthService
        {
            HandleAuthCallbackThrows = new HttpRequestException("simulated DNS fail"),
        };
        var vm = MakeVm(auth);

        await vm.HandleAuthCallbackAsync(EmptyParams(), CancellationToken.None);

        Assert.Equal(LoginState.ErrorNetwork, vm.State);
    }

    [Fact]
    public async Task HandleAuthCallbackAsync_AuthException_transitions_to_ErrorNetwork()
    {
        var auth = new FakeAuthService
        {
            HandleAuthCallbackThrows = new AuthException("supabase 5xx"),
        };
        var vm = MakeVm(auth);

        await vm.HandleAuthCallbackAsync(EmptyParams(), CancellationToken.None);

        Assert.Equal(LoginState.ErrorNetwork, vm.State);
    }

    // Cobertura de "rate limit (429)" del AC del ticket. El SupabaseAuthClient mapea
    // 429 a AuthException con mensaje específico; desde el punto de vista del VM, eso
    // entra por el mismo catch que cualquier AuthException → ErrorNetwork (la red está
    // ok pero el server pidió esperar). El VM no distingue 429 de 5xx — ambos son
    // "transient, reintentá". Sí distingue de 401/403 (que vienen como result.Success=false
    // con ErrorReason específico, mapeados a ErrorValidating). Test documenta esa intención.
    [Fact]
    public async Task HandleAuthCallbackAsync_rate_limit_429_treated_as_network_transient()
    {
        var auth = new FakeAuthService
        {
            // SupabaseAuthClient envuelve 429 en AuthException — el VM no necesita
            // saber el detalle, sólo que es transient.
            HandleAuthCallbackThrows = new AuthException("Rate limit (429)"),
        };
        var vm = MakeVm(auth);

        await vm.HandleAuthCallbackAsync(EmptyParams(), CancellationToken.None);

        Assert.Equal(LoginState.ErrorNetwork, vm.State);
    }

    // ────────────────────────── Suscripción a StateChanged ─────────────────────

    [Fact]
    public void OnAuthStateChanged_with_LoggedIn_externally_requests_close()
    {
        var auth = new FakeAuthService();
        var vm = MakeVm(auth);
        var closeRequested = false;
        vm.RequestClose += (_, _) => closeRequested = true;

        auth.SetLoggedInForTest();   // dispara StateChanged

        Assert.True(closeRequested);
    }

    [Fact]
    public async Task OnAuthStateChanged_during_handling_callback_does_not_duplicate_close()
    {
        // Caso edge: el AuthService.HandleAuthCallback internamente dispara StateChanged
        // al setear LoggedIn. El handler de StateChanged del VM debe ignorar ese cambio
        // (el VM ya está manejando el flow y va a disparar RequestClose por sí mismo
        // al terminar el microflash). Sin esto, RequestClose se dispara dos veces y la
        // window cierra dos veces (la segunda falla con InvalidOperationException).
        var auth = new FakeAuthService
        {
            NextCallbackResult = new AuthCallbackResult(true, SampleProfile, SampleEntitlement(), null),
            FireStateChangedDuringCallback = true,
        };
        var vm = MakeVm(auth);
        var closeCount = 0;
        vm.RequestClose += (_, _) => closeCount++;

        await vm.HandleAuthCallbackAsync(SampleCallbackParams(), CancellationToken.None);

        Assert.Equal(1, closeCount);
    }

    // ────────────────────────── StartOver / RetryNetwork ───────────────────────

    [Fact]
    public void StartOver_from_WaitingForMagicLink_clears_pending_email_and_returns_to_Idle()
    {
        var vm = MakeVm();
        vm.HandleAuthPending("nacho@spikit.dev");
        Assert.Equal(LoginState.WaitingForMagicLink, vm.State);

        vm.StartOverCommand.Execute(null);

        Assert.Equal(LoginState.Idle, vm.State);
        Assert.Equal(LoginIdleVariant.FirstLaunch, vm.IdleVariant);
        Assert.Null(vm.PendingEmail);
        Assert.Equal(0, vm.ResendCooldownSec);
    }

    [Fact]
    public async Task RetryNetworkCommand_from_ErrorNetwork_returns_to_Idle()
    {
        var auth = new FakeAuthService
        {
            HandleAuthCallbackThrows = new HttpRequestException("simulated"),
        };
        var vm = MakeVm(auth);
        await vm.HandleAuthCallbackAsync(EmptyParams(), CancellationToken.None);
        Assert.Equal(LoginState.ErrorNetwork, vm.State);

        vm.RetryNetworkCommand.Execute(null);

        Assert.Equal(LoginState.Idle, vm.State);
    }

    // ────────────────────────── Watchdog / Caption switch ──────────────────────

    [Fact]
    public void TriggerWatchdog_during_WaitingForMagicLink_returns_to_Idle()
    {
        var vm = MakeVm();
        vm.HandleAuthPending("nacho@spikit.dev");
        Assert.Equal(LoginState.WaitingForMagicLink, vm.State);

        vm.TriggerWatchdog();

        Assert.Equal(LoginState.Idle, vm.State);
    }

    [Fact]
    public void TriggerWatchdog_in_other_states_is_no_op()
    {
        var vm = MakeVm();   // arranca en Idle

        vm.TriggerWatchdog();

        Assert.Equal(LoginState.Idle, vm.State);   // no cambia nada (no había Waiting)
    }

    [Fact]
    public void SwitchToSlowValidatingCaption_changes_caption_only()
    {
        var vm = MakeVm();
        var defaultCaption = vm.ValidatingCaption;

        vm.SwitchToSlowValidatingCaption();

        Assert.NotEqual(defaultCaption, vm.ValidatingCaption);
        Assert.Contains("tardando", vm.ValidatingCaption);
    }

    // ────────────────────────── AuthPendingReceived event (EP-11.4) ───────────

    [Fact]
    public void OnAuthPendingReceived_with_valid_email_transitions_to_waiting()
    {
        // El SpikitUriDispatcher dispara IAuthService.AuthPendingReceived al recibir
        // spikit://auth-pending. El LoginVM está suscrito y debe mutar al estado 0.2.
        var auth = new FakeAuthService();
        var vm = MakeVm(auth);

        auth.RaiseAuthPendingReceived("nacho@spikit.dev");

        Assert.Equal(LoginState.WaitingForMagicLink, vm.State);
        Assert.Equal("nacho@spikit.dev", vm.PendingEmail);
    }

    [Fact]
    public void OnAuthPendingReceived_with_invalid_email_is_ignored()
    {
        var auth = new FakeAuthService();
        var vm = MakeVm(auth);

        auth.RaiseAuthPendingReceived("not-an-email");

        Assert.Equal(LoginState.Idle, vm.State);
        Assert.Null(vm.PendingEmail);
    }

    [Fact]
    public void Dispose_unsubscribes_from_AuthPendingReceived()
    {
        var auth = new FakeAuthService();
        var vm = MakeVm(auth);

        vm.Dispose();
        auth.RaiseAuthPendingReceived("nacho@spikit.dev");

        // Tras Dispose, el evento ya no debería mutar el VM.
        Assert.Null(vm.PendingEmail);
    }

    // ────────────────────────── Dispose ────────────────────────────────────────

    [Fact]
    public void Dispose_unsubscribes_from_StateChanged()
    {
        var auth = new FakeAuthService();
        var vm = MakeVm(auth);
        var closeRequested = false;
        vm.RequestClose += (_, _) => closeRequested = true;

        vm.Dispose();
        auth.SetLoggedInForTest();   // ya no debería propagarse

        Assert.False(closeRequested);
    }

    // ────────────────────────── Helpers ────────────────────────────────────────

    private static IReadOnlyDictionary<string, string> EmptyParams() =>
        new Dictionary<string, string>();

    private static IReadOnlyDictionary<string, string> SampleCallbackParams() =>
        new Dictionary<string, string>
        {
            ["access_token"] = "abc",
            ["refresh_token"] = "def",
            ["expires_in"] = "3600",
        };

    // ────────────────────────── Fake IAuthService ──────────────────────────────
    //
    // Implementación local (no compartida con AuthServiceTests / SpikitUriDispatcherTests
    // porque cada test suite tiene matices distintos sobre qué métodos exercise). Soporta:
    //   - Setear NextCallbackResult / HandleAuthCallbackThrows para simular escenarios.
    //   - Disparar StateChanged externamente vía SetLoggedInForTest() — necesario para
    //     testear el caso "alguien resuelve la sesión por afuera" del VM.
    //   - FireStateChangedDuringCallback=true: dispara StateChanged adentro del
    //     HandleAuthCallback, simulando lo que hace el AuthService real al setear LoggedIn.

    private sealed class FakeAuthService : IAuthService
    {
        private AuthSessionState _state = AuthSessionState.LoggedOut;

        public AuthSessionState State => _state;
        public UserProfile? CurrentProfile { get; set; }
        public Entitlement? CurrentEntitlement { get; set; }
        public bool IsOfflineMode => false;
        public AuthInitOutcome LastInitializeOutcome => AuthInitOutcome.NotRun;

        public event EventHandler? StateChanged;
        public event EventHandler<string>? AuthPendingReceived;

        public void RaiseAuthPendingReceived(string email) =>
            AuthPendingReceived?.Invoke(this, email);

        public AuthCallbackResult? NextCallbackResult { get; set; }
        public Exception? HandleAuthCallbackThrows { get; set; }
        public bool FireStateChangedDuringCallback { get; set; }
        public int HandleAuthCallbackCount { get; private set; }
        public IReadOnlyDictionary<string, string>? LastCallbackParams { get; private set; }

        public int StartLoginCount { get; private set; }
        public int LogoutCount { get; private set; }

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StartLoginAsync(CancellationToken ct)
        {
            StartLoginCount++;
            return Task.CompletedTask;
        }

        public Task<AuthCallbackResult> HandleAuthCallbackAsync(
            IReadOnlyDictionary<string, string> queryParams, CancellationToken ct)
        {
            HandleAuthCallbackCount++;
            LastCallbackParams = queryParams;

            if (HandleAuthCallbackThrows is not null) throw HandleAuthCallbackThrows;

            var result = NextCallbackResult
                ?? new AuthCallbackResult(false, null, null, "Test no seteó NextCallbackResult");

            if (result.Success && FireStateChangedDuringCallback)
            {
                _state = AuthSessionState.LoggedIn;
                CurrentProfile = result.Profile;
                CurrentEntitlement = result.Entitlement;
                StateChanged?.Invoke(this, EventArgs.Empty);
            }

            return Task.FromResult(result);
        }

        public Task LogoutAsync(CancellationToken ct)
        {
            LogoutCount++;
            _state = AuthSessionState.LoggedOut;
            CurrentProfile = null;
            CurrentEntitlement = null;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task<string?> GetCurrentAccessTokenAsync(CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task<string?> ForceRefreshAccessTokenAsync(CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task<Entitlement?> RefreshEntitlementAsync(CancellationToken ct) =>
            Task.FromResult<Entitlement?>(null);

        public Task<Entitlement?> RefreshEntitlementWithBackoffAsync(
            Func<Entitlement, bool> isAcceptable, CancellationToken ct) =>
            Task.FromResult<Entitlement?>(null);

        public void SetLoggedInForTest()
        {
            _state = AuthSessionState.LoggedIn;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
