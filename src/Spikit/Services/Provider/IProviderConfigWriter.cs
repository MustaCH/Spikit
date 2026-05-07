using Spikit.Models;

namespace Spikit.Services.Provider;

// Persiste la configuración del provider de forma transaccional + refresca los servicios
// runtime (WhisperApiKey + WhisperApiOptions) para que la próxima request al transcriber
// use los nuevos valores sin reiniciar la app.
//
// Contrato transaccional:
//   1. Si DPAPI falla, NO se persiste el JSON de settings y se tira ProviderConfigSaveException.
//   2. Si DPAPI OK pero JsonSettings falla, se hace rollback del DPAPI write (delete) y se
//      tira ProviderConfigSaveException. El estado runtime no se toca.
//   3. Si ambas escrituras OK, se mutan in-place WhisperApiKey + WhisperApiOptions
//      (registrados como singletons compartidos en DI).
public interface IProviderConfigWriter
{
    Task SaveAsync(ProviderConfig config, CancellationToken ct = default);
}

// DTO inmutable con todo lo que el VM provee al guardar. ApiKey viaja en plano por este
// boundary porque el cifrado lo hace el writer; nada lo persiste antes.
public sealed record ProviderConfig(string PresetId, string BaseUrl, string Model, string ApiKey);
