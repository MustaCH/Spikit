using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spikit.Services.Secrets;

namespace Spikit.Services.Auth;

// Impl productiva del IAuthTokenStore. Serializa el par a JSON y lo escribe vía
// ISecretStore (DPAPI). Reusa el mismo store que usa la BYOK key — distinta `key`
// canónica, mismos garantías de scoping al usuario de Windows actual.
public sealed class AuthTokenStore : IAuthTokenStore
{
    internal const string SecretKey = "auth.tokens";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ISecretStore _secrets;
    private readonly ILogger<AuthTokenStore> _logger;

    public AuthTokenStore(ISecretStore secrets, ILogger<AuthTokenStore> logger)
    {
        _secrets = secrets;
        _logger = logger;
    }

    public AccessTokenPair? Read()
    {
        var raw = _secrets.Read(SecretKey);
        if (string.IsNullOrEmpty(raw)) return null;

        try
        {
            return JsonSerializer.Deserialize<AccessTokenPair>(raw, JsonOptions);
        }
        catch (JsonException ex)
        {
            // Archivo cifrado pero contenido inválido — probablemente formato viejo o
            // corrupción. Tratamos como ausencia para que el flow dispare re-login.
            _logger.LogWarning(ex, "Tokens en DPAPI no parseables — re-login requerido");
            return null;
        }
    }

    public void Write(AccessTokenPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        _secrets.Write(SecretKey, JsonSerializer.Serialize(pair, JsonOptions));
    }

    public void Clear() => _secrets.Delete(SecretKey);
}
