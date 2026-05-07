namespace Spikit.Services.Tray;

// Punto de entrada permanente de Spikit cuando no hay ventana visible (EP-4.2).
//
// Implementación canónica en WpfTrayIconService usando H.NotifyIcon.Wpf. Singleton,
// instanciado al EnterMainAppMode (no en onboarding ni --diagnostics-poc) y disposeado
// en App.OnExit.
public interface ITrayIconService : IDisposable
{
    // Crea y muestra el tray icon. Idempotente.
    void Initialize();
}
