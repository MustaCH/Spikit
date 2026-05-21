using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Spikit.Services.Auth;
using Spikit.Services.Transcription;

namespace Spikit.Tests.Services.Transcription;

public class ProxyTranscriptionServiceTests
{
    private readonly SupabaseOptions _options = new()
    {
        BaseUrl = "https://example.supabase.co",
        PublishableKey = "pk_test",
    };

    private readonly FakeAuthForProxy _auth = new();

    private ProxyTranscriptionService BuildService(FakeHttpMessageHandler handler) =>
        new(new HttpClient(handler), Options.Create(_options), _auth,
            NullLogger<ProxyTranscriptionService>.Instance);

    private static byte[] WavBytes() => new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3 };

    [Fact]
    public async Task TranscribeAsync_returns_text_from_proxy_response()
    {
        _auth.CurrentToken = "tk";
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK,
            """{"text":"hola mundo"}""");
        var svc = BuildService(handler);

        var text = await svc.TranscribeAsync(WavBytes(), CancellationToken.None);

        Assert.Equal("hola mundo", text);
    }

    [Fact]
    public async Task TranscribeAsync_sends_bearer_apikey_and_audio_field()
    {
        _auth.CurrentToken = "tk-abc";
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, """{"text":"x"}""");
        var svc = BuildService(handler);

        await svc.TranscribeAsync(WavBytes(), CancellationToken.None);

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://example.supabase.co/functions/v1/transcribe", req.RequestUri!.ToString());
        Assert.Equal("tk-abc", req.Headers.Authorization!.Parameter);
        Assert.True(req.Headers.Contains("apikey"));
        Assert.IsType<MultipartFormDataContent>(req.Content);
        // El field name `audio` es contractual con el Edge Function (form.get('audio')).
        Assert.Contains("name=audio", handler.CapturedBodies[0]);
    }

    [Fact]
    public async Task TranscribeAsync_throws_AuthTokenInvalid_when_no_session()
    {
        _auth.CurrentToken = null;
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, """{"text":"x"}""");
        var svc = BuildService(handler);

        await Assert.ThrowsAsync<AuthTokenInvalidException>(
            () => svc.TranscribeAsync(WavBytes(), CancellationToken.None));
    }

    [Fact]
    public async Task TranscribeAsync_throws_SubscriptionRequired_on_402()
    {
        _auth.CurrentToken = "tk";
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.PaymentRequired,
            """{"error":"subscription_required"}""");
        var svc = BuildService(handler);

        await Assert.ThrowsAsync<SubscriptionRequiredException>(
            () => svc.TranscribeAsync(WavBytes(), CancellationToken.None));
    }

    [Fact]
    public async Task TranscribeAsync_throws_TranscriptionException_on_500()
    {
        _auth.CurrentToken = "tk";
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.InternalServerError, "boom");
        var svc = BuildService(handler);

        var ex = await Assert.ThrowsAsync<TranscriptionException>(
            () => svc.TranscribeAsync(WavBytes(), CancellationToken.None));
        Assert.Equal(HttpStatusCode.InternalServerError, ex.StatusCode);
        // SubscriptionRequiredException es derived; aseguramos que NO es esa.
        Assert.IsNotType<SubscriptionRequiredException>(ex);
    }

    [Fact]
    public async Task TranscribeAsync_401_triggers_force_refresh_and_retries_once()
    {
        // Primer call: 401. Segundo call (post-force-refresh): 200.
        _auth.CurrentToken = "stale-token";
        _auth.ForceRefreshResult = "fresh-token";

        var callIndex = 0;
        var handler = new FakeHttpMessageHandler((req, _) =>
        {
            callIndex++;
            var statusCode = callIndex == 1 ? HttpStatusCode.Unauthorized : HttpStatusCode.OK;
            var body = callIndex == 1 ? """{"error":"invalid_jwt"}""" : """{"text":"chau"}""";
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        });
        var svc = BuildService(handler);

        var text = await svc.TranscribeAsync(WavBytes(), CancellationToken.None);

        Assert.Equal("chau", text);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(1, _auth.ForceRefreshCallCount);
        // El segundo request usa el token forced-refreshed.
        Assert.Equal("fresh-token", handler.Requests[1].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task TranscribeAsync_401_then_force_refresh_failed_propagates_AuthTokenInvalid()
    {
        _auth.CurrentToken = "stale";
        _auth.ForceRefreshResult = null; // refresh falla → null
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.Unauthorized,
            """{"error":"invalid_jwt"}""");
        var svc = BuildService(handler);

        await Assert.ThrowsAsync<AuthTokenInvalidException>(
            () => svc.TranscribeAsync(WavBytes(), CancellationToken.None));
        Assert.Equal(1, _auth.ForceRefreshCallCount);
        Assert.Single(handler.Requests); // no retry porque no hay token nuevo
    }

    [Fact]
    public async Task TranscribeAsync_401_then_second_401_propagates()
    {
        // Edge case: force-refresh devuelve un token pero el server lo rechaza igual
        // (revocación profunda). No reintentamos infinitamente — propagamos.
        _auth.CurrentToken = "stale";
        _auth.ForceRefreshResult = "also-stale";
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.Unauthorized,
            """{"error":"invalid_jwt"}""");
        var svc = BuildService(handler);

        await Assert.ThrowsAsync<AuthTokenInvalidException>(
            () => svc.TranscribeAsync(WavBytes(), CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task TranscribeAsync_rejects_null_or_empty_wav()
    {
        _auth.CurrentToken = "tk";
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, """{"text":""}""");
        var svc = BuildService(handler);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => svc.TranscribeAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.TranscribeAsync(Array.Empty<byte>(), CancellationToken.None));
    }

    // Fake mínimo de IAuthService que solo cubre los métodos que ProxyTranscriptionService usa.
    private sealed class FakeAuthForProxy : IAuthService
    {
        public string? CurrentToken { get; set; }
        public string? ForceRefreshResult { get; set; }
        public int ForceRefreshCallCount { get; private set; }

        public AuthSessionState State => AuthSessionState.LoggedIn;
        public UserProfile? CurrentProfile => null;
        public Entitlement? CurrentEntitlement => null;
        public bool IsOfflineMode => false;
        public AuthInitOutcome LastInitializeOutcome => AuthInitOutcome.NotRun;
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
        public Task<string?> ForceRefreshAccessTokenAsync(CancellationToken ct)
        {
            ForceRefreshCallCount++;
            if (ForceRefreshResult is not null) CurrentToken = ForceRefreshResult;
            return Task.FromResult(ForceRefreshResult);
        }
        public Task<Entitlement?> RefreshEntitlementAsync(CancellationToken ct) =>
            Task.FromResult<Entitlement?>(null);
        public Task<Entitlement?> RefreshEntitlementWithBackoffAsync(
            Func<Entitlement, bool> f, CancellationToken ct) =>
            Task.FromResult<Entitlement?>(null);
    }
}
