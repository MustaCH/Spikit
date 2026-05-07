using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Services.Secrets;

namespace Spikit.Tests.Services.Secrets;

// Tests de integration: DPAPI real + filesystem real (testing-strategy.md "DpapiSecretStore
// cifrando/descifrando contra Windows real"). Solo corren en Windows (DPAPI es API de Win32).
[Trait("Category", "Integration")]
public class DpapiSecretStoreTests : IDisposable
{
    private readonly string _tmpDir;

    public DpapiSecretStoreTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "spikit-secret-tests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmpDir))
        {
            Directory.Delete(_tmpDir, recursive: true);
        }
    }

    private DpapiSecretStore MakeStore() => new(_tmpDir, NullLogger<DpapiSecretStore>.Instance);

    [Fact]
    public void Read_returns_null_when_key_not_set()
    {
        var store = MakeStore();

        Assert.Null(store.Read("provider.apiKey"));
    }

    [Fact]
    public void Write_then_Read_roundtrips_value()
    {
        var store = MakeStore();
        const string secret = "sk-roundtrip-test-1234567890";

        store.Write("provider.apiKey", secret);
        var loaded = store.Read("provider.apiKey");

        Assert.Equal(secret, loaded);
    }

    [Fact]
    public void Write_persists_encrypted_payload_not_plaintext()
    {
        var store = MakeStore();
        const string secret = "sk-encrypted-payload-test-1234567890";

        store.Write("provider.apiKey", secret);

        // El archivo en disco no debe contener el plaintext (bytes UTF-8 no-ASCII en el cifrado).
        var raw = File.ReadAllBytes(Path.Combine(_tmpDir, "provider.apiKey.bin"));
        var asString = System.Text.Encoding.UTF8.GetString(raw);
        Assert.DoesNotContain(secret, asString);
    }

    [Fact]
    public void Write_overwrites_previous_value()
    {
        var store = MakeStore();

        store.Write("provider.apiKey", "first-value-1234567890");
        store.Write("provider.apiKey", "second-value-1234567890");

        Assert.Equal("second-value-1234567890", store.Read("provider.apiKey"));
    }

    [Fact]
    public void Delete_removes_existing_secret()
    {
        var store = MakeStore();
        store.Write("provider.apiKey", "to-be-deleted-1234567890");

        store.Delete("provider.apiKey");

        Assert.Null(store.Read("provider.apiKey"));
    }

    [Fact]
    public void Delete_is_noop_when_key_does_not_exist()
    {
        var store = MakeStore();

        var ex = Record.Exception(() => store.Delete("non-existent.key"));

        Assert.Null(ex);
    }

    [Fact]
    public void Sanitizes_keys_with_invalid_filename_chars()
    {
        var store = MakeStore();

        store.Write("with/slash:and|other*chars", "ok-1234567890");
        Assert.Equal("ok-1234567890", store.Read("with/slash:and|other*chars"));
    }

    [Fact]
    public void Empty_key_throws()
    {
        var store = MakeStore();

        Assert.Throws<ArgumentException>(() => store.Write("", "value"));
    }
}
