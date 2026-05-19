using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spikit.Services.Auth;

namespace Spikit.Services.Billing;

// Impl productiva del IStripeBillingClient. Comparte el patrón de los otros clients de
// Supabase (apikey + Bearer headers, JSON snake_case, manejo explícito de 401).
public sealed class StripeBillingClient : IStripeBillingClient
{
    private const string CheckoutPath = "/functions/v1/create-checkout-session";
    private const string PortalPath = "/functions/v1/create-portal-session";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly SupabaseOptions _options;
    private readonly ILogger<StripeBillingClient> _logger;

    public StripeBillingClient(
        HttpClient http,
        IOptions<SupabaseOptions> options,
        ILogger<StripeBillingClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public Task<string> CreateCheckoutSessionAsync(string accessToken, string lookupKey, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lookupKey);
        return CallAsync(accessToken, CheckoutPath, new { lookup_key = lookupKey }, ct);
    }

    public Task<string> CreatePortalSessionAsync(string accessToken, CancellationToken ct) =>
        CallAsync(accessToken, PortalPath, body: null, ct);

    private async Task<string> CallAsync(
        string accessToken, string path, object? body, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(path));
        request.Headers.TryAddWithoutValidation("apikey", _options.PublishableKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) { throw new BillingException("Error de red contra Stripe billing", ex); }
        catch (TaskCanceledException ex) { throw new BillingException("Timeout contra Stripe billing", ex); }

        using (response)
        {
            var responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new AuthTokenInvalidException(
                    $"Stripe billing {path} respondió {(int)response.StatusCode}");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new BillingException(
                    $"Stripe billing {path} respondió {(int)response.StatusCode}: {Truncate(responseBody, 200)}");
            }

            var parsed = Deserialize<UrlResponse>(responseBody);
            if (parsed is null || string.IsNullOrEmpty(parsed.Url))
            {
                throw new BillingException(
                    $"Stripe billing {path} devolvió 200 pero sin campo `url`");
            }

            return parsed.Url;
        }
    }

    private Uri BuildUri(string path) => new(_options.BaseUrl.TrimEnd('/') + path);

    private static T? Deserialize<T>(string body)
    {
        try { return JsonSerializer.Deserialize<T>(body, JsonOptions); }
        catch (JsonException) { return default; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed class UrlResponse
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
    }
}
