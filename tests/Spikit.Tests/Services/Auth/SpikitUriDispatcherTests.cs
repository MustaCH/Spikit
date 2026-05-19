using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Models;
using Spikit.Services.Auth;
using Spikit.Services.Toast;

namespace Spikit.Tests.Services.Auth;

public class SpikitUriDispatcherTests
{
    private readonly FakeAuthService _auth = new();
    private readonly FakeToastService _toast = new();

    private SpikitUriDispatcher BuildDispatcher() =>
        new(_auth, _toast, NullLogger<SpikitUriDispatcher>.Instance);

    // ──────────────────────────────────── Parsing fallback ──────────────────────

    [Fact]
    public async Task DispatchAsync_ignores_non_parseable_uri()
    {
        var dispatcher = BuildDispatcher();

        await dispatcher.DispatchAsync("not-a-uri", CancellationToken.None);

        Assert.Empty(_toast.Shown);
        Assert.Equal(0, _auth.HandleAuthCallbackCount);
        Assert.Equal(0, _auth.RefreshEntitlementWithBackoffCount);
    }

    [Fact]
    public async Task DispatchAsync_ignores_unknown_host_kind()
    {
        var dispatcher = BuildDispatcher();

        await dispatcher.DispatchAsync("spikit://something-else?x=1", CancellationToken.None);

        Assert.Empty(_toast.Shown);
        Assert.Equal(0, _auth.HandleAuthCallbackCount);
    }

    // ──────────────────────────────────── AuthCallback ──────────────────────────

    [Fact]
    public async Task DispatchAsync_auth_callback_success_invokes_auth_and_shows_info_toast()
    {
        _auth.NextCallbackResult = new AuthCallbackResult(
            Success: true,
            Profile: new UserProfile("user-1", "nacho@spikit.dev"),
            Entitlement: null,
            ErrorReason: null);
        var dispatcher = BuildDispatcher();

        await dispatcher.DispatchAsync(
            "spikit://auth-callback?access_token=abc&refresh_token=def",
            CancellationToken.None);

        Assert.Equal(1, _auth.HandleAuthCallbackCount);
        Assert.Equal("abc", _auth.LastCallbackParams!["access_token"]);
        var toast = Assert.Single(_toast.Shown);
        Assert.Equal(ToastSeverity.Info, toast.Severity);
        Assert.Contains("nacho@spikit.dev", toast.Title);
    }

    [Fact]
    public async Task DispatchAsync_auth_callback_failure_shows_warning_toast_with_reason()
    {
        _auth.NextCallbackResult = new AuthCallbackResult(
            Success: false,
            Profile: null,
            Entitlement: null,
            ErrorReason: "Magic link expired");
        var dispatcher = BuildDispatcher();

        await dispatcher.DispatchAsync(
            "spikit://auth-callback?error_description=Magic%20link%20expired",
            CancellationToken.None);

        var toast = Assert.Single(_toast.Shown);
        Assert.Equal(ToastSeverity.Warning, toast.Severity);
        Assert.Contains("Magic link expired", toast.Title);
    }

    [Fact]
    public async Task DispatchAsync_auth_callback_unhandled_exception_shows_error_toast()
    {
        _auth.HandleAuthCallbackThrows = new InvalidOperationException("boom");
        var dispatcher = BuildDispatcher();

        await dispatcher.DispatchAsync(
            "spikit://auth-callback?access_token=a&refresh_token=b",
            CancellationToken.None);

        var toast = Assert.Single(_toast.Shown);
        Assert.Equal(ToastSeverity.Error, toast.Severity);
    }

    // ──────────────────────────────────── BillingReturn ─────────────────────────

    [Fact]
    public async Task DispatchAsync_billing_return_cancel_shows_neutral_toast_and_does_not_refresh()
    {
        var dispatcher = BuildDispatcher();

        await dispatcher.DispatchAsync(
            "spikit://billing-return?status=cancel",
            CancellationToken.None);

        Assert.Equal(0, _auth.RefreshEntitlementWithBackoffCount);
        var toast = Assert.Single(_toast.Shown);
        Assert.Equal(ToastSeverity.Info, toast.Severity);
        Assert.Contains("cancelado", toast.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DispatchAsync_billing_return_success_refreshes_and_announces_Pro()
    {
        _auth.NextRefreshResult = new Entitlement(Tier.Pro, null,
            new DateTimeOffset(2026, 06, 19, 0, 0, 0, TimeSpan.Zero), null, 0);
        var dispatcher = BuildDispatcher();

        await dispatcher.DispatchAsync(
            "spikit://billing-return?status=success",
            CancellationToken.None);

        Assert.Equal(1, _auth.RefreshEntitlementWithBackoffCount);
        var toast = Assert.Single(_toast.Shown);
        Assert.Equal(ToastSeverity.Info, toast.Severity);
        Assert.Contains("Pro", toast.Title);
    }

    [Fact]
    public async Task DispatchAsync_billing_return_no_status_still_refreshes()
    {
        // Stripe puede no incluir el `status` si configuramos los return URLs raw.
        // Default = treat as success.
        _auth.NextRefreshResult = new Entitlement(Tier.Pro, null, null, null, 0);
        var dispatcher = BuildDispatcher();

        await dispatcher.DispatchAsync(
            "spikit://billing-return",
            CancellationToken.None);

        Assert.Equal(1, _auth.RefreshEntitlementWithBackoffCount);
        Assert.Single(_toast.Shown);
    }

    [Fact]
    public async Task DispatchAsync_billing_return_refresh_returns_null_shows_processing_toast()
    {
        // Race condition con el webhook — el refresh devolvió null (fetch fail o no session).
        // Mensaje user-friendly explicando que está procesándose.
        _auth.NextRefreshResult = null;
        var dispatcher = BuildDispatcher();

        await dispatcher.DispatchAsync(
            "spikit://billing-return?status=success",
            CancellationToken.None);

        var toast = Assert.Single(_toast.Shown);
        Assert.Equal(ToastSeverity.Info, toast.Severity);
        Assert.Contains("procesándose", toast.Title);
    }

    // ─────────────────────────────────────── Fakes ──────────────────────────────

    private sealed class FakeAuthService : IAuthService
    {
        public AuthSessionState State => AuthSessionState.LoggedOut;
        public UserProfile? CurrentProfile => null;
        public Entitlement? CurrentEntitlement => null;
        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }

        public AuthCallbackResult? NextCallbackResult { get; set; }
        public Exception? HandleAuthCallbackThrows { get; set; }
        public int HandleAuthCallbackCount { get; private set; }
        public IReadOnlyDictionary<string, string>? LastCallbackParams { get; private set; }

        public Entitlement? NextRefreshResult { get; set; }
        public int RefreshEntitlementCount { get; private set; }
        public int RefreshEntitlementWithBackoffCount { get; private set; }
        public Func<Entitlement, bool>? LastBackoffPredicate { get; private set; }

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StartLoginAsync(CancellationToken ct) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<string?> GetCurrentAccessTokenAsync(CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<string?> ForceRefreshAccessTokenAsync(CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<AuthCallbackResult> HandleAuthCallbackAsync(
            IReadOnlyDictionary<string, string> queryParams, CancellationToken ct)
        {
            HandleAuthCallbackCount++;
            LastCallbackParams = queryParams;
            if (HandleAuthCallbackThrows is not null) throw HandleAuthCallbackThrows;
            return Task.FromResult(NextCallbackResult
                ?? new AuthCallbackResult(false, null, null, "Test no seteó NextCallbackResult"));
        }

        public Task<Entitlement?> RefreshEntitlementAsync(CancellationToken ct)
        {
            RefreshEntitlementCount++;
            return Task.FromResult(NextRefreshResult);
        }

        public Task<Entitlement?> RefreshEntitlementWithBackoffAsync(
            Func<Entitlement, bool> isAcceptable, CancellationToken ct)
        {
            RefreshEntitlementWithBackoffCount++;
            LastBackoffPredicate = isAcceptable;
            return Task.FromResult(NextRefreshResult);
        }
    }

    private sealed class FakeToastService : IToastService
    {
        public List<(ToastSeverity Severity, string Title, string? Message, string? DedupeKey)> Shown { get; } = new();

        public void Show(
            ToastSeverity severity, string title, string? message = null,
            ToastAction? action = null, TimeSpan? autoDismiss = null, string? dedupeKey = null)
        {
            Shown.Add((severity, title, message, dedupeKey));
        }
    }
}
