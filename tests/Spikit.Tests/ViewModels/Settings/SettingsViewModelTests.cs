using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Services.Auth;
using Spikit.ViewModels.Settings;
using Spikit.ViewModels.Settings.Sections;

namespace Spikit.Tests.ViewModels.Settings;

// Tests del shell SettingsViewModel — centrados en EP-11.6: visibilidad reactiva de la
// sección Provider según tier + auth state, y redirect a General cuando el tier deja de
// permitirla. Los section VMs son sealed y arrastran muchísimas dependencies; como el
// shell solo los toca para asignar properties y suscribirse a History.NavigateRequested,
// usamos RuntimeHelpers.GetUninitializedObject para crear instancias sin invocar sus
// ctors. El event += sobre History tolera el field nullable arrancando en null.
public class SettingsViewModelTests
{
    private readonly FakeAuth _auth = new();

    private SettingsViewModel BuildVm()
    {
        return new SettingsViewModel(
            NullLogger<SettingsViewModel>.Instance,
            _auth,
            Uninitialized<ProviderSectionViewModel>(),
            Uninitialized<HotkeySectionViewModel>(),
            Uninitialized<GeneralSectionViewModel>(),
            Uninitialized<AudioSectionViewModel>(),
            Uninitialized<PrivacySectionViewModel>(),
            Uninitialized<HistorySectionViewModel>(),
            Uninitialized<PlanSectionViewModel>(),
            Uninitialized<AboutSectionViewModel>());
    }

    private static T Uninitialized<T>() where T : class =>
        (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    // ─────────────────── IsProviderVisible: 4 combinaciones ──────────────────────

    [Fact]
    public void LoggedOut_hides_Provider()
    {
        var vm = BuildVm();

        Assert.Equal(AuthSessionState.LoggedOut, _auth.State);
        Assert.False(vm.IsProviderVisible);
    }

    [Fact]
    public void LoggedIn_with_Byok_shows_Provider()
    {
        _auth.SetLoggedIn(ByokEntitlement());
        var vm = BuildVm();

        Assert.True(vm.IsProviderVisible);
    }

    [Fact]
    public void LoggedIn_with_Trial_hides_Provider()
    {
        _auth.SetLoggedIn(TrialEntitlement());
        var vm = BuildVm();

        Assert.False(vm.IsProviderVisible);
    }

    [Fact]
    public void LoggedIn_with_Pro_hides_Provider()
    {
        _auth.SetLoggedIn(ProEntitlement());
        var vm = BuildVm();

        Assert.False(vm.IsProviderVisible);
    }

    [Fact]
    public void LoggedIn_with_Expired_hides_Provider()
    {
        _auth.SetLoggedIn(ExpiredEntitlement());
        var vm = BuildVm();

        Assert.False(vm.IsProviderVisible);
    }

    [Fact]
    public void LoggedIn_without_entitlement_hides_Provider()
    {
        _auth.SetLoggedIn(entitlement: null);
        var vm = BuildVm();

        Assert.False(vm.IsProviderVisible);
    }

    // ─────────────────── BYOK en grace 30d sigue mostrando Provider ──────────────

    [Fact]
    public void Byok_in_grace_still_shows_Provider()
    {
        // Durante el grace de 30d el Tier sigue siendo Byok hasta el expire (ADR-0007 § 2).
        // Confirmado con Nacho en sesión Architect 2026-05-20: la flag sigue true.
        var graceEnds = DateTimeOffset.UtcNow.AddDays(15);
        _auth.SetLoggedIn(new Entitlement(Tier.Byok, null, null, graceEnds, 0));
        var vm = BuildVm();

        Assert.True(vm.IsProviderVisible);
    }

    // ─────────────────── Reactividad: cambia tras StateChanged ───────────────────

    [Fact]
    public void IsProviderVisible_updates_when_auth_state_changes()
    {
        var vm = BuildVm();
        Assert.False(vm.IsProviderVisible);

        var notifiedKeys = new List<string>();
        vm.PropertyChanged += (_, e) => notifiedKeys.Add(e.PropertyName ?? "");

        _auth.SetLoggedIn(ByokEntitlement());

        Assert.True(vm.IsProviderVisible);
        Assert.Contains(nameof(SettingsViewModel.IsProviderVisible), notifiedKeys);
        Assert.Contains(nameof(SettingsViewModel.IsProviderSectionRendered), notifiedKeys);
    }

    [Fact]
    public void IsProviderVisible_flips_off_when_user_logs_out()
    {
        _auth.SetLoggedIn(ByokEntitlement());
        var vm = BuildVm();
        Assert.True(vm.IsProviderVisible);

        _auth.SetLoggedOut();

        Assert.False(vm.IsProviderVisible);
    }

    [Fact]
    public void IsProviderVisible_flips_off_when_Byok_grace_expires_to_Expired()
    {
        _auth.SetLoggedIn(ByokEntitlement());
        var vm = BuildVm();
        Assert.True(vm.IsProviderVisible);

        // Simulamos el daily_entitlement_sweep que mueve el tier de Byok (post-grace) a
        // Expired (ADR-0007 § 2).
        _auth.SetLoggedIn(ExpiredEntitlement());

        Assert.False(vm.IsProviderVisible);
    }

    // ─────────────────── Redirect automático a General ───────────────────────────

    [Fact]
    public void Redirects_to_General_when_tier_revokes_Provider_visibility()
    {
        _auth.SetLoggedIn(ByokEntitlement());
        var vm = BuildVm();
        vm.NavigateTo(SettingsSection.Provider);
        Assert.Equal(SettingsSection.Provider, vm.CurrentSection);

        // BYOK revocado → Expired (transición de revoke + grace expirado).
        _auth.SetLoggedIn(ExpiredEntitlement());

        Assert.Equal(SettingsSection.General, vm.CurrentSection);
        Assert.False(vm.IsProviderSectionRendered);
    }

    [Fact]
    public void Does_not_redirect_when_in_other_section_and_tier_revoked()
    {
        // Si el usuario estaba parado en Plan (o cualquier otra sección que no es Provider),
        // el cambio de tier no debe afectar CurrentSection.
        _auth.SetLoggedIn(ByokEntitlement());
        var vm = BuildVm();
        vm.NavigateTo(SettingsSection.Plan);

        _auth.SetLoggedIn(ExpiredEntitlement());

        Assert.Equal(SettingsSection.Plan, vm.CurrentSection);
    }

    [Fact]
    public void Does_not_redirect_when_in_Provider_and_tier_still_Byok()
    {
        _auth.SetLoggedIn(ByokEntitlement());
        var vm = BuildVm();
        vm.NavigateTo(SettingsSection.Provider);

        // Otro StateChanged sin cambiar el tier (ej. refresh de entitlement post-Stripe
        // que no afecta a BYOK). Si lo notificamos seguimos en Provider.
        _auth.SetLoggedIn(ByokEntitlement());

        Assert.Equal(SettingsSection.Provider, vm.CurrentSection);
        Assert.True(vm.IsProviderSectionRendered);
    }

    // ─────────────────── IsProviderSectionRendered: defensa en profundidad ───────

    [Fact]
    public void IsProviderSectionRendered_requires_both_section_and_tier()
    {
        _auth.SetLoggedIn(ByokEntitlement());
        var vm = BuildVm();

        // En General con BYOK: sección no renderizada (no es la activa).
        Assert.False(vm.IsProviderSectionRendered);

        // En Provider con BYOK: renderizada.
        vm.NavigateTo(SettingsSection.Provider);
        Assert.True(vm.IsProviderSectionRendered);
    }

    // ─────────────────── Dispose ─────────────────────────────────────────────────

    [Fact]
    public void Dispose_unsubscribes_from_auth_StateChanged()
    {
        var vm = BuildVm();
        vm.Dispose();

        var notified = false;
        vm.PropertyChanged += (_, _) => notified = true;

        _auth.SetLoggedIn(ByokEntitlement());

        Assert.False(notified, "Tras Dispose el VM no debería seguir reaccionando al StateChanged");
    }

    // ─────────────────── Helpers ─────────────────────────────────────────────────

    private static Entitlement TrialEntitlement() =>
        new(Tier.Trial, DateTimeOffset.UtcNow.AddDays(10), null, null, 0);

    private static Entitlement ProEntitlement() =>
        new(Tier.Pro, null, DateTimeOffset.UtcNow.AddMonths(1), null, 0);

    private static Entitlement ByokEntitlement() =>
        new(Tier.Byok, null, null, null, 0);

    private static Entitlement ExpiredEntitlement() =>
        new(Tier.Expired, null, null, null, 0);

    // Fake mínimo de IAuthService — solo lo necesario para los tests del shell (State +
    // CurrentEntitlement + StateChanged). El resto de la interface devuelve defaults.
    private sealed class FakeAuth : IAuthService
    {
        public AuthSessionState State { get; private set; } = AuthSessionState.LoggedOut;
        public UserProfile? CurrentProfile { get; private set; }
        public Entitlement? CurrentEntitlement { get; private set; }
        public bool IsOfflineMode => false;
        public AuthInitOutcome LastInitializeOutcome => AuthInitOutcome.NotRun;
        public event EventHandler? StateChanged;
        public event EventHandler<string>? AuthPendingReceived;

        public void RaiseAuthPendingReceived(string email) =>
            AuthPendingReceived?.Invoke(this, email);

        public void SetLoggedIn(Entitlement? entitlement)
        {
            State = AuthSessionState.LoggedIn;
            CurrentProfile = new UserProfile("u1", "test@spikit.dev");
            CurrentEntitlement = entitlement;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public void SetLoggedOut()
        {
            State = AuthSessionState.LoggedOut;
            CurrentProfile = null;
            CurrentEntitlement = null;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StartLoginAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<AuthCallbackResult> HandleAuthCallbackAsync(
            IReadOnlyDictionary<string, string> queryParams, CancellationToken ct) =>
            Task.FromResult(new AuthCallbackResult(false, null, null, null));

        public Task LogoutAsync(CancellationToken ct)
        {
            SetLoggedOut();
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
