using Microsoft.Extensions.Logging;
using Spikit.Services.Hotkey;
using Spikit.Services.Orchestration;
using Spikit.Services.Tray;

namespace Spikit.Services.Auth;

// Implementación canónica de ISessionLifecycleService (EP-11.7).
//
// Orden del cleanup (relevante: queremos que ningún servicio runtime siga vivo cuando
// llamamos IAuthService.LogoutAsync, porque ese dispara StateChanged y el handler de
// App.xaml.cs asume "todo lo runtime ya está apagado, falta solo el UI"):
//
//  1. Cancelar dictado en curso (descarta audio + libera tokens internos).
//  2. Stop del orchestrator (desuscribe del hotkey/audio, deja el singleton vivo
//     para un Start() futuro post-login).
//  3. Unregister del hotkey principal + cancel hotkey (libera la combinación en Win32).
//  4. Shutdown del tray (libera HICON, oculta el ícono — re-Initializable después).
//  5. IAuthService.LogoutAsync → limpia tokens DPAPI, dispara StateChanged.
//
// Errores: cada paso loguea + sigue. Una falla en stop del orchestrator no debe impedir
// que el tray se oculte y los tokens se limpien — el user lo pidió, completar el intent
// hasta donde se pueda.
internal sealed class SessionLifecycleService : ISessionLifecycleService
{
    private readonly IAuthService _auth;
    private readonly IDictationLifecycle _orchestrator;
    private readonly IHotkeyService _hotkey;
    private readonly ITrayIconService _tray;
    private readonly ILogger<SessionLifecycleService> _logger;

    public SessionLifecycleService(
        IAuthService auth,
        IDictationLifecycle orchestrator,
        IHotkeyService hotkey,
        ITrayIconService tray,
        ILogger<SessionLifecycleService> logger)
    {
        _auth = auth;
        _orchestrator = orchestrator;
        _hotkey = hotkey;
        _tray = tray;
        _logger = logger;
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        _logger.LogInformation("Logout iniciado — apagando servicios runtime");

        try
        {
            await _orchestrator.CancelActiveSessionAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Logout: CancelActiveSessionAsync falló");
        }

        try
        {
            _orchestrator.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Logout: orchestrator.Stop falló");
        }

        try
        {
            _hotkey.Unregister();
            _hotkey.UnregisterCancelHotkey();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Logout: hotkey unregister falló");
        }

        try
        {
            _tray.Shutdown();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Logout: tray.Shutdown falló");
        }

        // Auth.LogoutAsync dispara StateChanged tras SetLoggedOut() — App.xaml.cs lo
        // escucha y se encarga del UI cleanup (cerrar ventanas + abrir LoginWindow).
        await _auth.LogoutAsync(ct).ConfigureAwait(true);

        _logger.LogInformation("Logout completado");
    }
}
