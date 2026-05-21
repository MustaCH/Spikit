using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Models;
using Spikit.Services.Auth;
using Spikit.Services.Hotkey;
using Spikit.Services.Onboarding;
using Spikit.Services.Provider;
using Spikit.Services.Settings;
using Spikit.ViewModels.Onboarding;

namespace Spikit.Tests.ViewModels.Onboarding;

// Tests del shell + bifurcación por tier (EP-11.5 / ADR-0008 / design-system §10.13).
//
// Cobertura:
//   - Resolución del tier desde IAuthService al ctor (Byok/Trial/Pro/null → default Byok).
//   - Copy del Welcome bifurcado por tier (h1, sub-h, lista, tiempo).
//   - Stepper: visibility del 3er círculo + línea 2→3, labels "Paso N de M".
//   - Routing GoNext desde Welcome (BYOK → Provider, Trial/Pro → Hotkey saltea Provider).
//   - Routing GoBack desde Hotkey (BYOK → Provider, Trial/Pro → Welcome).
//
// NO cubre el SaveAsync de Provider/Hotkey (ya cubierto en ProviderStepViewModelTests +
// HotkeyStepViewModelTests). Acá testeamos solo la lógica nueva de la bifurcación.
public class OnboardingViewModelTests
{
    private static OnboardingViewModel MakeVm(Tier? tier = Tier.Byok)
    {
        var auth = new FakeAuthService();
        if (tier is not null)
        {
            auth.SetEntitlement(new Entitlement(tier.Value, null, null, null, 0));
        }
        return Build(auth);
    }

    private static OnboardingViewModel Build(FakeAuthService auth)
    {
        var provider = new ProviderStepViewModel(
            NullLogger<ProviderStepViewModel>.Instance,
            new FakeProviderTester(),
            new FakeProviderConfigWriter());
        var hotkey = new HotkeyStepViewModel(
            NullLogger<HotkeyStepViewModel>.Instance,
            new FakeHotkeyConfigWriter());
        var prueba = new PruebaStepViewModel(NullLogger<PruebaStepViewModel>.Instance);

        return new OnboardingViewModel(
            NullLogger<OnboardingViewModel>.Instance,
            provider, hotkey, prueba,
            new FakeCompletionStore(),
            new FakeSettingsService(),
            auth);
    }

    // ===== Resolución del tier =====

    [Theory]
    [InlineData(Tier.Byok, OnboardingTierVariant.Byok)]
    [InlineData(Tier.Trial, OnboardingTierVariant.Trial)]
    [InlineData(Tier.Pro, OnboardingTierVariant.Pro)]
    public void Bootstrap_resolves_tier_to_correct_variant(Tier tier, OnboardingTierVariant expected)
    {
        var vm = MakeVm(tier);

        Assert.Equal(expected, vm.TierVariant);
    }

    [Fact]
    public void Bootstrap_with_null_entitlement_defaults_to_byok_variant()
    {
        // Edge case: race entre StateChanged y fetch del entitlement. El default seguro
        // es BYOK (incluye Provider step, peor caso un Trial/Pro pasa por una pantalla
        // extra). Documentado en ResolveTierVariant.
        var auth = new FakeAuthService();  // sin SetEntitlement → CurrentEntitlement=null
        var vm = Build(auth);

        Assert.Equal(OnboardingTierVariant.Byok, vm.TierVariant);
        Assert.True(vm.IsByokVariant);
    }

    [Fact]
    public void Bootstrap_with_expired_tier_defaults_to_byok_variant()
    {
        // Tier Expired no debería llegar al wizard (LoginWindow ramifica a Settings → Plan
        // en ese caso, según FLOW 0). Pero si llega por bug, fallback seguro a BYOK.
        var vm = MakeVm(Tier.Expired);

        Assert.Equal(OnboardingTierVariant.Byok, vm.TierVariant);
    }

    [Fact]
    public void IsByokVariant_IsTrialVariant_IsProVariant_are_mutually_exclusive()
    {
        var byok = MakeVm(Tier.Byok);
        var trial = MakeVm(Tier.Trial);
        var pro = MakeVm(Tier.Pro);

        Assert.True(byok.IsByokVariant);
        Assert.False(byok.IsTrialVariant);
        Assert.False(byok.IsProVariant);

        Assert.False(trial.IsByokVariant);
        Assert.True(trial.IsTrialVariant);
        Assert.False(trial.IsProVariant);

        Assert.False(pro.IsByokVariant);
        Assert.False(pro.IsTrialVariant);
        Assert.True(pro.IsProVariant);
    }

    // ===== Welcome copy bindings =====

    [Fact]
    public void Welcome_h1_for_byok_and_trial_is_generic_welcome()
    {
        Assert.Equal("Bienvenido a Spikit", MakeVm(Tier.Byok).WelcomeH1);
        Assert.Equal("Bienvenido a Spikit", MakeVm(Tier.Trial).WelcomeH1);
    }

    [Fact]
    public void Welcome_h1_for_pro_mentions_pro_explicitly()
    {
        var vm = MakeVm(Tier.Pro);

        Assert.Equal("Bienvenido a Spikit Pro", vm.WelcomeH1);
        Assert.DoesNotContain("🚀", vm.WelcomeH1);
    }

    // ===== Stepper visibility (Step3 + Line23) =====

    [Fact]
    public void IsStep3Visible_only_in_byok()
    {
        Assert.True(MakeVm(Tier.Byok).IsStep3Visible);
        Assert.False(MakeVm(Tier.Trial).IsStep3Visible);
        Assert.False(MakeVm(Tier.Pro).IsStep3Visible);
    }

    [Fact]
    public void IsLine23Visible_only_in_byok()
    {
        Assert.True(MakeVm(Tier.Byok).IsLine23Visible);
        Assert.False(MakeVm(Tier.Trial).IsLine23Visible);
        Assert.False(MakeVm(Tier.Pro).IsLine23Visible);
    }

    // ===== StepperLabel "Paso N de M" =====

    [Fact]
    public void StepperLabel_byok_uses_denominator_three()
    {
        var vm = MakeVm(Tier.Byok);

        vm.GoNextCommand.Execute(null);    // Welcome → Provider
        Assert.Equal("Paso 1 de 3", vm.StepperLabel);
    }

    [Fact]
    public void StepperLabel_trial_uses_denominator_two_and_hotkey_is_step_one()
    {
        var vm = MakeVm(Tier.Trial);

        vm.GoNextCommand.Execute(null);    // Welcome → Hotkey (saltea Provider)
        Assert.Equal("Paso 1 de 2", vm.StepperLabel);
    }

    [Fact]
    public void StepperLabel_pro_uses_denominator_two()
    {
        var vm = MakeVm(Tier.Pro);

        vm.GoNextCommand.Execute(null);    // Welcome → Hotkey
        Assert.Equal("Paso 1 de 2", vm.StepperLabel);
    }

    // ===== Routing GoNext desde Welcome (la bifurcación core) =====

    [Fact]
    public void GoNext_from_Welcome_in_byok_goes_to_Provider()
    {
        var vm = MakeVm(Tier.Byok);
        Assert.Equal(OnboardingStep.Welcome, vm.CurrentStep);

        vm.GoNextCommand.Execute(null);

        Assert.Equal(OnboardingStep.Provider, vm.CurrentStep);
    }

    [Fact]
    public void GoNext_from_Welcome_in_trial_skips_Provider_and_goes_to_Hotkey()
    {
        var vm = MakeVm(Tier.Trial);

        vm.GoNextCommand.Execute(null);

        Assert.Equal(OnboardingStep.Hotkey, vm.CurrentStep);
    }

    [Fact]
    public void GoNext_from_Welcome_in_pro_skips_Provider_and_goes_to_Hotkey()
    {
        var vm = MakeVm(Tier.Pro);

        vm.GoNextCommand.Execute(null);

        Assert.Equal(OnboardingStep.Hotkey, vm.CurrentStep);
    }

    // ===== Routing GoBack desde Hotkey (vuelta lógica) =====

    [Fact]
    public void GoBack_from_Hotkey_in_trial_returns_to_Welcome_not_Provider()
    {
        var vm = MakeVm(Tier.Trial);
        vm.GoNextCommand.Execute(null);   // Welcome → Hotkey
        Assert.Equal(OnboardingStep.Hotkey, vm.CurrentStep);

        vm.GoBackCommand.Execute(null);

        // En Trial/Pro no hay Provider; back va directo al Welcome.
        Assert.Equal(OnboardingStep.Welcome, vm.CurrentStep);
    }

    [Fact]
    public void GoBack_from_Hotkey_in_pro_returns_to_Welcome()
    {
        var vm = MakeVm(Tier.Pro);
        vm.GoNextCommand.Execute(null);   // Welcome → Hotkey

        vm.GoBackCommand.Execute(null);

        Assert.Equal(OnboardingStep.Welcome, vm.CurrentStep);
    }

    // ===== Helpers / fakes inline =====

    private sealed class FakeAuthService : IAuthService
    {
        private Entitlement? _entitlement;

        public AuthSessionState State => AuthSessionState.LoggedIn;
        public UserProfile? CurrentProfile { get; } = new("user-1", "test@spikit.dev");
        public Entitlement? CurrentEntitlement => _entitlement;
        public event EventHandler? StateChanged { add { } remove { } }
        public event EventHandler<string>? AuthPendingReceived { add { } remove { } }

        public void SetEntitlement(Entitlement entitlement) => _entitlement = entitlement;
        public void RaiseAuthPendingReceived(string email) { }

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StartLoginAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<AuthCallbackResult> HandleAuthCallbackAsync(
            IReadOnlyDictionary<string, string> p, CancellationToken ct) =>
            Task.FromResult(new AuthCallbackResult(false, null, null, null));
        public Task LogoutAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<string?> GetCurrentAccessTokenAsync(CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task<string?> ForceRefreshAccessTokenAsync(CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task<Entitlement?> RefreshEntitlementAsync(CancellationToken ct) =>
            Task.FromResult(_entitlement);
        public Task<Entitlement?> RefreshEntitlementWithBackoffAsync(
            Func<Entitlement, bool> isAcceptable, CancellationToken ct) =>
            Task.FromResult(_entitlement);
    }

    private sealed class FakeCompletionStore : IOnboardingCompletionStore
    {
        public bool Completed { get; private set; }
        public bool IsCompleted() => Completed;
        public void MarkCompleted() => Completed = true;
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public event EventHandler? SettingsChanged;
        private AppSettings _settings = new();
        public AppSettings Load() => _settings;
        public void Save(AppSettings settings)
        {
            _settings = settings;
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeProviderTester : IProviderConnectionTester
    {
        public Task<ProviderConnectionResult> TestAsync(string baseUrl, string apiKey, CancellationToken ct = default) =>
            Task.FromResult(ProviderConnectionResult.Ok());
    }

    private sealed class FakeProviderConfigWriter : IProviderConfigWriter
    {
        public Task SaveAsync(ProviderConfig config, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeHotkeyConfigWriter : IHotkeyConfigWriter
    {
        public Task SaveAsync(HotkeyDefinition definition, HotkeyMode mode, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
