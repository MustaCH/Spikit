namespace Spikit.ViewModels.Auth;

// Los 7 estados visuales del LoginWindow definidos en design-system.md §10.12 y
// flows.md FLOW 0. Cada estado mapea a un DataTemplate en LoginWindow.xaml.
//
// Transiciones canónicas (lo que NO está acá no debería pasar en runtime — si pasa
// es un bug del orquestador):
//
//   Idle              → WaitingForMagicLink (deep-link auth-pending llega)
//                     → ValidatingToken      (deep-link auth-callback llega directo,
//                                             ej. app cerrada cuando el user clickeó
//                                             el magic link, Windows abre la app con
//                                             el callback como argv[1])
//
//   WaitingForMagicLink → ValidatingToken    (deep-link auth-callback llega)
//                       → Idle               (link "Volver a empezar" / timeout 15 min)
//
//   ValidatingToken     → LoadingEntitlement (validate OK)
//                       → ErrorValidating    (401/403)
//                       → ErrorNetwork       (timeout/red)
//
//   LoadingEntitlement  → Success            (fetch OK — el AuthService dispara
//                                             StateChanged con LoggedIn)
//                       → ErrorNetwork       (3 retries fallaron por red)
//
//   Success             → (window se cierra ~200ms después, RequestClose event)
//
//   ErrorValidating     → Idle               (CTA "Volver a intentar" → reabre browser)
//
//   ErrorNetwork        → ValidatingToken    (CTA "Reintentar" reintenta el último paso)
//                       → LoadingEntitlement (ídem si el fail vino del fetch entitlement)
public enum LoginState
{
    Idle,
    WaitingForMagicLink,
    ValidatingToken,
    LoadingEntitlement,
    Success,
    ErrorValidating,
    ErrorNetwork,
}

// Variante de copy del estado Idle. Cambia sólo la primera línea del body — el resto
// del layout (logo, h1, CTA, caption) se mantiene idéntico. flows.md FLOW 0 § Estado 0.1
// "Variante sesión expirada".
public enum LoginIdleVariant
{
    // "Iniciá sesión con tu email para empezar a dictar..."
    FirstLaunch,

    // "Tu sesión expiró. Volvé a iniciar sesión para seguir usando Spikit."
    SessionExpired,
}
