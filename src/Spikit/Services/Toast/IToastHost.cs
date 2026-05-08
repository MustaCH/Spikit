using Spikit.Models;

namespace Spikit.Services.Toast;

// Puerto interno que la capa de UI implementa (WpfToastHost). El ToastService llama estos
// métodos al UI thread; la impl es responsable de crear/animar/destruir windows.
//
// Show: crear una ToastWindow para esta id.
// Refresh: el toast con esta id ya está visible, actualizar contenido (dedupe hit).
// Dismiss: cerrar la ToastWindow asociada a esta id (auto-dismiss expirado, max-3 evict, etc.).
// Dismissed: disparado por el host cuando una window se cerró (por dismiss programático o
//            click en la acción). El service lo usa para limpiar su lista interna.
internal interface IToastHost
{
    void Show(Guid id, ToastNotification notification);
    void Refresh(Guid id, ToastNotification notification);
    void Dismiss(Guid id);
    event EventHandler<Guid>? Dismissed;
}
