using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Spikit.Services.Auth;

// EP-11.8 — background polling para salir del modo offline. Cuando IAuthService
// queda en IsOfflineMode = true (cache válido + tokens persistidos + server
// inalcanzable en Init), este worker reintenta `RefreshEntitlementAsync` con
// backoff progresivo hasta que el server responda. Tras el primer éxito el
// AuthService apaga la flag sólito y este worker vuelve a "idle" esperando otro
// evento (en V1 solo el Init dispara offline mode; futuras versiones podrían
// activarlo desde un transcribe-failure).
//
// Schedule de backoff (decidido en EP-11.8 con Nacho 2026-05-21):
//   30s → 60s → 120s → 300s → 300s → 300s ...   (cap a 5 min)
// Reseteado a 30s al volver a entrar en offline mode (o si el server respondió
// pero por algún motivo seguimos offline; edge improbable).
//
// El worker es resiliente a errores de red propios del intento — cualquier
// excepción del refresh la atrapa, loguea y sigue al próximo tick. Solo
// OperationCanceledException corta el loop (shutdown del host).
internal sealed class OfflineRefreshWorker : BackgroundService
{
    // Cadence de retry — duplica hasta cap a 5min. Internal para los tests inyecten
    // delays cortos sin esperar minutos reales.
    internal static readonly IReadOnlyList<TimeSpan> DefaultBackoffSchedule = new[]
    {
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(120),
        TimeSpan.FromMinutes(5),
    };

    private readonly IAuthService _auth;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<TimeSpan> _schedule;
    private readonly ILogger<OfflineRefreshWorker> _logger;

    public OfflineRefreshWorker(IAuthService auth, ILogger<OfflineRefreshWorker> logger)
        : this(auth, TimeProvider.System, DefaultBackoffSchedule, logger)
    {
    }

    internal OfflineRefreshWorker(
        IAuthService auth,
        TimeProvider time,
        IReadOnlyList<TimeSpan> schedule,
        ILogger<OfflineRefreshWorker> logger)
    {
        _auth = auth;
        _time = time;
        _schedule = schedule;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var idleDelay = TimeSpan.FromSeconds(15);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!_auth.IsOfflineMode)
            {
                // Idle — chequeo periódico. No usamos un evento porque el flip
                // a offline mode siempre ocurre durante Init (síncrono al boot),
                // y nuestro `ExecuteAsync` arranca con el host después. Un poll
                // ligero a 15s detecta el flip sin armar plumbing de eventos.
                try
                {
                    await Task.Delay(idleDelay, _time, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
                continue;
            }

            await PollUntilOnlineOrCancelledAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task PollUntilOnlineOrCancelledAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Offline mode detectado — arrancando retry loop");

        var attempt = 0;
        while (!stoppingToken.IsCancellationRequested && _auth.IsOfflineMode)
        {
            var delay = _schedule[Math.Min(attempt, _schedule.Count - 1)];
            try
            {
                await Task.Delay(delay, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            if (!_auth.IsOfflineMode) break; // se resolvió por otra vía
            if (_auth.State != AuthSessionState.LoggedIn) break; // logout durante el delay

            try
            {
                var entitlement = await _auth.RefreshEntitlementAsync(stoppingToken)
                    .ConfigureAwait(false);
                if (entitlement is not null && !_auth.IsOfflineMode)
                {
                    _logger.LogInformation(
                        "Offline mode resuelto post-retry (attempt={Attempt})", attempt + 1);
                    return;
                }
                _logger.LogDebug(
                    "Retry attempt {Attempt} sin éxito, seguimos offline", attempt + 1);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Retry attempt {Attempt} tiró excepción", attempt + 1);
            }

            attempt++;
        }
    }
}
