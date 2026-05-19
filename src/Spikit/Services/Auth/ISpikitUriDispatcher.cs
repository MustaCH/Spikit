namespace Spikit.Services.Auth;

// Punto de entrada del runtime para procesar URIs `spikit://...` que llegan a la app,
// ya sea por argv en boot directo o por forward de single-instance (otra instancia que
// recibió el deep-link del OS y nos lo mandó por pipe). Rutea según `SpikitUriKind`:
//
//   - AuthCallback: llama IAuthService.HandleAuthCallbackAsync con los params parseados,
//                   y muestra toast con el resultado (success → log + sesión activa;
//                   failure → toast warning con la razón).
//   - BillingReturn: si status=success, fuerza RefreshEntitlementAsync y toast acorde;
//                    si status=cancel, toast neutro. La retry-with-backoff completa
//                    (ADR-0007 § 4.2) se cablea en EP-10.12.
//   - Unknown: loguea warning y no hace nada (no rompemos UX por un URI desconocido).
public interface ISpikitUriDispatcher
{
    Task DispatchAsync(string rawUri, CancellationToken ct);
}
