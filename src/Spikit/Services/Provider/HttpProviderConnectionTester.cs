using System.Net.Http;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

namespace Spikit.Services.Provider;

public sealed class HttpProviderConnectionTester : IProviderConnectionTester
{
    // Más corto que el del WhisperApiTranscriptionService (30s) porque acá hay un usuario
    // esperando y "probando" 30s frente a un input freezeado se siente roto.
    public static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly ILogger<HttpProviderConnectionTester> _logger;

    public HttpProviderConnectionTester(HttpClient http, ILogger<HttpProviderConnectionTester> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<ProviderConnectionResult> TestAsync(string baseUrl, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return ProviderConnectionResult.Failed("La Base URL está vacía.");
        }
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ProviderConnectionResult.Failed("La API key está vacía.");
        }
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return ProviderConnectionResult.Failed("La Base URL no es válida. Tiene que empezar con http:// o https://.");
        }

        var url = baseUrl.TrimEnd('/') + "/models";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TestTimeout);

        try
        {
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return ProviderConnectionResult.Ok();
            }
            return ProviderConnectionResult.Failed(MapErrorMessage(response));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelación externa (ej. usuario cerró la window). Re-throw para que el caller
            // diferencie de un timeout interno.
            throw;
        }
        catch (OperationCanceledException)
        {
            // CancelAfter del timeout se disparó.
            return ProviderConnectionResult.Failed("Timeout — el provider tardó demasiado en responder.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Connection test failed para {Url}", url);
            return ProviderConnectionResult.Failed("No pude contactar el provider. Revisá tu conexión o la Base URL.");
        }
    }

    private static string MapErrorMessage(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        return status switch
        {
            401 or 403 => "API key inválida o sin permisos.",
            404 => "Endpoint no responde — revisá la Base URL.",
            >= 400 and < 500 => $"Error del provider: {status} {response.ReasonPhrase}.",
            >= 500 => $"El provider devolvió un error: {status} {response.ReasonPhrase}.",
            _ => "Respuesta inesperada del provider.",
        };
    }
}
