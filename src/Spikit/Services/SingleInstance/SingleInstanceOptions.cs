namespace Spikit.Services.SingleInstance;

// Parámetros del SingleInstanceGuard. Se exponen como record para que los tests
// puedan crear instancias con nombres aleatorios y timeouts cortos sin tocar la
// configuración real de producción.
public sealed record SingleInstanceOptions
{
    // Nombre del mutex global. `Global\` cubre todas las sesiones de Windows
    // (incluye Remote Desktop y switch de usuario). Decisión cerrada con Nacho 2026-05-08.
    public required string MutexName { get; init; }

    // Nombre del named pipe server. No requiere prefijo `Global\` — los named pipes
    // son visibles a través de sesiones por default cuando la ACL lo permite.
    public required string PipeName { get; init; }

    // Cuánto espera la segunda instancia para conectar al pipe de la primera antes de
    // declarar a la primera "zombie" y arrancar igual. 2000 ms cubre con margen el
    // arranque del listener (típico <50 ms) y deja espacio para una primera instancia
    // bajo carga sin penalizar al usuario que doble-click en taskbar.
    public int ConnectTimeoutMilliseconds { get; init; } = 2000;

    // Configuración real de producción. Mantener acá centralizado para que cualquier
    // change de naming sea un solo lugar.
    public static SingleInstanceOptions Default { get; } = new()
    {
        MutexName = @"Global\Spikit-SingleInstance",
        PipeName = "Spikit.IPC.OpenRequest",
    };
}
