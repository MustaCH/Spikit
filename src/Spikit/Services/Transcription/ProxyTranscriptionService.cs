using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spikit.Services.Auth;

namespace Spikit.Services.Transcription;

// Impl de ITranscriptionService para users tier=Trial / Pro. Sube el WAV al Edge
// Function `transcribe` (ADR-0007 § 5) con Bearer JWT — el server proxy a OpenAI con
// la key gestionada de Spikit (que nunca toca el cliente).
//
// El multipart usa la field name `audio` (no `file` como OpenAI directo); model y
// language los decide el server.
//
// Errores específicos del proxy:
//   401  → AuthTokenInvalidException (el caller puede intentar ForceRefresh + retry).
//   402  → SubscriptionRequiredException (tier no autoriza, UI muestra CTA upgrade).
//   demás → TranscriptionException con StatusCode/body para diagnóstico.
public sealed class ProxyTranscriptionService : ITranscriptionService
{
    private const string TranscribePath = "/functions/v1/transcribe";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly SupabaseOptions _options;
    private readonly IAuthService _auth;
    private readonly ILogger<ProxyTranscriptionService> _logger;

    public ProxyTranscriptionService(
        HttpClient http,
        IOptions<SupabaseOptions> options,
        IAuthService auth,
        ILogger<ProxyTranscriptionService> logger)
    {
        _http = http;
        _options = options.Value;
        _auth = auth;
        _logger = logger;
    }

    public async Task<string> TranscribeAsync(byte[] wavData, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(wavData);
        if (wavData.Length == 0) throw new ArgumentException("WAV vacío", nameof(wavData));

        var accessToken = await _auth.GetCurrentAccessTokenAsync(ct).ConfigureAwait(false);
        if (accessToken is null)
        {
            throw new AuthTokenInvalidException(
                "No hay sesión activa — el cliente no puede llamar /transcribe sin un JWT");
        }

        try
        {
            return await CallProxyAsync(accessToken, wavData, ct).ConfigureAwait(false);
        }
        catch (AuthTokenInvalidException)
        {
            // El server rechazó el token a pesar de que GetCurrentAccessToken lo daba por
            // válido (clock skew, revocación post-issue, etc.). Forzamos un refresh limpio
            // y reintentamos UNA vez. Si el segundo intento también es 401, propagamos.
            _logger.LogInformation("Proxy 401 con token aparentemente fresco — intentando force-refresh + retry");
            var refreshed = await _auth.ForceRefreshAccessTokenAsync(ct).ConfigureAwait(false);
            if (refreshed is null)
            {
                throw;
            }
            return await CallProxyAsync(refreshed, wavData, ct).ConfigureAwait(false);
        }
    }

    private async Task<string> CallProxyAsync(string accessToken, byte[] wavData, CancellationToken ct)
    {
        using var multipart = new MultipartFormDataContent();
        var audioContent = new ByteArrayContent(wavData);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        // El server lee `form.get('audio')` — el nombre del field es contractual.
        multipart.Add(audioContent, "audio", "audio.wav");

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(TranscribePath))
        {
            Content = multipart,
        };
        request.Headers.TryAddWithoutValidation("apikey", _options.PublishableKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex)
        {
            throw new TranscriptionException("Error de red al contactar el proxy /transcribe", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new TranscriptionException("Timeout al contactar el proxy /transcribe", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new AuthTokenInvalidException(
                    $"Proxy /transcribe rechazó el JWT (body: {Truncate(body, 200)})");
            }

            if (response.StatusCode == HttpStatusCode.PaymentRequired)
            {
                throw new SubscriptionRequiredException(
                    "El proxy /transcribe respondió 402 — tu suscripción no autoriza la transcripción.",
                    responseBody: body);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Proxy /transcribe respondió {Status}. Body: {Body}",
                    (int)response.StatusCode, Truncate(body, 500));
                throw new TranscriptionException(
                    $"Proxy /transcribe respondió {(int)response.StatusCode} {response.ReasonPhrase}",
                    response.StatusCode,
                    body);
            }

            return ParseTranscription(body);
        }
    }

    private Uri BuildUri(string path) => new(_options.BaseUrl.TrimEnd('/') + path);

    private static string ParseTranscription(string body)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<TranscriptionResponse>(body, JsonOptions);
            if (parsed?.Text is null)
            {
                throw new TranscriptionException(
                    "Proxy devolvió 200 pero sin campo `text` en el JSON",
                    responseBody: body);
            }
            return parsed.Text;
        }
        catch (JsonException ex)
        {
            throw new TranscriptionException("No se pudo parsear la respuesta del proxy", ex);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed class TranscriptionResponse
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
    }
}
