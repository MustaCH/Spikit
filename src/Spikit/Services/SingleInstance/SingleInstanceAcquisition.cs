namespace Spikit.Services.SingleInstance;

// Resultado de TryAcquire(). Tres estados que cubren los AC del ticket EP-8.1 + el caso
// "instancia zombi" descripto en las notas técnicas (mutex tomado pero pipe no responde).
public enum SingleInstanceAcquisition
{
    // Soy la primera instancia. El mutex fue adquirido y el listener IPC quedó activo.
    // El caller debe continuar el bootstrap normal de la app.
    Primary,

    // Ya había otra instancia viva. Le mandé OPEN_SETTINGS por el pipe y debo cerrarme
    // con exit code 0 — no es error, es comportamiento esperado (RN-9, CB-11).
    SecondaryNotified,

    // El mutex está tomado pero el pipe de la otra instancia no respondió en el timeout
    // configurado (proceso zombie sin event loop activo). El caller arranca igual como
    // primary degradado: NO tiene mutex, NO tiene listener — la única protección es que
    // este caso es estadísticamente raro. Se loguea WARN para investigar si se repite.
    SecondaryForwardFailed,
}
