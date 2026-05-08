using Spikit.Models;

namespace Spikit.Services.Orchestration;

// Presenta el FloatingResultWindow con la variante apropiada (V1-V6 de design-system §10.4).
// Implementación canónica en WpfFloatingResultPresenter; stub LoggingFloatingResultPresenter
// para tests / arranque temprano.
public interface IFloatingResultPresenter
{
    // Abre o reusa la window mostrando la variante correspondiente al motivo.
    //
    // - text: solo se muestra el textbox cuando hay texto recuperable (V1, V6). En V3/V4/V5
    //   pasar null y la anatomía de la window oculta la sección de texto.
    // - targetHwnd: se retiene para "Reintentar paste" (solo V1). En el resto pasar IntPtr.Zero.
    void Show(ResultErrorReason reason, string? text = null, IntPtr targetHwnd = default);

    void Hide();
}
