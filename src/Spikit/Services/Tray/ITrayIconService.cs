namespace Spikit.Services.Tray;

// Punto de entrada permanente de Spikit cuando no hay ventana visible (EP-4.2).
//
// Implementación canónica en WpfTrayIconService usando H.NotifyIcon.Wpf. Singleton,
// instanciado al EnterMainAppMode (no en onboarding ni --diagnostics-poc). El cleanup
// final corre en App.OnExit vía Dispose; el cleanup transitorio (logout EP-11.7 que
// vuelve a LoginWindow sin cerrar la app) corre vía Shutdown.
public interface ITrayIconService : IDisposable
{
    // Crea y muestra el tray icon. Idempotente — si ya está visible es no-op. Tolera
    // ser llamado tras Shutdown (re-arma desde cero).
    void Initialize();

    // EP-11.7: oculta el tray + libera el icon nativo + desuscribe events, dejando el
    // servicio en estado "no inicializado". A diferencia de Dispose, no lo marca como
    // terminal — un Initialize posterior re-arma el tray desde cero. Idempotente.
    void Shutdown();
}
