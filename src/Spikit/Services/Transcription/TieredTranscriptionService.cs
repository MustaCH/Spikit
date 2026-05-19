using Microsoft.Extensions.Logging;
using Spikit.Services.Auth;

namespace Spikit.Services.Transcription;

// Dispatcher de transcripción según el tier del user. Es la impl pública de
// ITranscriptionService que el orchestrator consume. Internamente delega a:
//   - WhisperApiTranscriptionService cuando el user es BYOK (logueado o legacy
//     pre-EP-10.4 sin sesión) — la BYOK key vive en DPAPI del cliente.
//   - ProxyTranscriptionService cuando el user es Trial / Pro — el WAV va al Edge
//     Function /transcribe y el server proxea a OpenAI con la key gestionada.
//
// El estado Expired no debería llegar acá: el gate de EP-10.12 en DictationOrchestrator
// bloquea el hotkey antes de iniciar grabación. Si llega igual (cache stale entre
// gate y este call), tiramos SubscriptionRequiredException para que el orchestrator
// muestre el toast de "Upgrade to Pro" — mismo manejo que un 402 del server.
public sealed class TieredTranscriptionService : ITranscriptionService
{
    private readonly WhisperApiTranscriptionService _direct;
    private readonly ProxyTranscriptionService _proxy;
    private readonly IAuthService _auth;
    private readonly ILogger<TieredTranscriptionService> _logger;

    public TieredTranscriptionService(
        WhisperApiTranscriptionService direct,
        ProxyTranscriptionService proxy,
        IAuthService auth,
        ILogger<TieredTranscriptionService> logger)
    {
        _direct = direct;
        _proxy = proxy;
        _auth = auth;
        _logger = logger;
    }

    public Task<string> TranscribeAsync(byte[] wavData, CancellationToken ct)
    {
        var path = SelectPath();
        _logger.LogDebug("Tiered transcription: path={Path}", path);

        return path switch
        {
            TranscriptionPath.Direct => _direct.TranscribeAsync(wavData, ct),
            TranscriptionPath.Proxy => _proxy.TranscribeAsync(wavData, ct),
            TranscriptionPath.Blocked => throw new SubscriptionRequiredException(
                "Tu suscripción a Spikit expiró. Suscribite a Pro para seguir dictando."),
            _ => throw new InvalidOperationException($"Path no manejado: {path}"),
        };
    }

    // Política de routing. Public-ish (internal) para que un test pueda chequear el
    // mapeo sin pegar a la red.
    internal TranscriptionPath SelectPath()
    {
        // No hay sesión activa: legacy BYOK (la app pre-EP-10.4 funciona sin login).
        if (_auth.State != AuthSessionState.LoggedIn) return TranscriptionPath.Direct;

        var tier = _auth.CurrentEntitlement?.Tier;
        return tier switch
        {
            Tier.Byok => TranscriptionPath.Direct,
            Tier.Trial => TranscriptionPath.Proxy,
            Tier.Pro => TranscriptionPath.Proxy,
            Tier.Expired => TranscriptionPath.Blocked,
            // Cache sin poblar (entitlement null tras login pero antes del fetch
            // exitoso): conservador — degradamos a Direct. Si BYOK no es lo que el
            // user es realmente, el server-side igual rechazará y el flow recovers.
            null => TranscriptionPath.Direct,
            _ => TranscriptionPath.Direct,
        };
    }

    internal enum TranscriptionPath
    {
        Direct,
        Proxy,
        Blocked,
    }
}
