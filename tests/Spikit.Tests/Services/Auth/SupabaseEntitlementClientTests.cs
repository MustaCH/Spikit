using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Spikit.Services.Auth;
using Spikit.Tests.Services.Transcription;

namespace Spikit.Tests.Services.Auth;

public class SupabaseEntitlementClientTests
{
    private readonly SupabaseOptions _options = new()
    {
        BaseUrl = "https://example.supabase.co",
        PublishableKey = "pk_test",
    };

    private SupabaseEntitlementClient BuildClient(FakeHttpMessageHandler handler) =>
        new(new HttpClient(handler), Options.Create(_options),
            NullLogger<SupabaseEntitlementClient>.Instance);

    [Fact]
    public async Task FetchAsync_parses_trial_response()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK,
            """
            {
              "tier": "trial",
              "trial_ends_at": "2026-06-02T00:00:00Z",
              "pro_renews_at": null,
              "byok_grace_ends_at": null,
              "minutes_used_period": 12
            }
            """);
        var client = BuildClient(handler);

        var ent = await client.FetchAsync("access", CancellationToken.None);

        Assert.Equal(Tier.Trial, ent.Tier);
        Assert.Equal(new DateTimeOffset(2026, 06, 02, 0, 0, 0, TimeSpan.Zero), ent.TrialEndsAt);
        Assert.Null(ent.ProRenewsAt);
        Assert.Null(ent.ByokGraceEndsAt);
        Assert.Equal(12, ent.MinutesUsedPeriod);
    }

    [Theory]
    [InlineData("trial", Tier.Trial)]
    [InlineData("pro", Tier.Pro)]
    [InlineData("byok", Tier.Byok)]
    [InlineData("expired", Tier.Expired)]
    public async Task FetchAsync_maps_all_known_tiers(string raw, Tier expected)
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK,
            $$"""
            {"tier":"{{raw}}","trial_ends_at":null,"pro_renews_at":null,"byok_grace_ends_at":null,"minutes_used_period":0}
            """);
        var client = BuildClient(handler);

        var ent = await client.FetchAsync("access", CancellationToken.None);

        Assert.Equal(expected, ent.Tier);
    }

    [Fact]
    public async Task FetchAsync_sends_apikey_and_bearer()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK,
            """{"tier":"trial","trial_ends_at":null,"pro_renews_at":null,"byok_grace_ends_at":null,"minutes_used_period":0}""");
        var client = BuildClient(handler);

        await client.FetchAsync("access-tk", CancellationToken.None);

        var req = handler.Requests.Single();
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("https://example.supabase.co/functions/v1/entitlement", req.RequestUri!.ToString());
        Assert.Equal("access-tk", req.Headers.Authorization!.Parameter);
        Assert.True(req.Headers.Contains("apikey"));
    }

    [Fact]
    public async Task FetchAsync_throws_TokenInvalid_on_401()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.Unauthorized, "");
        var client = BuildClient(handler);

        await Assert.ThrowsAsync<AuthTokenInvalidException>(
            () => client.FetchAsync("x", CancellationToken.None));
    }

    [Fact]
    public async Task FetchAsync_throws_AuthException_for_unknown_tier()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK,
            """{"tier":"unknown_state","trial_ends_at":null,"pro_renews_at":null,"byok_grace_ends_at":null,"minutes_used_period":0}""");
        var client = BuildClient(handler);

        await Assert.ThrowsAsync<AuthException>(
            () => client.FetchAsync("x", CancellationToken.None));
    }
}
