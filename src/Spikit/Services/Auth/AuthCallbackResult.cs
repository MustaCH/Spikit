namespace Spikit.Services.Auth;

// Resultado del HandleAuthCallbackAsync. Si Success=true, Profile y Entitlement están
// poblados (Entitlement puede ser null si el login fue ok pero el fetch falló por red;
// la sesión queda como logged-in igual y el UI cargará el cache stale). Si Success=false,
// ErrorReason explica por qué — útil para mostrar al usuario.
public sealed record AuthCallbackResult(
    bool Success,
    UserProfile? Profile,
    Entitlement? Entitlement,
    string? ErrorReason);
