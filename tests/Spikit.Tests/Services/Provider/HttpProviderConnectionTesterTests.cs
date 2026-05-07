using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Services.Provider;
using Spikit.Tests.Services.Transcription;

namespace Spikit.Tests.Services.Provider;

public class HttpProviderConnectionTesterTests
{
    private static HttpProviderConnectionTester MakeTester(FakeHttpMessageHandler handler)
    {
        var http = new HttpClient(handler);
        return new HttpProviderConnectionTester(http, NullLogger<HttpProviderConnectionTester>.Instance);
    }

    [Fact]
    public async Task Returns_ok_when_response_is_2xx()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "{\"data\":[]}");
        var tester = MakeTester(handler);

        var result = await tester.TestAsync("https://api.openai.com/v1", "sk-test-key-123456789", CancellationToken.None);

        Assert.True(result.IsOk);
    }

    [Fact]
    public async Task Hits_models_endpoint_with_bearer_authorization()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "{}");
        var tester = MakeTester(handler);

        await tester.TestAsync("https://api.openai.com/v1", "sk-test-key-123", CancellationToken.None);

        Assert.Single(handler.Requests);
        var req = handler.Requests[0];
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("https://api.openai.com/v1/models", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
        Assert.Equal("sk-test-key-123", req.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task Trailing_slash_in_baseurl_does_not_double_slash_endpoint()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "{}");
        var tester = MakeTester(handler);

        await tester.TestAsync("https://api.openai.com/v1/", "sk-test-key-123", CancellationToken.None);

        Assert.Equal("https://api.openai.com/v1/models", handler.Requests[0].RequestUri!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Maps_401_and_403_to_invalid_key_message(HttpStatusCode status)
    {
        var handler = FakeHttpMessageHandler.Returning(status, "");
        var tester = MakeTester(handler);

        var result = await tester.TestAsync("https://api.openai.com/v1", "sk-test-key-123456789", CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Contains("API key inválida", result.Message);
    }

    [Fact]
    public async Task Maps_404_to_endpoint_not_responding()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.NotFound, "");
        var tester = MakeTester(handler);

        var result = await tester.TestAsync("https://api.openai.com/v1", "sk-test-key-123456789", CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Contains("Base URL", result.Message);
    }

    [Fact]
    public async Task Maps_other_4xx_to_generic_provider_error()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.BadRequest, "");
        var tester = MakeTester(handler);

        var result = await tester.TestAsync("https://api.openai.com/v1", "sk-test-key-123456789", CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Contains("400", result.Message);
    }

    [Fact]
    public async Task Maps_5xx_to_provider_error_message()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.InternalServerError, "");
        var tester = MakeTester(handler);

        var result = await tester.TestAsync("https://api.openai.com/v1", "sk-test-key-123456789", CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Contains("500", result.Message);
    }

    [Fact]
    public async Task Maps_network_failure_to_friendly_connectivity_message()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            throw new HttpRequestException("network down"));
        var tester = MakeTester(handler);

        var result = await tester.TestAsync("https://api.openai.com/v1", "sk-test-key-123456789", CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Contains("contactar el provider", result.Message);
    }

    [Fact]
    public async Task Returns_friendly_error_for_empty_baseurl()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "{}");
        var tester = MakeTester(handler);

        var result = await tester.TestAsync("", "sk-test-key-123456789", CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Empty(handler.Requests); // ni siquiera intentó la request
    }

    [Fact]
    public async Task Returns_friendly_error_for_empty_apikey()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "{}");
        var tester = MakeTester(handler);

        var result = await tester.TestAsync("https://api.openai.com/v1", "", CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Returns_friendly_error_for_invalid_baseurl_scheme()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "{}");
        var tester = MakeTester(handler);

        var result = await tester.TestAsync("not-a-url", "sk-test-key-123456789", CancellationToken.None);

        Assert.False(result.IsOk);
        Assert.Contains("http", result.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task External_cancellation_propagates()
    {
        var handler = new FakeHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var tester = MakeTester(handler);

        using var cts = new CancellationTokenSource();
        var task = tester.TestAsync("https://api.openai.com/v1", "sk-test-key-123456789", cts.Token);
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() => task);
    }
}
