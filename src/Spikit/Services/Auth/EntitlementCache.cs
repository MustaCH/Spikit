using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spikit.Services.Secrets;

namespace Spikit.Services.Auth;

// Impl productiva del IEntitlementCache. Persiste en DPAPI (vía ISecretStore) un
// envelope JSON con el Entitlement + timestamp. TTL fijo a 24h (ADR-0007 § 8).
public sealed class EntitlementCache : IEntitlementCache
{
    internal const string SecretKey = "auth.entitlement";

    public static readonly TimeSpan DefaultTtl = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ISecretStore _secrets;
    private readonly TimeProvider _time;
    private readonly TimeSpan _ttl;
    private readonly ILogger<EntitlementCache> _logger;

    public EntitlementCache(ISecretStore secrets, ILogger<EntitlementCache> logger)
        : this(secrets, TimeProvider.System, DefaultTtl, logger)
    {
    }

    // Constructor extendido para tests (inyectar TimeProvider fake + TTL custom).
    public EntitlementCache(ISecretStore secrets, TimeProvider time, TimeSpan ttl, ILogger<EntitlementCache> logger)
    {
        _secrets = secrets;
        _time = time;
        _ttl = ttl;
        _logger = logger;
    }

    public Entitlement? ReadFresh()
    {
        var envelope = ReadEnvelope();
        if (envelope is null) return null;

        var age = _time.GetUtcNow() - envelope.CachedAt;
        if (age >= _ttl) return null;
        return envelope.Entitlement;
    }

    public Entitlement? ReadStale() => ReadEnvelope()?.Entitlement;

    public void Write(Entitlement entitlement)
    {
        ArgumentNullException.ThrowIfNull(entitlement);
        var envelope = new CacheEnvelope(entitlement, _time.GetUtcNow());
        _secrets.Write(SecretKey, JsonSerializer.Serialize(envelope, JsonOptions));
    }

    public void Clear() => _secrets.Delete(SecretKey);

    private CacheEnvelope? ReadEnvelope()
    {
        var raw = _secrets.Read(SecretKey);
        if (string.IsNullOrEmpty(raw)) return null;

        try
        {
            return JsonSerializer.Deserialize<CacheEnvelope>(raw, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Entitlement cache no parseable — tratado como ausente");
            return null;
        }
    }

    // Envelope serializado: el Entitlement + cuándo se cacheó. No es público porque
    // los callers no necesitan saber del wrapping.
    private sealed record CacheEnvelope(Entitlement Entitlement, DateTimeOffset CachedAt);
}
