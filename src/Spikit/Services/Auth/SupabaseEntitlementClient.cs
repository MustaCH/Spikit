using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Spikit.Services.Auth;

// Impl productiva del ISupabaseEntitlementClient. La Edge Function `entitlement`
// tiene `verify_jwt=true` → Bearer accessToken obligatorio.
public sealed class SupabaseEntitlementClient : ISupabaseEntitlementClient
{
    private const string EntitlementPath = "/functions/v1/entitlement";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly SupabaseOptions _options;
    private readonly ILogger<SupabaseEntitlementClient> _logger;

    public SupabaseEntitlementClient(
        HttpClient http,
        IOptions<SupabaseOptions> options,
        ILogger<SupabaseEntitlementClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Entitlement> FetchAsync(string accessToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(EntitlementPath));
        request.Headers.TryAddWithoutValidation("apikey", _options.PublishableKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex) { throw new AuthException("Error de red contra /functions/v1/entitlement", ex); }
        catch (TaskCanceledException ex) { throw new AuthException("Timeout contra /functions/v1/entitlement", ex); }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new AuthTokenInvalidException(
                    $"/functions/v1/entitlement respondió {(int)response.StatusCode}");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AuthException(
                    $"/functions/v1/entitlement respondió {(int)response.StatusCode}: {Truncate(body, 200)}");
            }

            return Parse(body);
        }
    }

    private static Entitlement Parse(string body)
    {
        EntitlementResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<EntitlementResponse>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new AuthException("No se pudo parsear la respuesta de /functions/v1/entitlement", ex);
        }

        if (parsed is null || string.IsNullOrEmpty(parsed.Tier))
        {
            throw new AuthException("/functions/v1/entitlement devolvió 200 sin campo tier");
        }

        var tier = parsed.Tier.ToLowerInvariant() switch
        {
            "trial" => Tier.Trial,
            "pro" => Tier.Pro,
            "byok" => Tier.Byok,
            "expired" => Tier.Expired,
            _ => throw new AuthException($"/functions/v1/entitlement devolvió tier desconocido: {parsed.Tier}"),
        };

        return new Entitlement(
            tier,
            parsed.TrialEndsAt,
            parsed.ProRenewsAt,
            parsed.ByokGraceEndsAt,
            parsed.MinutesUsedPeriod);
    }

    private Uri BuildUri(string path) => new(_options.BaseUrl.TrimEnd('/') + path);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed class EntitlementResponse
    {
        [JsonPropertyName("tier")] public string? Tier { get; set; }
        [JsonPropertyName("trial_ends_at")] public DateTimeOffset? TrialEndsAt { get; set; }
        [JsonPropertyName("pro_renews_at")] public DateTimeOffset? ProRenewsAt { get; set; }
        [JsonPropertyName("byok_grace_ends_at")] public DateTimeOffset? ByokGraceEndsAt { get; set; }
        [JsonPropertyName("minutes_used_period")] public int MinutesUsedPeriod { get; set; }
    }
}
