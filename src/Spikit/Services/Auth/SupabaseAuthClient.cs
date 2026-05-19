using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Spikit.Services.Auth;

// Impl productiva del ISupabaseAuthClient. Headers comunes: `apikey: <publishable>`
// + `Authorization: Bearer <token>`. Supabase requiere ambos en endpoints de Auth.
public sealed class SupabaseAuthClient : ISupabaseAuthClient
{
    private const string UserPath = "/auth/v1/user";
    private const string TokenRefreshPath = "/auth/v1/token?grant_type=refresh_token";
    private const string LogoutPath = "/auth/v1/logout";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly SupabaseOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SupabaseAuthClient> _logger;

    public SupabaseAuthClient(
        HttpClient http,
        IOptions<SupabaseOptions> options,
        ILogger<SupabaseAuthClient> logger)
        : this(http, options, TimeProvider.System, logger)
    {
    }

    // Constructor extendido para tests (inyectar TimeProvider fake — el ExpiresAt
    // calculado durante el refresh depende de "ahora").
    public SupabaseAuthClient(
        HttpClient http,
        IOptions<SupabaseOptions> options,
        TimeProvider time,
        ILogger<SupabaseAuthClient> logger)
    {
        _http = http;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task<UserProfile> ValidateAccessTokenAsync(string accessToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(UserPath));
        request.Headers.TryAddWithoutValidation("apikey", _options.PublishableKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AuthTokenInvalidException(
                $"Supabase Auth respondió {(int)response.StatusCode} al validar access_token");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new AuthException(
                $"Supabase /auth/v1/user respondió {(int)response.StatusCode}: {Truncate(body, 200)}");
        }

        var parsed = Deserialize<UserResponse>(body);
        if (parsed is null || string.IsNullOrEmpty(parsed.Id) || string.IsNullOrEmpty(parsed.Email))
        {
            throw new AuthException("Supabase /auth/v1/user devolvió 200 pero sin id/email");
        }

        return new UserProfile(parsed.Id, parsed.Email);
    }

    public async Task<AccessTokenPair> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(TokenRefreshPath))
        {
            Content = JsonContent.Create(new { refresh_token = refreshToken }),
        };
        request.Headers.TryAddWithoutValidation("apikey", _options.PublishableKey);

        using var response = await SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            throw new AuthRefreshFailedException(
                $"Supabase rechazó el refresh_token ({(int)response.StatusCode}): {Truncate(body, 200)}");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new AuthException(
                $"Supabase /auth/v1/token respondió {(int)response.StatusCode}: {Truncate(body, 200)}");
        }

        var parsed = Deserialize<TokenResponse>(body);
        if (parsed is null
            || string.IsNullOrEmpty(parsed.AccessToken)
            || string.IsNullOrEmpty(parsed.RefreshToken)
            || parsed.ExpiresIn <= 0)
        {
            throw new AuthException("Supabase /auth/v1/token devolvió 200 pero faltan campos");
        }

        return new AccessTokenPair(
            parsed.AccessToken,
            parsed.RefreshToken,
            _time.GetUtcNow().AddSeconds(parsed.ExpiresIn));
    }

    public async Task LogoutAsync(string accessToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(LogoutPath));
        request.Headers.TryAddWithoutValidation("apikey", _options.PublishableKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            using var response = await SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Best-effort: el server pudo haber expirado ya el token o estar caído.
                // Logueamos y volvemos — los tokens locales se borran igual en el caller.
                _logger.LogWarning(
                    "Supabase /auth/v1/logout respondió {Status} — tokens locales se borran igual",
                    (int)response.StatusCode);
            }
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Logout server-side falló por red — tokens locales se borran igual");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Logout server-side timeout — tokens locales se borran igual");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new AuthException("Error de red contra Supabase Auth", ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new AuthException("Timeout contra Supabase Auth", ex);
        }
    }

    private Uri BuildUri(string path) => new(_options.BaseUrl.TrimEnd('/') + path);

    private static T? Deserialize<T>(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private sealed class UserResponse
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("email")] public string? Email { get; set; }
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }
}
