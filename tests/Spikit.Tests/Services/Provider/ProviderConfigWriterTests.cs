using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Models;
using Spikit.Services.Provider;
using Spikit.Services.Secrets;
using Spikit.Services.Settings;
using Spikit.Services.Transcription;

namespace Spikit.Tests.Services.Provider;

// Tests unitarios del writer con mocks in-memory. Validan el contrato transaccional
// (DPAPI primero, rollback si JsonSettings falla, reload runtime al final).
public class ProviderConfigWriterTests
{
    private static readonly ProviderConfig SampleConfig = new(
        PresetId: "openai",
        BaseUrl: "https://api.openai.com/v1",
        Model: "whisper-1",
        ApiKey: "sk-sample-key-12345678901234567890");

    private static (ProviderConfigWriter writer, FakeSecretStore secrets, FakeSettingsService settings, WhisperApiKey key, WhisperApiOptions options)
        Build()
    {
        var secrets = new FakeSecretStore();
        var settings = new FakeSettingsService();
        var key = new WhisperApiKey(string.Empty);
        var options = new WhisperApiOptions { BaseUrl = "stale", Model = "stale" };
        var writer = new ProviderConfigWriter(secrets, settings, key, options, NullLogger<ProviderConfigWriter>.Instance);
        return (writer, secrets, settings, key, options);
    }

    [Fact]
    public async Task SaveAsync_persists_secret_under_canonical_key()
    {
        var (writer, secrets, _, _, _) = Build();

        await writer.SaveAsync(SampleConfig);

        Assert.Equal(SampleConfig.ApiKey, secrets.Read(ProviderConfigWriter.ApiKeySecretName));
    }

    [Fact]
    public async Task SaveAsync_persists_provider_block_in_settings()
    {
        var (writer, _, settings, _, _) = Build();

        await writer.SaveAsync(SampleConfig);

        var saved = settings.Saved!.Provider;
        Assert.Equal(SampleConfig.PresetId, saved.PresetId);
        Assert.Equal(SampleConfig.BaseUrl, saved.BaseUrl);
        Assert.Equal(SampleConfig.Model, saved.Model);
    }

    [Fact]
    public async Task SaveAsync_updates_runtime_whisper_singletons()
    {
        var (writer, _, _, key, options) = Build();

        await writer.SaveAsync(SampleConfig);

        Assert.Equal(SampleConfig.ApiKey, key.Value);
        Assert.True(key.IsConfigured);
        Assert.Equal(SampleConfig.BaseUrl, options.BaseUrl);
        Assert.Equal(SampleConfig.Model, options.Model);
    }

    [Fact]
    public async Task SaveAsync_throws_ProviderConfigSaveException_when_dpapi_fails()
    {
        var (writer, secrets, settings, key, options) = Build();
        secrets.ThrowOnWrite = new InvalidOperationException("DPAPI nope");

        var ex = await Assert.ThrowsAsync<ProviderConfigSaveException>(() => writer.SaveAsync(SampleConfig));

        Assert.NotNull(ex.InnerException);
        // No tocar settings ni runtime cuando DPAPI rompió.
        Assert.Null(settings.Saved);
        Assert.Empty(key.Value);
        Assert.Equal("stale", options.BaseUrl);
    }

    [Fact]
    public async Task SaveAsync_rollbacks_secret_when_settings_fail()
    {
        var (writer, secrets, settings, key, options) = Build();
        settings.ThrowOnSave = new IOException("disk full");

        await Assert.ThrowsAsync<ProviderConfigSaveException>(() => writer.SaveAsync(SampleConfig));

        // El secret no debe quedar persistido; el runtime tampoco debe haberse refrescado.
        Assert.Null(secrets.Read(ProviderConfigWriter.ApiKeySecretName));
        Assert.Empty(key.Value);
        Assert.Equal("stale", options.BaseUrl);
        Assert.Equal("stale", options.Model);
    }

    [Fact]
    public async Task SaveAsync_does_not_clobber_unrelated_settings_sections()
    {
        var (writer, _, settings, _, _) = Build();
        settings.Existing = new AppSettings
        {
            Provider = new ProviderSettings { PresetId = "groq", BaseUrl = "old", Model = "old" },
        };

        await writer.SaveAsync(SampleConfig);

        Assert.NotNull(settings.Saved);
        Assert.Equal("openai", settings.Saved!.Provider.PresetId);
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _store = new();

        public Exception? ThrowOnWrite { get; set; }

        public string? Read(string key) => _store.TryGetValue(key, out var v) ? v : null;

        public void Write(string key, string value)
        {
            if (ThrowOnWrite is not null) throw ThrowOnWrite;
            _store[key] = value;
        }

        public void Delete(string key) => _store.Remove(key);
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings? Existing { get; set; }
        public AppSettings? Saved { get; private set; }
        public Exception? ThrowOnSave { get; set; }

        public AppSettings Load() => Existing ?? new AppSettings();

        public void Save(AppSettings settings)
        {
            if (ThrowOnSave is not null) throw ThrowOnSave;
            Saved = settings;
        }
    }
}
