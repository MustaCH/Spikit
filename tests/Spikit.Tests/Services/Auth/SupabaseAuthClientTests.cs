using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Spikit.Services.Auth;
using Spikit.Tests.Services.Transcription;

namespace Spikit.Tests.Services.Auth;

public class SupabaseAuthClientTests
{
    private readonly SupabaseOptions _options = new()
    {
        BaseUrl = "https://example.supabase.co",
        PublishableKey = "pk_test",
    };

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 05, 19, 12, 00, 00, TimeSpan.Zero));

    private SupabaseAuthClient BuildClient(FakeHttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        return new SupabaseAuthClient(http, Options.Create(_options), _time,
            NullLogger<SupabaseAuthClient>.Instance);
    }

    // ──────────────────────────────── ValidateAccessTokenAsync ────────────────────

    [Fact]
    public async Task ValidateAccessTokenAsync_returns_profile_on_200()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK,
            """{"id":"user-id-123","email":"nacho@spikit.dev"}""");
        var client = BuildClient(handler);

        var profile = await client.ValidateAccessTokenAsync("access-123", CancellationToken.None);

        Assert.Equal("user-id-123", profile.Id);
        Assert.Equal("nacho@spikit.dev", profile.Email);
    }

    [Fact]
    public async Task ValidateAccessTokenAsync_sends_apikey_and_bearer_headers()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK,
            """{"id":"u","email":"e@x.com"}""");
        var client = BuildClient(handler);

        await client.ValidateAccessTokenAsync("access-xyz", CancellationToken.None);

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("https://example.supabase.co/auth/v1/user", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("access-xyz", req.Headers.Authorization.Parameter);
        Assert.True(req.Headers.TryGetValues("apikey", out var apikey));
        Assert.Equal("pk_test", apikey!.Single());
    }

    [Fact]
    public async Task ValidateAccessTokenAsync_throws_TokenInvalid_on_401()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.Unauthorized,
            """{"error":"unauthorized"}""");
        var client = BuildClient(handler);

        await Assert.ThrowsAsync<AuthTokenInvalidException>(
            () => client.ValidateAccessTokenAsync("bad", CancellationToken.None));
    }

    [Fact]
    public async Task ValidateAccessTokenAsync_throws_AuthException_on_500()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.InternalServerError, "boom");
        var client = BuildClient(handler);

        var ex = await Assert.ThrowsAsync<AuthException>(
            () => client.ValidateAccessTokenAsync("x", CancellationToken.None));
        Assert.IsNotType<AuthTokenInvalidException>(ex);
    }

    [Fact]
    public async Task ValidateAccessTokenAsync_throws_when_response_missing_email()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK,
            """{"id":"u","email":null}""");
        var client = BuildClient(handler);

        await Assert.ThrowsAsync<AuthException>(
            () => client.ValidateAccessTokenAsync("x", CancellationToken.None));
    }

    // ─────────────────────────────────── RefreshAsync ─────────────────────────────

    [Fact]
    public async Task RefreshAsync_returns_new_pair_with_computed_ExpiresAt()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK,
            """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600}""");
        var client = BuildClient(handler);

        var pair = await client.RefreshAsync("old-refresh", CancellationToken.None);

        Assert.Equal("new-access", pair.AccessToken);
        Assert.Equal("new-refresh", pair.RefreshToken);
        Assert.Equal(_time.GetUtcNow().AddSeconds(3600), pair.ExpiresAt);
    }

    [Fact]
    public async Task RefreshAsync_sends_refresh_token_in_body()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK,
            """{"access_token":"a","refresh_token":"b","expires_in":1}""");
        var client = BuildClient(handler);

        await client.RefreshAsync("the-refresh-tk", CancellationToken.None);

        Assert.Single(handler.CapturedBodies);
        Assert.Contains("\"refresh_token\":\"the-refresh-tk\"", handler.CapturedBodies[0]);
    }

    [Fact]
    public async Task RefreshAsync_throws_RefreshFailed_on_401()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.Unauthorized, "expired");
        var client = BuildClient(handler);

        await Assert.ThrowsAsync<AuthRefreshFailedException>(
            () => client.RefreshAsync("x", CancellationToken.None));
    }

    [Fact]
    public async Task RefreshAsync_throws_RefreshFailed_on_400()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.BadRequest, "invalid_grant");
        var client = BuildClient(handler);

        await Assert.ThrowsAsync<AuthRefreshFailedException>(
            () => client.RefreshAsync("x", CancellationToken.None));
    }

    // ─────────────────────────────────── LogoutAsync ──────────────────────────────

    [Fact]
    public async Task LogoutAsync_sends_post_with_bearer()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.NoContent, "");
        var client = BuildClient(handler);

        await client.LogoutAsync("the-access", CancellationToken.None);

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://example.supabase.co/auth/v1/logout", req.RequestUri!.ToString());
        Assert.Equal("the-access", req.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task LogoutAsync_swallows_http_errors_silently()
    {
        // Best-effort: el caller borra tokens locales aunque el server falle.
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.InternalServerError, "boom");
        var client = BuildClient(handler);

        // No tira. Si tira, el caller no podría loguear logout cuando hay network glitch.
        await client.LogoutAsync("x", CancellationToken.None);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
