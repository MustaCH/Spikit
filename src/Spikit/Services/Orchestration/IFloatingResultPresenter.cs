using Spikit.Services.Insertion;

namespace Spikit.Services.Orchestration;

// Presenta el resultado de la transcripción en una ventana flotante cuando el paste
// no se pudo aplicar (TargetGone, Failed, o sin campo editable). Implementación real
// en sub-task #7 (FloatingResultWindow). Hasta entonces se inyecta un stub que loguea.
public interface IFloatingResultPresenter
{
    void Show(string text, InsertionResult reason);
    void Hide();
}
