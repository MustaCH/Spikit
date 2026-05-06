using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Spikit.Services.Transcription;

public sealed class WhisperApiTranscriptionService : ITranscriptionService
{
    private const string TranscriptionsPath = "/audio/transcriptions";

    private readonly HttpClient _http;
    private readonly WhisperApiOptions _options;
    private readonly WhisperApiKey _apiKey;
    private readonly ILogger<WhisperApiTranscriptionService> _logger;

    public WhisperApiTranscriptionService(
        HttpClient http,
        IOptions<WhisperApiOptions> options,
        WhisperApiKey apiKey,
        ILogger<WhisperApiTranscriptionService> logger)
    {
        _http = http;
        _options = options.Value;
        _apiKey = apiKey;
        _logger = logger;
    }

    public async Task<string> TranscribeAsync(byte[] wavData, CancellationToken ct)
    {
        if (wavData is null) throw new ArgumentNullException(nameof(wavData));
        if (wavData.Length == 0) throw new ArgumentException("WAV vacío", nameof(wavData));
        if (!_apiKey.IsConfigured)
        {
            throw new TranscriptionException(
                "OPENAI_API_KEY no está seteada. Configurala como variable de entorno (User scope).");
        }

        using var content = BuildMultipartContent(wavData);
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri())
        {
            Content = content,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey.Value);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new TranscriptionException("Error de red al contactar Whisper API", ex);
        }
        catch (TaskCanceledException ex)
        {
            // TaskCanceledException sin cancellation explícita = timeout del HttpClient.
            throw new TranscriptionException("Timeout al contactar Whisper API", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Whisper API respondió {StatusCode}. Body: {Body}",
                    (int)response.StatusCode, Truncate(body, 500));

                throw new TranscriptionException(
                    $"Whisper API respondió {(int)response.StatusCode} {response.ReasonPhrase}",
                    response.StatusCode,
                    body);
            }

            return ParseTranscription(body);
        }
    }

    private MultipartFormDataContent BuildMultipartContent(byte[] wavData)
    {
        var content = new MultipartFormDataContent();

        var fileContent = new ByteArrayContent(wavData);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", "audio.wav");

        content.Add(new StringContent(_options.Model), "model");

        if (!string.IsNullOrWhiteSpace(_options.Language))
        {
            content.Add(new StringContent(_options.Language), "language");
        }

        return content;
    }

    private Uri BuildUri()
    {
        var baseUrl = _options.BaseUrl.TrimEnd('/');
        return new Uri($"{baseUrl}{TranscriptionsPath}");
    }

    private static string ParseTranscription(string body)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<TranscriptionResponse>(body, JsonOptions);
            if (parsed?.Text is null)
            {
                throw new TranscriptionException(
                    "Whisper API devolvió 200 pero sin campo 'text' en el JSON",
                    responseBody: body);
            }
            return parsed.Text;
        }
        catch (JsonException ex)
        {
            throw new TranscriptionException("No se pudo parsear la respuesta de Whisper API", ex);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class TranscriptionResponse
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
