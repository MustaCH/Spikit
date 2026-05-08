namespace Spikit.Models;

// Una sesión de dictado guardada en el historial local. Persiste como objeto JSON dentro
// de %AppData%\Spikit\history.json cuando privacy.historyEnabled = true. Spec en EP-4.8 +
// US-5.5.
//
// Decisiones:
//   - Id GUID generado en Append. Necesario para DeleteOne y para identificar entries
//     unívocamente desde la UI (timestamp no es único: dos dictados en el mismo ms colisionan).
//   - Timestamp en UTC (DateTimeOffset preserva offset). La UI lo formatea con la TZ local
//     del usuario al renderizarlo.
//   - DurationMs en long para evitar el quirk de TimeSpan.ToString() en JSON.
//   - TargetProcessName es el nombre del .exe (ej. "cursor.exe", "Code.exe"). Si la
//     captura del orchestrator no pudo identificarlo, queda string.Empty — la UI lo trata
//     como "(desconocido)" al render.
public sealed class HistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Timestamp { get; set; }
    public long DurationMs { get; set; }
    public string Text { get; set; } = string.Empty;
    public string TargetProcessName { get; set; } = string.Empty;
}
