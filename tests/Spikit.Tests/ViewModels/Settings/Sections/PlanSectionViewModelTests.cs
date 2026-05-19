using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Services.Auth;
using Spikit.Services.Billing;
using Spikit.ViewModels.Settings.Sections;

namespace Spikit.Tests.ViewModels.Settings.Sections;

public class PlanSectionViewModelTests
{
    private readonly FakeAuth _auth = new();
    private readonly FakeBilling _billing = new();
    private readonly FakeBrowser _browser = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 05, 19, 12, 00, 00, TimeSpan.Zero));

    private PlanSectionViewModel BuildVm() =>
        new(_auth, _billing, _browser, _time, NullLogger<PlanSectionViewModel>.Instance);

    // ─────────────────────────────────── State flags ────────────────────────────

    [Fact]
    public void Defaults_to_LoggedOut_and_CanLogin()
    {
        var vm = BuildVm();

        Assert.True(vm.IsLoggedOut);
        Assert.False(vm.IsLoggedIn);
        Assert.True(vm.CanLogin);
        Assert.False(vm.CanLogout);
        Assert.False(vm.CanUpgrade);
        Assert.False(vm.CanManageSubscription);
    }

    [Fact]
    public void Reacts_to_AuthStateChanged_with_LoggedIn_transition()
    {
        var vm = BuildVm();
        var notified = false;
        vm.PropertyChanged += (_, _) => notified = true;

        _auth.SetLoggedIn(new UserProfile("u1", "nacho@spikit.dev"), TrialEntitlement());

        Assert.True(vm.IsLoggedIn);
        Assert.Equal("nacho@spikit.dev", vm.UserEmail);
        Assert.True(notified, "VM debería haber notificado PropertyChanged tras el state change");
    }

    // ─────────────────────────────────── Tier displays ──────────────────────────

    [Fact]
    public void Trial_tier_shows_trial_details_and_can_upgrade()
    {
        _auth.SetLoggedIn(new UserProfile("u1", "x@y.com"), TrialEntitlement(daysFromNow: 10));
        var vm = BuildVm();

        Assert.True(vm.ShowTrialDetails);
        Assert.False(vm.ShowProDetails);
        Assert.Equal("Trial", vm.TierLabel);
        Assert.Equal("Quedan 10 días", vm.TrialCountdown);
        Assert.True(vm.CanUpgrade);
        Assert.False(vm.CanManageSubscription);
    }

    [Fact]
    public void Trial_countdown_pluralizes_1_day_and_today()
    {
        _auth.SetLoggedIn(new UserProfile("u1", "x@y.com"), TrialEntitlement(daysFromNow: 1));
        var vmOneDay = BuildVm();
        Assert.Equal("Queda 1 día", vmOneDay.TrialCountdown);

        _auth.SetLoggedIn(new UserProfile("u1", "x@y.com"), TrialEntitlement(daysFromNow: 0));
        var vmToday = BuildVm();
        Assert.Equal("Termina hoy", vmToday.TrialCountdown);
    }

    [Fact]
    public void Pro_tier_shows_pro_details_and_can_manage()
    {
        var renewsAt = _time.GetUtcNow().AddMonths(1);
        _auth.SetLoggedIn(new UserProfile("u1", "x@y.com"),
            new Entitlement(Tier.Pro, null, renewsAt, null, 0));
        var vm = BuildVm();

        Assert.True(vm.ShowProDetails);
        Assert.False(vm.ShowTrialDetails);
        Assert.Equal("Pro", vm.TierLabel);
        Assert.NotNull(vm.ProRenewsAt);
        Assert.True(vm.CanManageSubscription);
        Assert.False(vm.CanUpgrade);
    }

    [Fact]
    public void Byok_tier_shows_creator_program_and_grace_when_set()
    {
        var graceEnds = _time.GetUtcNow().AddDays(15);
        _auth.SetLoggedIn(new UserProfile("u1", "x@y.com"),
            new Entitlement(Tier.Byok, null, null, graceEnds, 0));
        var vm = BuildVm();

        Assert.True(vm.ShowByokDetails);
        Assert.True(vm.ShowByokGrace);
        Assert.Equal("Creator program", vm.TierLabel);
        Assert.NotNull(vm.ByokGraceCountdown);
        Assert.Contains("15", vm.ByokGraceCountdown!);
        // BYOK debería poder upgradear (offboarding path).
        Assert.True(vm.CanUpgrade);
    }

    [Fact]
    public void Byok_tier_without_grace_hides_grace_message()
    {
        _auth.SetLoggedIn(new UserProfile("u1", "x@y.com"),
            new Entitlement(Tier.Byok, null, null, null, 0));
        var vm = BuildVm();

        Assert.True(vm.ShowByokDetails);
        Assert.False(vm.ShowByokGrace);
        Assert.Null(vm.ByokGraceCountdown);
    }

    [Fact]
    public void Expired_tier_shows_expired_details_and_can_upgrade()
    {
        _auth.SetLoggedIn(new UserProfile("u1", "x@y.com"),
            new Entitlement(Tier.Expired, null, null, null, 0));
        var vm = BuildVm();

        Assert.True(vm.ShowExpiredDetails);
        Assert.True(vm.CanUpgrade);
    }

    [Fact]
    public void LoggedIn_without_entitlement_shows_loading_details()
    {
        _auth.SetLoggedIn(new UserProfile("u1", "x@y.com"), entitlement: null);
        var vm = BuildVm();

        Assert.True(vm.ShowLoadingDetails);
        Assert.False(vm.ShowTrialDetails);
        Assert.False(vm.ShowProDetails);
    }

    // ─────────────────────────────────── Login command ──────────────────────────

    [Fact]
    public async Task LoginCommand_invokes_StartLoginAsync()
    {
        var vm = BuildVm();

        vm.LoginCommand.Execute(null);
        await FlushAsyncCommands();

        Assert.Equal(1, _auth.StartLoginCount);
    }

    // ─────────────────────────────────── Logout command ─────────────────────────

    [Fact]
    public async Task LogoutCommand_invokes_LogoutAsync_and_clears_busy()
    {
        _auth.SetLoggedIn(new UserProfile("u1", "x@y.com"), TrialEntitlement());
        var vm = BuildVm();

        vm.LogoutCommand.Execute(null);
        await FlushAsyncCommands();

        Assert.Equal(1, _auth.LogoutCount);
        Assert.False(vm.IsBusy);
    }

    // ─────────────────────────────────── Upgrade command ────────────────────────

    [Fact]
    public async Task UpgradeCommand_uses_monthly_lookup_key_by_default()
    {
        _auth.SetLoggedIn(new UserProfile("u1", "x@y.com"), TrialEntitlement());
        _auth.AccessToken = "tk";
        _billing.NextCheckoutUrl = "https://checkout.stripe.com/sess/abc";
        var vm = BuildVm();

        vm.UpgradeCommand.Execute(null);
        await FlushAsyncCommands();

        Assert.Equal(PlanSectionViewModel.MonthlyLookupKey, _billing.LastCheckoutLookupKey);
        Assert.Equal("https://checkout.stripe.com/sess/abc", _browser.LastUrl);
    }

    [Fact]
    public async Task UpgradeCommand_uses_yearly_lookup_when_selected()
    {
        _auth.SetLoggedIn(new UserProfile("u1", "x@y.com"), TrialEntitlement());
        _auth.AccessToken = "tk";
        _billing.NextCheckoutUrl = "https://checkout.stripe.com/sess/year";
        var vm = BuildVm();
        vm.BillingInterval = "yearly";

        vm.UpgradeCommand.Execute(null);
        await FlushAsyncCommands();

        Assert.Equal(PlanSectionViewModel.YearlyLookupKey, _billing.LastCheckoutLookupKey);
    }

    [Fact]
    public async Task UpgradeCommand_shows_error_when_no_token()
    {
        _auth.SetLoggedIn(new UserProfile("u1", "x@y.com"), TrialEntitlement());
        _auth.AccessToken = null;
        var vm = BuildVm();

        vm.UpgradeCommand.Execute(null);
        await FlushAsyncCommands();

        Assert.NotNull(vm.ErrorMessage);
        Assert.Null(_browser.LastUrl);
        Assert.Null(_billing.LastCheckoutLookupKey);
    }

    [Fact]
    public async Task UpgradeCommand_handles_BillingException_with_error_message()
    {
        _auth.SetLoggedIn(new UserProfile("u1", "x@y.com"), TrialEntitlement());
        _auth.AccessToken = "tk";
        _billing.NextCheckoutException = new BillingException("server 500");
        var vm = BuildVm();

        vm.UpgradeCommand.Execute(null);
        await FlushAsyncCommands();

        Assert.NotNull(vm.ErrorMessage);
        Assert.Null(_browser.LastUrl);
    }

    [Fact]
    public async Task UpgradeCommand_handles_AuthTokenInvalid()
    {
        _auth.SetLoggedIn(new UserProfile("u1", "x@y.com"), TrialEntitlement());
        _auth.AccessToken = "tk";
        _billing.NextCheckoutException = new AuthTokenInvalidException("expired");
        var vm = BuildVm();

        vm.UpgradeCommand.Execute(null);
        await FlushAsyncCommands();

        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("sesión", vm.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────── ManageSubscription ─────────────────────────

    [Fact]
    public async Task ManageSubscriptionCommand_opens_portal_url()
    {
        _auth.SetLoggedIn(new UserProfile("u1", "x@y.com"),
            new Entitlement(Tier.Pro, null, _time.GetUtcNow().AddMonths(1), null, 0));
        _auth.AccessToken = "tk";
        _billing.NextPortalUrl = "https://billing.stripe.com/p/sess/xyz";
        var vm = BuildVm();

        vm.ManageSubscriptionCommand.Execute(null);
        await FlushAsyncCommands();

        Assert.Equal("https://billing.stripe.com/p/sess/xyz", _browser.LastUrl);
    }

    // ─────────────────────────────────── Helpers ────────────────────────────────

    private Entitlement TrialEntitlement(int daysFromNow = 7) =>
        new(Tier.Trial,
            _time.GetUtcNow().AddDays(daysFromNow),
            null, null, 0);

    private static async Task FlushAsyncCommands()
    {
        // Las RelayCommand `async void` agenden continuations en el threadpool / sync
        // context. Damos varios yields para que las task-continuations corran antes de
        // que el test assert.
        for (int i = 0; i < 5; i++) await Task.Yield();
    }

    // ─────────────────────────────────── Fakes ──────────────────────────────────

    private sealed class FakeAuth : IAuthService
    {
        public AuthSessionState State { get; private set; } = AuthSessionState.LoggedOut;
        public UserProfile? CurrentProfile { get; private set; }
        public Entitlement? CurrentEntitlement { get; private set; }
        public event EventHandler? StateChanged;

        public string? AccessToken { get; set; }
        public int StartLoginCount { get; private set; }
        public int LogoutCount { get; private set; }

        public void SetLoggedIn(UserProfile profile, Entitlement? entitlement)
        {
            State = AuthSessionState.LoggedIn;
            CurrentProfile = profile;
            CurrentEntitlement = entitlement;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;

        public Task StartLoginAsync(CancellationToken ct)
        {
            StartLoginCount++;
            return Task.CompletedTask;
        }

        public Task<AuthCallbackResult> HandleAuthCallbackAsync(
            IReadOnlyDictionary<string, string> queryParams, CancellationToken ct) =>
            Task.FromResult(new AuthCallbackResult(false, null, null, null));

        public Task LogoutAsync(CancellationToken ct)
        {
            LogoutCount++;
            State = AuthSessionState.LoggedOut;
            CurrentProfile = null;
            CurrentEntitlement = null;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task<string?> GetCurrentAccessTokenAsync(CancellationToken ct) =>
            Task.FromResult(AccessToken);

        public Task<Entitlement?> RefreshEntitlementAsync(CancellationToken ct) =>
            Task.FromResult(CurrentEntitlement);

        public Task<Entitlement?> RefreshEntitlementWithBackoffAsync(
            Func<Entitlement, bool> isAcceptable, CancellationToken ct) =>
            Task.FromResult(CurrentEntitlement);
    }

    private sealed class FakeBilling : IStripeBillingClient
    {
        public string? NextCheckoutUrl { get; set; }
        public string? NextPortalUrl { get; set; }
        public Exception? NextCheckoutException { get; set; }
        public string? LastCheckoutLookupKey { get; private set; }

        public Task<string> CreateCheckoutSessionAsync(string accessToken, string lookupKey, CancellationToken ct)
        {
            LastCheckoutLookupKey = lookupKey;
            if (NextCheckoutException is not null) throw NextCheckoutException;
            return Task.FromResult(NextCheckoutUrl
                ?? throw new InvalidOperationException("Test no seteó NextCheckoutUrl"));
        }

        public Task<string> CreatePortalSessionAsync(string accessToken, CancellationToken ct) =>
            Task.FromResult(NextPortalUrl
                ?? throw new InvalidOperationException("Test no seteó NextPortalUrl"));
    }

    private sealed class FakeBrowser : IBrowserLauncher
    {
        public string? LastUrl { get; private set; }
        public void Open(string url) => LastUrl = url;
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
