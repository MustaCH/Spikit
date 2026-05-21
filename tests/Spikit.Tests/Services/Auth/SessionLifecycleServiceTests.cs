using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Models;
using Spikit.Services.Auth;
using Spikit.Services.Hotkey;
using Spikit.Services.Orchestration;
using Spikit.Services.Tray;

namespace Spikit.Tests.Services.Auth;

// Tests del orquestador de logout EP-11.7. Cubre orden de cleanup, robustez ante
// fallas parciales, y comportamiento durante dictado activo. El cierre de ventanas
// + transición a LoginWindow lo maneja App.xaml.cs reaccionando al StateChanged que
// dispara IAuthService.LogoutAsync — esa parte se cubre por smoke manual (depende
// de Application.Current y no es razonable unit-testear con WPF dispatchers).
public class SessionLifecycleServiceTests
{
    private readonly FakeAuth _auth = new();
    private readonly FakeOrchestrator _orchestrator = new();
    private readonly FakeHotkey _hotkey = new();
    private readonly FakeTray _tray = new();
    private readonly List<string> _callOrder = new();

    private SessionLifecycleService BuildSut()
    {
        _orchestrator.CallOrder = _callOrder;
        _hotkey.CallOrder = _callOrder;
        _tray.CallOrder = _callOrder;
        _auth.CallOrder = _callOrder;

        return new SessionLifecycleService(
            _auth, _orchestrator, _hotkey, _tray,
            NullLogger<SessionLifecycleService>.Instance);
    }

    // ─────────────────── Orden de cleanup ────────────────────────────────────────

    [Fact]
    public async Task LogoutAsync_invokes_cleanup_in_correct_order()
    {
        var sut = BuildSut();

        await sut.LogoutAsync(CancellationToken.None);

        // Orden esperado: cancelar dictado → stop orchestrator → unregister hotkey
        // (principal + cancel) → shutdown tray → auth.logout. El último paso debe
        // ser auth.LogoutAsync porque dispara StateChanged y App.xaml.cs asume "todo
        // lo runtime ya está apagado".
        Assert.Equal(new[]
        {
            "orchestrator.CancelActiveSessionAsync",
            "orchestrator.Stop",
            "hotkey.Unregister",
            "hotkey.UnregisterCancelHotkey",
            "tray.Shutdown",
            "auth.LogoutAsync",
        }, _callOrder);
    }

    [Fact]
    public async Task LogoutAsync_cancels_active_dictation_before_stopping_orchestrator()
    {
        // Aunque no haya sesión activa siempre se invoca CancelActiveSessionAsync
        // (es no-op cuando estamos en Idle, mucho más simple que duplicar el chequeo
        // de estado acá). Lo importante es que CancelActiveSession SIEMPRE va antes
        // de Stop — Stop desuscribe los handlers, lo que dejaría una sesión en vuelo
        // sin nadie escuchando el release del audio.
        var sut = BuildSut();
        _orchestrator.SimulateActiveSession = true;

        await sut.LogoutAsync(CancellationToken.None);

        var cancelIndex = _callOrder.IndexOf("orchestrator.CancelActiveSessionAsync");
        var stopIndex = _callOrder.IndexOf("orchestrator.Stop");
        Assert.True(cancelIndex >= 0 && stopIndex >= 0);
        Assert.True(cancelIndex < stopIndex,
            "CancelActiveSessionAsync debe ir ANTES de Stop");
        Assert.True(_orchestrator.SessionWasCancelledWhileActive,
            "Si había sesión activa, debió ser cancelada");
    }

    [Fact]
    public async Task LogoutAsync_auth_logout_is_last_step()
    {
        // Hipótesis crítica del flow: el handler de App.xaml.cs reacciona al
        // StateChanged disparado por auth.LogoutAsync y asume que los servicios
        // runtime ya están apagados. Si auth.LogoutAsync corriera primero, el
        // handler de App podría intentar cerrar ventanas mientras la pill todavía
        // recibe StateChanged del orchestrator.
        var sut = BuildSut();

        await sut.LogoutAsync(CancellationToken.None);

        Assert.Equal("auth.LogoutAsync", _callOrder.Last());
    }

    // ─────────────────── Robustez ante fallas ────────────────────────────────────

    [Fact]
    public async Task LogoutAsync_continues_when_CancelActiveSession_throws()
    {
        var sut = BuildSut();
        _orchestrator.NextCancelException =
            new InvalidOperationException("audio engine murio");

        await sut.LogoutAsync(CancellationToken.None);

        Assert.True(_orchestrator.StopCalled, "Stop debe correr aunque Cancel haya tirado");
        Assert.True(_auth.LogoutCalled, "auth.LogoutAsync debe correr aunque Cancel haya tirado");
    }

    [Fact]
    public async Task LogoutAsync_continues_when_orchestrator_Stop_throws()
    {
        var sut = BuildSut();
        _orchestrator.NextStopException = new InvalidOperationException("stop fallo");

        await sut.LogoutAsync(CancellationToken.None);

        Assert.True(_hotkey.UnregisterCalled);
        Assert.True(_tray.ShutdownCalled);
        Assert.True(_auth.LogoutCalled);
    }

    [Fact]
    public async Task LogoutAsync_continues_when_hotkey_Unregister_throws()
    {
        var sut = BuildSut();
        _hotkey.NextUnregisterException = new InvalidOperationException("win32 broken");

        await sut.LogoutAsync(CancellationToken.None);

        Assert.True(_tray.ShutdownCalled);
        Assert.True(_auth.LogoutCalled);
    }

    [Fact]
    public async Task LogoutAsync_continues_when_tray_Shutdown_throws()
    {
        var sut = BuildSut();
        _tray.NextShutdownException = new InvalidOperationException("HICON leak");

        await sut.LogoutAsync(CancellationToken.None);

        Assert.True(_auth.LogoutCalled,
            "auth.LogoutAsync debe correr aunque tray.Shutdown haya tirado — limpiar tokens es el intent último");
    }

    [Fact]
    public async Task LogoutAsync_propagates_when_auth_Logout_throws()
    {
        // auth.LogoutAsync sí propaga: es el ÚLTIMO paso y los anteriores son
        // best-effort. Si auth falló, el caller (PlanSectionViewModel) muestra
        // un ErrorMessage. Los servicios runtime ya están apagados — la app está
        // en estado "logout parcial" hasta que el user reintente.
        var sut = BuildSut();
        _auth.NextLogoutException = new InvalidOperationException("server down");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.LogoutAsync(CancellationToken.None));

        // Y sin embargo los servicios runtime se apagaron.
        Assert.True(_orchestrator.StopCalled);
        Assert.True(_hotkey.UnregisterCalled);
        Assert.True(_tray.ShutdownCalled);
    }

    // ─────────────────── Fakes ────────────────────────────────────────────────────

    private sealed class FakeOrchestrator : IDictationLifecycle
    {
        public List<string>? CallOrder { get; set; }
        public bool SimulateActiveSession { get; set; }
        public bool SessionWasCancelledWhileActive { get; private set; }
        public bool StopCalled { get; private set; }
        public Exception? NextCancelException { get; set; }
        public Exception? NextStopException { get; set; }

        public Task CancelActiveSessionAsync()
        {
            CallOrder?.Add("orchestrator.CancelActiveSessionAsync");
            if (SimulateActiveSession)
            {
                SessionWasCancelledWhileActive = true;
                SimulateActiveSession = false;
            }
            if (NextCancelException is not null) throw NextCancelException;
            return Task.CompletedTask;
        }

        public void Stop()
        {
            CallOrder?.Add("orchestrator.Stop");
            StopCalled = true;
            if (NextStopException is not null) throw NextStopException;
        }
    }

    private sealed class FakeHotkey : IHotkeyService
    {
        public List<string>? CallOrder { get; set; }
        public bool UnregisterCalled { get; private set; }
        public Exception? NextUnregisterException { get; set; }

        public HotkeyDefinition? CurrentRegistration => null;
        public event EventHandler? HotkeyPressed { add { } remove { } }
        public event EventHandler? HotkeyReleased { add { } remove { } }
        public event EventHandler? CancelHotkeyPressed { add { } remove { } }
        public bool IsPaused => false;
        public event EventHandler? PausedChanged { add { } remove { } }

        public void Register(HotkeyDefinition definition) { }

        public void Unregister()
        {
            CallOrder?.Add("hotkey.Unregister");
            UnregisterCalled = true;
            if (NextUnregisterException is not null) throw NextUnregisterException;
        }
        public void SetPaused(bool paused) { }
        public void TriggerManualPress() { }
        public void RegisterCancelHotkey() { }
        public void UnregisterCancelHotkey() => CallOrder?.Add("hotkey.UnregisterCancelHotkey");
        public void SuspendForCapture() { }
        public void ResumeFromCapture() { }
        public void Dispose() { }
    }

    private sealed class FakeTray : ITrayIconService
    {
        public List<string>? CallOrder { get; set; }
        public bool ShutdownCalled { get; private set; }
        public Exception? NextShutdownException { get; set; }

        public void Initialize() { }
        public void Shutdown()
        {
            CallOrder?.Add("tray.Shutdown");
            ShutdownCalled = true;
            if (NextShutdownException is not null) throw NextShutdownException;
        }
        public void Dispose() { }
    }

    private sealed class FakeAuth : IAuthService
    {
        public List<string>? CallOrder { get; set; }
        public bool LogoutCalled { get; private set; }
        public Exception? NextLogoutException { get; set; }

        public AuthSessionState State { get; private set; } = AuthSessionState.LoggedIn;
        public UserProfile? CurrentProfile { get; private set; } = new("u1", "test@spikit.dev");
        public Entitlement? CurrentEntitlement { get; private set; }
        public bool IsOfflineMode => false;
        public AuthInitOutcome LastInitializeOutcome => AuthInitOutcome.NotRun;
        public event EventHandler? StateChanged;
        public event EventHandler<string>? AuthPendingReceived { add { } remove { } }

        public void RaiseAuthPendingReceived(string email) { }

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StartLoginAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<AuthCallbackResult> HandleAuthCallbackAsync(
            IReadOnlyDictionary<string, string> queryParams, CancellationToken ct) =>
            Task.FromResult(new AuthCallbackResult(false, null, null, null));

        public Task LogoutAsync(CancellationToken ct)
        {
            CallOrder?.Add("auth.LogoutAsync");
            LogoutCalled = true;
            if (NextLogoutException is not null) throw NextLogoutException;
            State = AuthSessionState.LoggedOut;
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
            Task.FromResult(CurrentEntitlement);
        public Task<Entitlement?> RefreshEntitlementWithBackoffAsync(
            Func<Entitlement, bool> isAcceptable, CancellationToken ct) =>
            Task.FromResult(CurrentEntitlement);
    }
}
