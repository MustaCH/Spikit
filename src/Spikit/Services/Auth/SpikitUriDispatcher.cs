using Microsoft.Extensions.Logging;
using Spikit.Models;
using Spikit.Services.Toast;

namespace Spikit.Services.Auth;

// Impl productiva del ISpikitUriDispatcher. Combina el parser puro (SpikitUriParser)
// con IAuthService + IToastService para cerrar el flow.
public sealed class SpikitUriDispatcher : ISpikitUriDispatcher
{
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(5);

    private readonly IAuthService _auth;
    private readonly IToastService _toast;
    private readonly ILogger<SpikitUriDispatcher> _logger;

    public SpikitUriDispatcher(IAuthService auth, IToastService toast, ILogger<SpikitUriDispatcher> logger)
    {
        _auth = auth;
        _toast = toast;
        _logger = logger;
    }

    public async Task DispatchAsync(string rawUri, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawUri);

        var parsed = SpikitUriParser.TryParse(rawUri);
        if (parsed is null)
        {
            _logger.LogWarning("Dispatch: URI no parseable, ignorado ({Raw})", rawUri);
            return;
        }

        _logger.LogInformation("Dispatch: kind={Kind}", parsed.Kind);

        switch (parsed.Kind)
        {
            case SpikitUriKind.AuthCallback:
                await HandleAuthCallbackAsync(parsed.Params, ct).ConfigureAwait(false);
                break;

            case SpikitUriKind.AuthPending:
                HandleAuthPending(parsed.Params);
                break;

            case SpikitUriKind.BillingReturn:
                await HandleBillingReturnAsync(parsed.Params, ct).ConfigureAwait(false);
                break;

            case SpikitUriKind.Unknown:
            default:
                _logger.LogWarning("Dispatch: URI kind desconocido, ignorado ({Raw})", rawUri);
                break;
        }
    }

    // EP-11.4 — `spikit://auth-pending?email=...` (cierre Q-9 de ADR-0008). El payload
    // es el email URL-decoded que la página spikit.dev/auth puso en el redirect tras
    // un signInWithOtp exitoso. No tocamos tokens ni state — solo propagamos al canal
    // del evento `AuthPendingReceived` del IAuthService para que el LoginViewModel
    // mute al estado WaitingForMagicLink mostrando el email exacto. Si no hay
    // LoginWindow visible (caso edge: la app está en MainApp logueada y llega un
    // auth-pending), el evento no tiene listener y queda como no-op silencioso.
    //
    // Sin toast: el feedback visual lo da el LoginWindow (estado 0.2). Mostrar un
    // toast adicional sería ruido.
    private void HandleAuthPending(IReadOnlyDictionary<string, string> queryParams)
    {
        if (!queryParams.TryGetValue("email", out var email)
            || string.IsNullOrWhiteSpace(email))
        {
            _logger.LogWarning("Dispatch: auth-pending sin email, ignorado");
            return;
        }

        _auth.RaiseAuthPendingReceived(email);
    }

    private async Task HandleAuthCallbackAsync(IReadOnlyDictionary<string, string> queryParams, CancellationToken ct)
    {
        AuthCallbackResult result;
        try
        {
            result = await _auth.HandleAuthCallbackAsync(queryParams, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AuthCallback dispatch falló con excepción no controlada");
            _toast.Show(ToastSeverity.Error, "No pudimos completar el login. Probá de nuevo.",
                autoDismiss: ToastDuration, dedupeKey: "auth-callback");
            return;
        }

        if (result.Success)
        {
            var email = result.Profile?.Email ?? "tu cuenta";
            _toast.Show(ToastSeverity.Info, $"Sesión iniciada como {email}",
                autoDismiss: ToastDuration, dedupeKey: "auth-callback");
            return;
        }

        var reason = string.IsNullOrEmpty(result.ErrorReason)
            ? "El callback no pudo procesarse."
            : result.ErrorReason;
        _toast.Show(ToastSeverity.Warning, $"Login falló: {reason}",
            autoDismiss: ToastDuration, dedupeKey: "auth-callback");
    }

    private async Task HandleBillingReturnAsync(IReadOnlyDictionary<string, string> queryParams, CancellationToken ct)
    {
        // ?status=cancel → el usuario cerró Checkout sin pagar.
        if (queryParams.TryGetValue("status", out var status)
            && string.Equals(status, "cancel", StringComparison.OrdinalIgnoreCase))
        {
            _toast.Show(ToastSeverity.Info, "Pago cancelado. Podés volver cuando quieras.",
                autoDismiss: ToastDuration, dedupeKey: "billing-return");
            return;
        }

        // Default (sin status o status=success): refrescamos el entitlement con backoff
        // exponencial hasta que el server muestre tier='pro' o se agoten los reintentos
        // (ADR-0007 § 4.2 — race condition entre el deep-link de retorno y el webhook
        // que actualiza la fila).
        var entitlement = await _auth
            .RefreshEntitlementWithBackoffAsync(e => e.Tier == Tier.Pro, ct)
            .ConfigureAwait(false);

        if (entitlement is null)
        {
            // Nunca pudo refrescar — sin sesión, o el server no responde después de 5
            // intentos. Toast informativo; el webhook eventualmente cierra el gap.
            _toast.Show(ToastSeverity.Info,
                "Tu suscripción está procesándose. Te avisamos por email cuando esté lista.",
                autoDismiss: ToastDuration, dedupeKey: "billing-return");
            return;
        }

        if (entitlement.Tier == Tier.Pro)
        {
            _toast.Show(ToastSeverity.Info, "Pro activado. ¡Gracias!",
                autoDismiss: ToastDuration, dedupeKey: "billing-return");
            return;
        }

        // Refrescamos exitoso pero el tier todavía no es Pro tras los 5 intentos —
        // probablemente el webhook tardó más de lo esperado. Mensaje neutral.
        _toast.Show(ToastSeverity.Info,
            "Tu suscripción está procesándose. Te avisamos por email cuando esté lista.",
            autoDismiss: ToastDuration, dedupeKey: "billing-return");
    }
}
