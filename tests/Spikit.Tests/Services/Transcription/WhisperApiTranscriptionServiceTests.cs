using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Spikit.Services.Transcription;

namespace Spikit.Tests.Services.Transcription;

public class WhisperApiTranscriptionServiceTests
{
    private static readonly byte[] DummyWav = new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0 };

    private static WhisperApiTranscriptionService MakeService(
        FakeHttpMessageHandler handler,
        WhisperApiOptions? options = null,
        string apiKey = "sk-test-key-123")
    {
        var http = new HttpClient(handler) { BaseAddress = null };
        var opts = Options.Create(options ?? new WhisperApiOptions());
        var key = new WhisperApiKey(apiKey);
        return new WhisperApiTranscriptionService(http, opts, key, NullLogger<WhisperApiTranscriptionService>.Instance);
    }

    [Fact]
    public async Task Returns_text_field_from_successful_response()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, """{"text":"hola mundo"}""");
        var service = MakeService(handler);

        var result = await service.TranscribeAsync(DummyWav, CancellationToken.None);

        Assert.Equal("hola mundo", result);
    }

    [Fact]
    public async Task Posts_to_audio_transcriptions_endpoint()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, """{"text":""}""");
        var service = MakeService(handler, new WhisperApiOptions { BaseUrl = "https://example.test/v1" });

        await service.TranscribeAsync(DummyWav, CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("https://example.test/v1/audio/transcriptions", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Sends_bearer_authorization_header()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, """{"text":""}""");
        var service = MakeService(handler, apiKey: "sk-fake-abc");

        await service.TranscribeAsync(DummyWav, CancellationToken.None);

        var auth = handler.Requests[0].Headers.Authorization;
        Assert.NotNull(auth);
        Assert.Equal("Bearer", auth!.Scheme);
        Assert.Equal("sk-fake-abc", auth.Parameter);
    }

    [Fact]
    public async Task Multipart_body_includes_model_and_file_fields()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, """{"text":""}""");
        var service = MakeService(handler, new WhisperApiOptions { Model = "whisper-1" });

        await service.TranscribeAsync(DummyWav, CancellationToken.None);

        var body = handler.CapturedBodies[0];
        Assert.Matches(@"name=""?model""?", body);
        Assert.Contains("whisper-1", body);
        Assert.Matches(@"name=""?file""?", body);
        Assert.Contains("audio.wav", body);
    }

    [Fact]
    public async Task Multipart_includes_language_when_set()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, """{"text":""}""");
        var service = MakeService(handler, new WhisperApiOptions { Language = "es" });

        await service.TranscribeAsync(DummyWav, CancellationToken.None);

        Assert.Matches(@"name=""?language""?", handler.CapturedBodies[0]);
    }

    [Fact]
    public async Task Multipart_omits_language_when_null()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, """{"text":""}""");
        var service = MakeService(handler, new WhisperApiOptions { Language = null });

        await service.TranscribeAsync(DummyWav, CancellationToken.None);

        Assert.DoesNotMatch(@"name=""?language""?", handler.CapturedBodies[0]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Throws_TranscriptionException_with_status_on_error(HttpStatusCode status)
    {
        var handler = FakeHttpMessageHandler.Returning(status, """{"error":{"message":"nope"}}""");
        var service = MakeService(handler);

        var ex = await Assert.ThrowsAsync<TranscriptionException>(
            () => service.TranscribeAsync(DummyWav, CancellationToken.None));

        Assert.Equal(status, ex.StatusCode);
        Assert.Contains("nope", ex.ResponseBody);
    }

    [Fact]
    public async Task Throws_when_response_lacks_text_field()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, """{"foo":"bar"}""");
        var service = MakeService(handler);

        var ex = await Assert.ThrowsAsync<TranscriptionException>(
            () => service.TranscribeAsync(DummyWav, CancellationToken.None));
        Assert.Contains("'text'", ex.Message);
    }

    [Fact]
    public async Task Throws_when_response_is_not_json()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, "not json", "text/plain");
        var service = MakeService(handler);

        await Assert.ThrowsAsync<TranscriptionException>(
            () => service.TranscribeAsync(DummyWav, CancellationToken.None));
    }

    [Fact]
    public async Task Throws_when_api_key_not_configured()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, """{"text":""}""");
        var service = MakeService(handler, apiKey: "");

        var ex = await Assert.ThrowsAsync<TranscriptionException>(
            () => service.TranscribeAsync(DummyWav, CancellationToken.None));
        Assert.Contains("OPENAI_API_KEY", ex.Message);
    }

    [Fact]
    public async Task Throws_on_null_or_empty_wav()
    {
        var handler = FakeHttpMessageHandler.Returning(HttpStatusCode.OK, """{"text":""}""");
        var service = MakeService(handler);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => service.TranscribeAsync(null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.TranscribeAsync(Array.Empty<byte>(), CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var handler = new FakeHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var service = MakeService(handler);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => service.TranscribeAsync(DummyWav, cts.Token));
    }
}
