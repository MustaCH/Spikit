using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Spikit.Services.Auth;
using Spikit.Services.Billing;
using Spikit.Tests.Services.Transcription;

namespace Spikit.Tests.Services.Billing;

public class StripeBillingClientTests
{
    private readonly SupabaseOptions _options = new()
    {
        BaseUrl = "https://example.supabase.co",
        PublishableKey = "pk_test",
    };

    private StripeBillingClient BuildClient(FakeHttpMessageHandler handler) =>
        new(new HttpClient(handler), Options.Create(_options), NullLogger<StripeBillingClient>.Instance);

    // ───────────────────────────── CreateCheckoutSessionAsync ─────────────────────

    [Fact]
    public async Task CreateCheckoutSessionAsync_returns_url_from_response()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK,
            """{"url":"https://checkout.stripe.com/c/pay/abc123"}""");
        var client = BuildClient(handler);

        var url = await client.CreateCheckoutSessionAsync("tk", "pro_monthly", CancellationToken.None);

        Assert.Equal("https://checkout.stripe.com/c/pay/abc123", url);
    }

    [Fact]
    public async Task CreateCheckoutSessionAsync_sends_apikey_bearer_and_lookup_key_in_body()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK,
            """{"url":"https://x"}""");
        var client = BuildClient(handler);

        await client.CreateCheckoutSessionAsync("tk-zzz", "pro_yearly", CancellationToken.None);

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://example.supabase.co/functions/v1/create-checkout-session",
            req.RequestUri!.ToString());
        Assert.Equal("tk-zzz", req.Headers.Authorization!.Parameter);
        Assert.True(req.Headers.Contains("apikey"));
        Assert.Single(handler.CapturedBodies);
        Assert.Contains("\"lookup_key\":\"pro_yearly\"", handler.CapturedBodies[0]);
    }

    [Fact]
    public async Task CreateCheckoutSessionAsync_throws_TokenInvalid_on_401()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.Unauthorized, "");
        var client = BuildClient(handler);

        await Assert.ThrowsAsync<AuthTokenInvalidException>(
            () => client.CreateCheckoutSessionAsync("tk", "pro_monthly", CancellationToken.None));
    }

    [Fact]
    public async Task CreateCheckoutSessionAsync_throws_Billing_on_500()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.InternalServerError, "boom");
        var client = BuildClient(handler);

        await Assert.ThrowsAsync<BillingException>(
            () => client.CreateCheckoutSessionAsync("tk", "pro_monthly", CancellationToken.None));
    }

    [Fact]
    public async Task CreateCheckoutSessionAsync_throws_Billing_when_response_missing_url()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, """{"foo":"bar"}""");
        var client = BuildClient(handler);

        await Assert.ThrowsAsync<BillingException>(
            () => client.CreateCheckoutSessionAsync("tk", "pro_monthly", CancellationToken.None));
    }

    // ────────────────────────────── CreatePortalSessionAsync ──────────────────────

    [Fact]
    public async Task CreatePortalSessionAsync_returns_url_from_response()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK,
            """{"url":"https://billing.stripe.com/p/sess/xyz"}""");
        var client = BuildClient(handler);

        var url = await client.CreatePortalSessionAsync("tk", CancellationToken.None);

        Assert.Equal("https://billing.stripe.com/p/sess/xyz", url);
    }

    [Fact]
    public async Task CreatePortalSessionAsync_sends_post_with_no_body()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, """{"url":"https://x"}""");
        var client = BuildClient(handler);

        await client.CreatePortalSessionAsync("tk", CancellationToken.None);

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://example.supabase.co/functions/v1/create-portal-session",
            req.RequestUri!.ToString());
        Assert.Null(req.Content);
    }
}
