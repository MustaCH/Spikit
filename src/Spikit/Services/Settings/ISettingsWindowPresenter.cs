using Spikit.ViewModels.Settings;

namespace Spikit.Services.Settings;

// Punto de entrada único para abrir el SettingsWindow desde cualquier lado de la app
// (TrayIcon en EP-4.2, MainWindow debug, FloatingResultWindow CTAs, etc.).
//
// Garantiza que solo haya una window abierta a la vez: si ya está visible, la trae al
// frente (cumple el AC del ticket EP-4.1: "Si la window ya está abierta, traerla al
// frente en lugar de abrir otra"). Implementación canónica en WpfSettingsWindowPresenter.
public interface ISettingsWindowPresenter
{
    // Abre la window si no está abierta, o la trae al frente si ya estaba.
    // Si se pasa `section`, navega a esa sección al abrir/traer al frente (deep-link
    // usado por FloatingResultWindow V3 → Provider, EP-6.5).
    // Thread-safe: marshalea al UI thread internamente.
    void Open(SettingsSection? section = null);
}
