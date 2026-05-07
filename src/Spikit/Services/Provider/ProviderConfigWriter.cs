using Microsoft.Extensions.Logging;
using Spikit.Services.Secrets;
using Spikit.Services.Settings;
using Spikit.Services.Transcription;

namespace Spikit.Services.Provider;

// Implementación del contrato transaccional descrito en IProviderConfigWriter.
// El orden de operaciones está fijado por el ticket EP-3.4: DPAPI primero (es la
// primitiva más probable de fallar — perfil, política), y solo si OK, recién tocamos
// JsonSettings + el runtime de Whisper.
public sealed class ProviderConfigWriter : IProviderConfigWriter
{
    // Clave canónica de la API key dentro de DPAPI. No es un dato visible al usuario,
    // pero la inmortalizamos como const para que tests + rollback la compartan.
    public const string ApiKeySecretName = "provider.apiKey";

    private readonly ISecretStore _secrets;
    private readonly ISettingsService _settings;
    private readonly WhisperApiKey _runtimeKey;
    private readonly WhisperApiOptions _runtimeOptions;
    private readonly ILogger<ProviderConfigWriter> _logger;

    public ProviderConfigWriter(
        ISecretStore secrets,
        ISettingsService settings,
        WhisperApiKey runtimeKey,
        WhisperApiOptions runtimeOptions,
        ILogger<ProviderConfigWriter> logger)
    {
        _secrets = secrets;
        _settings = settings;
        _runtimeKey = runtimeKey;
        _runtimeOptions = runtimeOptions;
        _logger = logger;
    }

    public Task SaveAsync(ProviderConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ct.ThrowIfCancellationRequested();

        // Paso 1 — DPAPI. Si falla, abortamos sin tocar nada más.
        try
        {
            _secrets.Write(ApiKeySecretName, config.ApiKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DPAPI rechazó el guardado de la API key");
            throw new ProviderConfigSaveException(
                "No se pudo guardar la API key de forma segura. Probá de nuevo o revisá los permisos del perfil de Windows.",
                ex);
        }

        // Paso 2 — JsonSettings. Si falla post-DPAPI, rollback del secret para que la app
        // no quede en estado inconsistente (key cifrada sin BaseUrl/Model asociados).
        try
        {
            var current = _settings.Load();
            current.Provider = new Models.ProviderSettings
            {
                PresetId = config.PresetId,
                BaseUrl = config.BaseUrl,
                Model = config.Model,
            };
            _settings.Save(current);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JsonSettings rechazó el guardado del bloque provider — rollback del DPAPI write");
            try
            {
                _secrets.Delete(ApiKeySecretName);
            }
            catch (Exception rollbackEx)
            {
                // Doble fallo: DPAPI ya no responde tampoco. Logueamos y seguimos
                // tirando la excepción original — el rollback fallido no debería
                // tapar el error real que vio el usuario.
                _logger.LogError(rollbackEx, "Fallo al rollback del secret tras error de JsonSettings");
            }

            throw new ProviderConfigSaveException(
                "No se pudo guardar la configuración del provider en el archivo de settings. Probá de nuevo.",
                ex);
        }

        // Paso 3 — runtime reload. Mutación in-place de los singletons compartidos:
        //   - WhisperApiKey expone Update(string) para no romper el ctor existente.
        //   - WhisperApiOptions ya tiene setters; mutamos los campos que cambiaron.
        // La próxima request del WhisperApiTranscriptionService usa estos valores
        // (transient construido por AddHttpClient + IOptions wrappeando la misma instancia).
        _runtimeKey.Update(config.ApiKey);
        _runtimeOptions.BaseUrl = config.BaseUrl;
        _runtimeOptions.Model = config.Model;

        _logger.LogInformation(
            "Provider config persistida y aplicada en runtime (preset={PresetId}, baseUrl={BaseUrl}, model={Model})",
            config.PresetId, config.BaseUrl, config.Model);

        return Task.CompletedTask;
    }
}
