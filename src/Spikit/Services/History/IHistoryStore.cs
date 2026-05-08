using Spikit.Models;

namespace Spikit.Services.History;

// Almacén persistente del historial de dictados. Cubre EP-4.8 (visualización) y EP-4.10
// (cableado del orchestrator, que va a llamar Append cuando privacy.historyEnabled = true
// al cierre de cada sesión exitosa).
//
// Contrato:
//   - Las queries (LoadPage, Search) devuelven entries ordenadas por Timestamp DESC
//     (más reciente primero) — coherente con cómo el usuario espera ver su actividad.
//   - LoadPage usa skip/take sobre el corpus completo. Para historial chico (≤miles)
//     cargamos todo en memoria al primer hit; revisitar si V2 necesita streaming desde disco.
//   - Search es Contains case-insensitive sobre el campo Text (no busca en process name
//     ni timestamp — el usuario suele recordar QUÉ dictó, no CUÁNDO ni DÓNDE).
//   - Append/Delete son inmediatos a disco (no batch). Escritura atómica (write-then-rename).
//   - DeleteAll borra el archivo completo. La próxima Append crea uno nuevo.
//
// Si el archivo no existe o está corrupto, las queries devuelven empty — coherente con la
// política de JsonSettingsService.Load() (recover gracefully + log warning).
public interface IHistoryStore
{
    // Agrega una entry nueva al historial. Genera un Guid si Id es Guid.Empty.
    // Tira IOException si el filesystem rechaza la escritura.
    void Append(HistoryEntry entry);

    // Página de entries ordenadas DESC por Timestamp. take<=0 devuelve [].
    IReadOnlyList<HistoryEntry> LoadPage(int skip, int take);

    // Total de entries persistidas (después de filtros si aplica). Útil para que la UI
    // sepa si tiene más páginas para cargar.
    int Count();

    // Search inline (Contains case-insensitive sobre Text). Devuelve TODAS las matches
    // (no paginado en este contrato — las búsquedas reales raramente devuelven >100).
    IReadOnlyList<HistoryEntry> Search(string query);

    // Borra una entry por id. Idempotente: no tira si la entry no existe.
    void DeleteOne(Guid id);

    // Borra el archivo completo + corpus en memoria. Idempotente.
    void DeleteAll();
}
