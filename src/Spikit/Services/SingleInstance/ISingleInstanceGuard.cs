namespace Spikit.Services.SingleInstance;

// Garante de single-instance (RN-9 / CB-11). El caller invoca TryAcquire() **antes** de
// inicializar el host de DI: si retorna SecondaryNotified, el proceso debe terminar con
// exit code 0 sin bootstrappear nada. Si retorna Primary, el caller mantiene el guard
// vivo durante toda la sesión y se suscribe a OpenRequested / UriForwardRequested para
// reaccionar cuando un usuario lance otra instancia (por ej. doble click en el .exe del
// taskbar, o Windows abre la app con un deep-link `spikit://...`).
public interface ISingleInstanceGuard : IDisposable
{
    // Se dispara desde un thread del threadpool cuando la otra instancia notificó
    // OPEN_SETTINGS por el pipe (caso "lanzá la app sin args, ya estaba abierta").
    // El subscriber es responsable de marshalear al UI thread.
    event EventHandler? OpenRequested;

    // Se dispara desde un thread del threadpool cuando la otra instancia notificó un
    // deep-link `spikit://...` por el pipe (caso "Windows abrió Spikit con el callback
    // del magic link / del retorno de Stripe, pero ya había una instancia corriendo").
    // El payload es el URI raw como llegó por argv — el subscriber debe parsearlo y
    // dispatchear. Solo se invoca en instancias Primary.
    event EventHandler<string>? UriForwardRequested;

    // Resultado del intento de tomar el "slot" de la primera instancia. Idempotente:
    // invocaciones subsiguientes con los mismos args retornan el mismo valor.
    //
    // `forwardedUri`: si se provee y la acquisition cae en Secondary, el guard manda
    // el URI a la instancia primaria en lugar del OPEN_SETTINGS por default. Útil
    // cuando la segunda instancia fue lanzada por Windows con un deep-link.
    SingleInstanceAcquisition TryAcquire(string? forwardedUri = null);
}
