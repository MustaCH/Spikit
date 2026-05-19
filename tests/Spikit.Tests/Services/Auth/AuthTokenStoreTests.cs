using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Services.Auth;
using Spikit.Services.Secrets;

namespace Spikit.Tests.Services.Auth;

public class AuthTokenStoreTests
{
    private readonly FakeSecretStore _secrets = new();

    private AuthTokenStore BuildStore() =>
        new(_secrets, NullLogger<AuthTokenStore>.Instance);

    [Fact]
    public void Read_returns_null_when_secret_missing()
    {
        Assert.Null(BuildStore().Read());
    }

    [Fact]
    public void Write_then_Read_roundtrips_all_fields()
    {
        var pair = new AccessTokenPair(
            "access-xyz",
            "refresh-abc",
            new DateTimeOffset(2026, 06, 01, 12, 00, 00, TimeSpan.Zero));

        var store = BuildStore();
        store.Write(pair);

        var read = store.Read();
        Assert.NotNull(read);
        Assert.Equal("access-xyz", read!.AccessToken);
        Assert.Equal("refresh-abc", read.RefreshToken);
        Assert.Equal(pair.ExpiresAt, read.ExpiresAt);
    }

    [Fact]
    public void Clear_removes_persisted_pair()
    {
        var pair = new AccessTokenPair("a", "b", DateTimeOffset.UtcNow);
        var store = BuildStore();
        store.Write(pair);

        store.Clear();

        Assert.Null(store.Read());
    }

    [Fact]
    public void Read_returns_null_when_persisted_payload_is_garbage()
    {
        // Si el archivo cifrado existe pero contiene basura (versión vieja del formato,
        // corrupción), tratamos como ausente — el caller dispara re-login.
        _secrets.Write(AuthTokenStore.SecretKey, "not-json");

        Assert.Null(BuildStore().Read());
    }

    // Fake in-memory de ISecretStore. No cifra, no toca filesystem — solo verifica
    // que el store usa la key correcta y maneja la serialización JSON sin sorpresas.
    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _store = new();
        public string? Read(string key) => _store.TryGetValue(key, out var v) ? v : null;
        public void Write(string key, string value) => _store[key] = value;
        public void Delete(string key) => _store.Remove(key);
    }
}
