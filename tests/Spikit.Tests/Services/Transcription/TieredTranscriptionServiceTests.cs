using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Spikit.Services.Auth;
using Spikit.Services.Transcription;

namespace Spikit.Tests.Services.Transcription;

public class TieredTranscriptionServiceTests
{
    private readonly FakeAuthForTiered _auth = new();
    private readonly SupabaseOptions _supaOptions = new()
    {
        BaseUrl = "https://example.supabase.co",
        PublishableKey = "pk_test",
    };

    private TieredTranscriptionService BuildTiered(
        FakeHttpMessageHandler? directHandler = null,
        FakeHttpMessageHandler? proxyHandler = null)
    {
        var directHttp = new HttpClient(directHandler ?? FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "{}"));
        var direct = new WhisperApiTranscriptionService(
            directHttp,
            Options.Create(new WhisperApiOptions { BaseUrl = "https://api.openai.com/v1", Model = "whisper-1" }),
            new WhisperApiKey("sk-test"),
            NullLogger<WhisperApiTranscriptionService>.Instance);

        var proxyHttp = new HttpClient(proxyHandler ?? FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "{}"));
        var proxy = new ProxyTranscriptionService(
            proxyHttp, Options.Create(_supaOptions), _auth,
            NullLogger<ProxyTranscriptionService>.Instance);

        return new TieredTranscriptionService(direct, proxy, _auth,
            NullLogger<TieredTranscriptionService>.Instance);
    }

    // ──────────────────────────────────── SelectPath ────────────────────────────

    [Fact]
    public void SelectPath_LoggedOut_routes_to_Direct_legacy_BYOK()
    {
        _auth.State = AuthSessionState.LoggedOut;
        var tiered = BuildTiered();

        Assert.Equal(TieredTranscriptionService.TranscriptionPath.Direct, tiered.SelectPath());
    }

    [Fact]
    public void SelectPath_LoggedIn_Byok_routes_to_Direct()
    {
        _auth.State = AuthSessionState.LoggedIn;
        _auth.CurrentEntitlement = new Entitlement(Tier.Byok, null, null, null, 0);
        Assert.Equal(TieredTranscriptionService.TranscriptionPath.Direct, BuildTiered().SelectPath());
    }

    [Fact]
    public void SelectPath_LoggedIn_Trial_routes_to_Proxy()
    {
        _auth.State = AuthSessionState.LoggedIn;
        _auth.CurrentEntitlement = new Entitlement(Tier.Trial, null, null, null, 0);
        Assert.Equal(TieredTranscriptionService.TranscriptionPath.Proxy, BuildTiered().SelectPath());
    }

    [Fact]
    public void SelectPath_LoggedIn_Pro_routes_to_Proxy()
    {
        _auth.State = AuthSessionState.LoggedIn;
        _auth.CurrentEntitlement = new Entitlement(Tier.Pro, null, null, null, 0);
        Assert.Equal(TieredTranscriptionService.TranscriptionPath.Proxy, BuildTiered().SelectPath());
    }

    [Fact]
    public void SelectPath_LoggedIn_Expired_routes_to_Blocked()
    {
        _auth.State = AuthSessionState.LoggedIn;
        _auth.CurrentEntitlement = new Entitlement(Tier.Expired, null, null, null, 0);
        Assert.Equal(TieredTranscriptionService.TranscriptionPath.Blocked, BuildTiered().SelectPath());
    }

    [Fact]
    public void SelectPath_LoggedIn_with_null_entitlement_falls_back_to_Direct()
    {
        // Cache pre-fetch o fetch falló post-login. Conservador: Direct path (BYOK key
        // local; si no es BYOK realmente, el server-side rechaza después).
        _auth.State = AuthSessionState.LoggedIn;
        _auth.CurrentEntitlement = null;
        var tiered = BuildTiered();

        Assert.Equal(TieredTranscriptionService.TranscriptionPath.Direct, tiered.SelectPath());
    }

    // ─────────────────────────────────── TranscribeAsync ────────────────────────

    [Fact]
    public async Task TranscribeAsync_BYOK_uses_Direct_path()
    {
        _auth.State = AuthSessionState.LoggedIn;
        _auth.CurrentEntitlement = new Entitlement(Tier.Byok, null, null, null, 0);
        var directHandler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK,
            """{"text":"directo OpenAI"}""");
        var proxyHandler = FakeHttpMessageHandler.Returning(HttpStatusCode.InternalServerError, "");
        var tiered = BuildTiered(directHandler, proxyHandler);

        var text = await tiered.TranscribeAsync(WavBytes(), CancellationToken.None);

        Assert.Equal("directo OpenAI", text);
        Assert.Single(directHandler.Requests);
        Assert.Empty(proxyHandler.Requests);
    }

    [Fact]
    public async Task TranscribeAsync_Trial_uses_Proxy_path()
    {
        _auth.State = AuthSessionState.LoggedIn;
        _auth.CurrentEntitlement = new Entitlement(Tier.Trial, null, null, null, 0);
        _auth.CurrentToken = "tk";
        var directHandler = FakeHttpMessageHandler.Returning(HttpStatusCode.InternalServerError, "");
        var proxyHandler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK,
            """{"text":"via proxy"}""");
        var tiered = BuildTiered(directHandler, proxyHandler);

        var text = await tiered.TranscribeAsync(WavBytes(), CancellationToken.None);

        Assert.Equal("via proxy", text);
        Assert.Single(proxyHandler.Requests);
        Assert.Empty(directHandler.Requests);
    }

    [Fact]
    public async Task TranscribeAsync_Pro_uses_Proxy_path()
    {
        _auth.State = AuthSessionState.LoggedIn;
        _auth.CurrentEntitlement = new Entitlement(Tier.Pro, null, null, null, 0);
        _auth.CurrentToken = "tk";
        var proxyHandler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK,
            """{"text":"pro proxy"}""");
        var tiered = BuildTiered(null, proxyHandler);

        var text = await tiered.TranscribeAsync(WavBytes(), CancellationToken.None);

        Assert.Equal("pro proxy", text);
    }

    [Fact]
    public async Task TranscribeAsync_Expired_throws_SubscriptionRequired_without_HTTP()
    {
        _auth.State = AuthSessionState.LoggedIn;
        _auth.CurrentEntitlement = new Entitlement(Tier.Expired, null, null, null, 0);
        var directHandler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "{}");
        var proxyHandler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "{}");
        var tiered = BuildTiered(directHandler, proxyHandler);

        await Assert.ThrowsAsync<SubscriptionRequiredException>(
            () => tiered.TranscribeAsync(WavBytes(), CancellationToken.None));
        Assert.Empty(directHandler.Requests);
        Assert.Empty(proxyHandler.Requests);
    }

    private static byte[] WavBytes() => new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3 };

    // Fake mínimo de IAuthService que cubre lo que necesitan Tiered + Proxy.
    private sealed class FakeAuthForTiered : IAuthService
    {
        public AuthSessionState State { get; set; } = AuthSessionState.LoggedOut;
        public UserProfile? CurrentProfile { get; set; }
        public Entitlement? CurrentEntitlement { get; set; }
        public string? CurrentToken { get; set; } = "tk";
        public event EventHandler? StateChanged { add { } remove { } }
        public event EventHandler<string>? AuthPendingReceived { add { } remove { } }
        public void RaiseAuthPendingReceived(string email) { }

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StartLoginAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<AuthCallbackResult> HandleAuthCallbackAsync(
            IReadOnlyDictionary<string, string> p, CancellationToken ct) =>
            Task.FromResult(new AuthCallbackResult(false, null, null, null));
        public Task LogoutAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<string?> GetCurrentAccessTokenAsync(CancellationToken ct) =>
            Task.FromResult(CurrentToken);
        public Task<string?> ForceRefreshAccessTokenAsync(CancellationToken ct) =>
            Task.FromResult(CurrentToken);
        public Task<Entitlement?> RefreshEntitlementAsync(CancellationToken ct) =>
            Task.FromResult(CurrentEntitlement);
        public Task<Entitlement?> RefreshEntitlementWithBackoffAsync(
            Func<Entitlement, bool> f, CancellationToken ct) =>
            Task.FromResult(CurrentEntitlement);
    }
}
