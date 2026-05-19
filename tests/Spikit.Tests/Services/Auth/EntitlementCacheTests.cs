using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Services.Auth;
using Spikit.Services.Secrets;

namespace Spikit.Tests.Services.Auth;

public class EntitlementCacheTests
{
    private readonly FakeSecretStore _secrets = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 05, 19, 12, 00, 00, TimeSpan.Zero));

    private EntitlementCache BuildCache(TimeSpan? ttl = null) =>
        new(_secrets, _time, ttl ?? EntitlementCache.DefaultTtl, NullLogger<EntitlementCache>.Instance);

    private static Entitlement SampleTrial() => new(
        Tier.Trial,
        TrialEndsAt: new DateTimeOffset(2026, 06, 02, 0, 0, 0, TimeSpan.Zero),
        ProRenewsAt: null,
        ByokGraceEndsAt: null,
        MinutesUsedPeriod: 0);

    [Fact]
    public void ReadFresh_returns_null_when_empty()
    {
        Assert.Null(BuildCache().ReadFresh());
    }

    [Fact]
    public void Write_then_ReadFresh_returns_same_entitlement()
    {
        var cache = BuildCache();
        var ent = SampleTrial();

        cache.Write(ent);

        var read = cache.ReadFresh();
        Assert.NotNull(read);
        Assert.Equal(Tier.Trial, read!.Tier);
        Assert.Equal(ent.TrialEndsAt, read.TrialEndsAt);
    }

    [Fact]
    public void ReadFresh_returns_null_after_TTL_expires()
    {
        var cache = BuildCache(TimeSpan.FromHours(1));
        cache.Write(SampleTrial());

        _time.Advance(TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(1)));

        Assert.Null(cache.ReadFresh());
    }

    [Fact]
    public void ReadFresh_returns_value_at_TTL_boundary_minus_1s()
    {
        var cache = BuildCache(TimeSpan.FromHours(1));
        cache.Write(SampleTrial());

        _time.Advance(TimeSpan.FromHours(1).Subtract(TimeSpan.FromSeconds(1)));

        Assert.NotNull(cache.ReadFresh());
    }

    [Fact]
    public void ReadStale_returns_value_even_when_TTL_expired()
    {
        var cache = BuildCache(TimeSpan.FromMinutes(5));
        cache.Write(SampleTrial());

        _time.Advance(TimeSpan.FromHours(24));

        var stale = cache.ReadStale();
        Assert.NotNull(stale);
        Assert.Equal(Tier.Trial, stale!.Tier);
    }

    [Fact]
    public void Clear_removes_both_fresh_and_stale_reads()
    {
        var cache = BuildCache();
        cache.Write(SampleTrial());

        cache.Clear();

        Assert.Null(cache.ReadFresh());
        Assert.Null(cache.ReadStale());
    }

    [Fact]
    public void Write_updates_the_cachedAt_timestamp()
    {
        var cache = BuildCache(TimeSpan.FromMinutes(10));
        cache.Write(SampleTrial());
        _time.Advance(TimeSpan.FromMinutes(9));

        // Aún fresh — todo OK.
        Assert.NotNull(cache.ReadFresh());

        // Sobrescribimos: el timestamp se renueva → vuelve a estar fresh por otros 10 min.
        cache.Write(SampleTrial());
        _time.Advance(TimeSpan.FromMinutes(9));

        Assert.NotNull(cache.ReadFresh());
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _store = new();
        public string? Read(string key) => _store.TryGetValue(key, out var v) ? v : null;
        public void Write(string key, string value) => _store[key] = value;
        public void Delete(string key) => _store.Remove(key);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;
        public FakeTimeProvider(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
