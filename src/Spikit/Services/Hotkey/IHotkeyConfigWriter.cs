using Spikit.Models;

namespace Spikit.Services.Hotkey;

// Persiste la combinación + modo de hotkey de forma transaccional + reconfigura el
// HotkeyService runtime para que los próximos presses usen la nueva combinación sin
// reiniciar la app.
//
// Contrato transaccional (EP-3.6 acceptance criteria):
//   1. Unregister de la combinación previa (si había alguna).
//   2. Register de la nueva → si tira HotkeyRegistrationException (CB-7), re-Register
//      la previa para no dejar al usuario sin hotkey activo, y propaga la excepción.
//   3. Persistir en JsonSettings → si falla, rollback del Register (Unregister nueva,
//      Register previa) antes de propagar.
//   4. Reload runtime: actualizar el modo del DictationOrchestrator.
public interface IHotkeyConfigWriter
{
    Task SaveAsync(HotkeyDefinition definition, HotkeyMode mode, CancellationToken ct = default);
}
