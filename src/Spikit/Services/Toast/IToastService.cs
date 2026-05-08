using Spikit.Models;

namespace Spikit.Services.Toast;

// Servicio único de toasts bottom-right (EP-5.3 / FLOW 5). Cola FIFO con máximo 3 visibles
// simultáneos. Llamadas con la misma DedupeKey reemplazan al toast existente reseteando el
// timer (no apilan). Auto-dismiss configurable; hover pausa el timer (manejado en VM).
//
// Stateless desde el punto de vista del caller: no hay handle de "toast id" ni manera de
// cerrarlo programáticamente desde fuera. El caller fire-and-forget y el servicio + el toast
// se encargan del lifecycle.
public interface IToastService
{
    // Default auto-dismiss usado cuando el caller no pasa autoDismiss (FLOW 5 — 5s).
    static readonly TimeSpan DefaultAutoDismiss = TimeSpan.FromSeconds(5);

    // Muestra un toast. Si el caller pasa null en autoDismiss usa DefaultAutoDismiss.
    // dedupeKey null = nunca deduplica (siempre apila como toast nuevo).
    void Show(
        ToastSeverity severity,
        string title,
        string? message = null,
        ToastAction? action = null,
        TimeSpan? autoDismiss = null,
        string? dedupeKey = null);
}
