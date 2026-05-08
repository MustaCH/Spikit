namespace Spikit.Services.Orchestration;

// Resuelve el nombre del proceso dueño de un HWND (target del paste). Lo usa el
// DictationOrchestrator cuando registra una entry en el historial (EP-4.10):
// quiere saber si el dictado fue para "cursor.exe" o "Code.exe".
//
// Inyectable para tests (los tests del orchestrator no necesitan tocar Win32).
public interface ITargetProcessResolver
{
    // Devuelve el nombre del proceso con extensión (ej. "cursor.exe"). Devuelve string.Empty
    // si el HWND es 0 / inválido / el proceso ya cerró / Win32 falló — el caller decide
    // qué mostrar al usuario (HistoryEntryViewModel cae a "(desconocido)").
    string Resolve(IntPtr hwnd);
}
