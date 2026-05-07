using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Services.Provider;
using Spikit.Services.Secrets;
using Spikit.Services.Settings;
using Spikit.Services.Transcription;

namespace Spikit.Tests.Services.Provider;

// Integration test pedido explícitamente por el ticket EP-3.4:
//   "round-trip DPAPI → JsonSettings → reload del Whisper".
// Usa DPAPI + filesystem reales contra un tmpdir aislado. Verifica que post-guardado los
// singletons compartidos (WhisperApiKey + WhisperApiOptions) reflejen lo persistido.
[Trait("Category", "Integration")]
public class ProviderConfigWriterIntegrationTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _settingsPath;
    private readonly string _secretsDir;

    public ProviderConfigWriterIntegrationTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "spikit-writer-integration-" + Guid.NewGuid().ToString("N"));
        _settingsPath = Path.Combine(_tmpDir, "settings.json");
        _secretsDir = Path.Combine(_tmpDir, "secrets");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tmpDir))
        {
            Directory.Delete(_tmpDir, recursive: true);
        }
    }

    [Fact]
    public async Task Roundtrip_dpapi_then_jsonsettings_then_reload_runtime()
    {
        var secrets = new DpapiSecretStore(_secretsDir, NullLogger<DpapiSecretStore>.Instance);
        var settings = new JsonSettingsService(_settingsPath, NullLogger<JsonSettingsService>.Instance);
        var key = new WhisperApiKey(string.Empty);
        var options = new WhisperApiOptions
        {
            BaseUrl = "https://stale.example/v1",
            Model = "stale-model",
            Language = "es",
            TimeoutSeconds = 30,
        };
        var writer = new ProviderConfigWriter(secrets, settings, key, options, NullLogger<ProviderConfigWriter>.Instance);

        var newConfig = new ProviderConfig(
            PresetId: "groq",
            BaseUrl: "https://api.groq.com/openai/v1",
            Model: "whisper-large-v3",
            ApiKey: "gsk_integration_roundtrip_key_xxx");

        await writer.SaveAsync(newConfig);

        // 1) DPAPI realmente persistió la key.
        Assert.Equal(newConfig.ApiKey, secrets.Read(ProviderConfigWriter.ApiKeySecretName));

        // 2) JsonSettings persistió el bloque provider en disco.
        Assert.True(File.Exists(_settingsPath));
        var reloaded = settings.Load();
        Assert.Equal(newConfig.PresetId, reloaded.Provider.PresetId);
        Assert.Equal(newConfig.BaseUrl, reloaded.Provider.BaseUrl);
        Assert.Equal(newConfig.Model, reloaded.Provider.Model);

        // 3) Runtime (los singletons del Whisper) reflejan los valores nuevos sin rebuild
        // del DI container. Language/TimeoutSeconds quedan como estaban porque no son
        // parte del scope de provider config.
        Assert.Equal(newConfig.ApiKey, key.Value);
        Assert.True(key.IsConfigured);
        Assert.Equal(newConfig.BaseUrl, options.BaseUrl);
        Assert.Equal(newConfig.Model, options.Model);
        Assert.Equal("es", options.Language);
        Assert.Equal(30, options.TimeoutSeconds);
    }

    [Fact]
    public async Task Bootstrap_simulation_loads_persisted_key_after_writer_save()
    {
        // Simula: el usuario completa el onboarding (writer.SaveAsync), cierra la app, y
        // arranca de nuevo. El bootstrap nuevo lee de DPAPI y arma un WhisperApiKey con
        // la key que persistió en la sesión anterior.
        var secrets = new DpapiSecretStore(_secretsDir, NullLogger<DpapiSecretStore>.Instance);
        var settings = new JsonSettingsService(_settingsPath, NullLogger<JsonSettingsService>.Instance);
        var initialKey = new WhisperApiKey(string.Empty);
        var initialOptions = new WhisperApiOptions { BaseUrl = "stale", Model = "stale" };
        var writer = new ProviderConfigWriter(secrets, settings, initialKey, initialOptions, NullLogger<ProviderConfigWriter>.Instance);

        await writer.SaveAsync(new ProviderConfig(
            PresetId: "openai",
            BaseUrl: "https://api.openai.com/v1",
            Model: "whisper-1",
            ApiKey: "sk-restart-simulation-1234567890"));

        // "Reinicio": instancias nuevas leídas desde disco — espejo de Program.cs.
        var freshSecrets = new DpapiSecretStore(_secretsDir, NullLogger<DpapiSecretStore>.Instance);
        var freshSettings = new JsonSettingsService(_settingsPath, NullLogger<JsonSettingsService>.Instance);

        var bootstrapKey = new WhisperApiKey(freshSecrets.Read(ProviderConfigWriter.ApiKeySecretName) ?? string.Empty);
        var loaded = freshSettings.Load();
        var bootstrapOptions = new WhisperApiOptions
        {
            BaseUrl = loaded.Provider.BaseUrl,
            Model = loaded.Provider.Model,
        };

        Assert.True(bootstrapKey.IsConfigured);
        Assert.Equal("sk-restart-simulation-1234567890", bootstrapKey.Value);
        Assert.Equal("https://api.openai.com/v1", bootstrapOptions.BaseUrl);
        Assert.Equal("whisper-1", bootstrapOptions.Model);
    }
}
