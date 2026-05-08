namespace Spikit.Services.SingleInstance;

// Garante de single-instance (RN-9 / CB-11). El caller invoca TryAcquire() **antes** de
// inicializar el host de DI: si retorna SecondaryNotified, el proceso debe terminar con
// exit code 0 sin bootstrappear nada. Si retorna Primary, el caller mantiene el guard
// vivo durante toda la sesión y se suscribe a OpenRequested para reaccionar cuando un
// usuario lance otra instancia (por ej. doble click en el .exe del taskbar).
public interface ISingleInstanceGuard : IDisposable
{
    // Se dispara desde un thread del threadpool cuando la otra instancia notificó
    // OPEN_SETTINGS por el pipe. El subscriber es responsable de marshalear al UI thread.
    // Solo se invoca en instancias en estado Primary.
    event EventHandler? OpenRequested;

    // Resultado del intento de tomar el "slot" de la primera instancia. Idempotente:
    // invocaciones subsiguientes retornan el mismo valor.
    SingleInstanceAcquisition TryAcquire();
}
