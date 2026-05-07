using Spikit.Models;

namespace Spikit.Services.Settings;

// Persistencia del bloque AppSettings (settings.json bajo %AppData%\Spikit).
//
// Contrato:
//   - Load() nunca tira: si no existe / está corrupto, devuelve defaults silenciosamente.
//     La capa que llama no tiene que saber si es la primera ejecución o no.
//   - Save() puede tirar IOException si el filesystem rechaza (disco lleno, permisos).
//     Quien lo llame es responsable de capturar y mostrar el error al usuario.
//   - SettingsChanged se dispara después de un Save() exitoso. Suscriptores típicos:
//     TrayIconService (refresh tooltip + menu header tras cambio de provider/hotkey en
//     Settings — EP-4.2). El evento no incluye payload — los listeners hacen Load() de
//     nuevo si necesitan el snapshot fresco. Esto es coherente con el flujo Load → mutar
//     → Save documentado en AppSettings.
public interface ISettingsService
{
    AppSettings Load();
    void Save(AppSettings settings);
    event EventHandler? SettingsChanged;
}
