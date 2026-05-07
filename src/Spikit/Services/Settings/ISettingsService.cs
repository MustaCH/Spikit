using Spikit.Models;

namespace Spikit.Services.Settings;

// Persistencia del bloque AppSettings (settings.json bajo %AppData%\Spikit).
//
// Contrato:
//   - Load() nunca tira: si no existe / está corrupto, devuelve defaults silenciosamente.
//     La capa que llama no tiene que saber si es la primera ejecución o no.
//   - Save() puede tirar IOException si el filesystem rechaza (disco lleno, permisos).
//     Quien lo llame es responsable de capturar y mostrar el error al usuario.
public interface ISettingsService
{
    AppSettings Load();
    void Save(AppSettings settings);
}
