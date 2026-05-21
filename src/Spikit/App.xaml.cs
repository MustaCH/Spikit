using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spikit.Cli;
using Spikit.Models;
using Spikit.Services.Auth;
using Spikit.Services.Hotkey;
using Spikit.Services.Onboarding;
using Spikit.Services.Orchestration;
using Spikit.Services.Settings;
using Spikit.Services.SingleInstance;
using Spikit.Services.Theme;
using Spikit.Services.Toast;
using Spikit.Services.Tray;
using Spikit.ViewModels.Auth;
using Spikit.Views;
using Spikit.Views.Auth;
using Spikit.Views.Diagnostics;
using Spikit.Views.Onboarding;
using Spikit.Views.Settings;

namespace Spikit;

public partial class App : Application
{
    private readonly IHost _host;
    private readonly ILogger<App> _logger;
    private readonly CommandLineArgs _cliArgs;
    private readonly ISingleInstanceGuard _instanceGuard;

    // True una vez que entramos al modo MainApp (pill + orchestrator activos). Evita
    // double-Start si la transición onboarding→main se dispara dos veces (no debería
    // pasar, pero el costo de chequearlo es trivial).
    private bool _mainAppActive;

    // EP-11.4 — timeout máximo del await IAuthService.InitializeAsync en el bootstrap.
    // Si la red está caída o Supabase no responde, no congelamos el startup más allá
    // de este lapso — caemos a LoginRequired (SessionExpired) y el StateChanged
    // post-init cierra la window automáticamente si la sesión termina siendo válida.
    private static readonly TimeSpan AuthInitTimeout = TimeSpan.FromSeconds(3);

    // EP-11.4 — handshake entre el LoginWindow y el routing post-success. El window
    // se queda en el field mientras está visible; al Close, OnLoginWindowClosed
    // decide qué surface mostrar según el estado de auth.
    private LoginWindow? _activeLoginWindow;

    // EP-11.4 — capturado en OnStartup antes del InitializeAsync. Permite al
    // ShowLoginWindow distinguir "primer arranque sin sesión" (FirstLaunch) de
    // "tenía tokens y el refresh falló" (SessionExpired) sin manchar IAuthService.
    private bool _hadTokensAtBoot;

    public App(IHost host, ILogger<App> logger, CommandLineArgs cliArgs, ISingleInstanceGuard instanceGuard)
    {
        _host = host;
        _logger = logger;
        _cliArgs = cliArgs;
        _instanceGuard = instanceGuard;
        InitializeComponent();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        await _host.StartAsync();

        _logger.LogInformation("App started");

        // RN-9 / CB-11: cuando una segunda instancia se lance, el listener IPC dispara
        // este evento desde un thread del threadpool. Marshalamos al UI thread y
        // decidimos qué window traer al frente según el modo activo.
        _instanceGuard.OpenRequested += OnExternalOpenRequested;

        // EP-10.4: si la segunda instancia fue lanzada por Windows con un deep-link
        // `spikit://...` (callback del magic link o retorno de Stripe), recibimos el
        // URI por el pipe y se lo pasamos al dispatcher (que ya está en DI).
        _instanceGuard.UriForwardRequested += OnExternalUriRequested;

        // EP-11.4 — snapshot del estado de auth antes de InitializeAsync: si hay tokens
        // persistidos y InitializeAsync los borra (refresh falló), distinguimos
        // "primer arranque" (sin tokens nunca) de "sesión expiró" (había tokens y se
        // rompieron) sin manchar la interface de IAuthService.
        var tokenStore = _host.Services.GetRequiredService<IAuthTokenStore>();
        _hadTokensAtBoot = tokenStore.Read() is not null;

        // EP-11.4 — validar tokens contra Supabase de forma BLOQUEANTE antes de decidir
        // qué surface mostrar. Sin esto, el StartupRouter ramificaría con State viejo
        // (LoggedOut por default) y mostraría LoginWindow incluso a un user con sesión
        // válida en DPAPI — flash visible feo.
        //
        // Timeout duro de 3s: si la red está caída o Supabase no responde, no queremos
        // congelar el bootstrap sin UI. Caemos al routing con State=LoggedOut →
        // LoginRequired; si InitializeAsync termina después con éxito, el LoginViewModel
        // detecta el StateChanged y cierra la window sola (cableado en EP-11.3).
        var auth = _host.Services.GetRequiredService<IAuthService>();
        await AuthInitializeWithTimeout(auth);

        // Bootstrap del tema: leemos el setting persistido y lo aplicamos antes de mostrar
        // ventanas. Si el archivo no existe (primera ejecución) o está corrupto, queda
        // System por default (Dark salvo que Windows reporte Light). Hacerlo acá evita
        // un flash de tema incorrecto cuando la app arranca con un tema custom guardado.
        BootstrapTheme();

        var completionStore = _host.Services.GetRequiredService<IOnboardingCompletionStore>();
        var mode = StartupRouter.Decide(
            _cliArgs, completionStore.IsCompleted(),
            isLoggedIn: auth.State == AuthSessionState.LoggedIn);
        _logger.LogInformation(
            "Startup mode → {Mode} (authState={AuthState}, hadTokens={HadTokens})",
            mode, auth.State, _hadTokensAtBoot);

        switch (mode)
        {
            case StartupRouter.StartupMode.DiagnosticsPoc:
                // POC bypassea auth — el dispatcher de SpikitUri también queda inactivo
                // porque no tendría sentido en este modo. Igualmente la POC no toca
                // protocol handlers.
                _host.Services.GetRequiredService<PocLatencyWindow>().Show();
                break;

            case StartupRouter.StartupMode.LoginRequired:
                ShowLoginWindow();
                break;

            case StartupRouter.StartupMode.Onboarding:
                // EP-11.4 — si hay deep-link en argv que llegó junto con sesión válida
                // (caso edge: user clickeó un magic link mientras ya estaba logueado en
                // otra sesión), dispatchearlo silenciosamente y seguir al Onboarding.
                if (!string.IsNullOrEmpty(_cliArgs.SpikitUri))
                {
                    DispatchSpikitUri(_cliArgs.SpikitUri);
                }
                ShowOnboardingWindow();
                break;

            case StartupRouter.StartupMode.MainApp:
                if (!string.IsNullOrEmpty(_cliArgs.SpikitUri))
                {
                    DispatchSpikitUri(_cliArgs.SpikitUri);
                }
                EnterMainAppMode();
                break;
        }

        base.OnStartup(e);
    }

    // Cubre el "happy path rápido" (~200ms con cache fresh, ~500ms con refresh) y el
    // peor caso (DNS roto / Supabase down) con cap a 3s para no congelar el arranque.
    // En el peor caso, InitializeAsync sigue corriendo en background (vía el _ del Task)
    // y eventualmente puede mutar State a LoggedIn — el LoginViewModel está suscrito
    // a StateChanged y maneja esa transición.
    private async Task AuthInitializeWithTimeout(IAuthService auth)
    {
        var initTask = auth.InitializeAsync(CancellationToken.None);
        var winner = await Task.WhenAny(initTask, Task.Delay(AuthInitTimeout));

        if (winner != initTask)
        {
            _logger.LogWarning(
                "Auth.InitializeAsync no terminó en {Timeout}, sigo con State={State}",
                AuthInitTimeout, auth.State);
            // initTask sigue corriendo en background — observar excepciones para no
            // que se traguen silenciosamente. El IAuthService loguea internamente.
            _ = initTask.ContinueWith(t =>
            {
                if (t.IsFaulted)
                    _logger.LogError(t.Exception, "Auth.InitializeAsync falló post-timeout");
            }, TaskScheduler.Default);
        }
    }

    // EP-11.4 — single UI visible cuando no hay sesión. Decide variante (FirstLaunch vs
    // SessionExpired) en base al snapshot tomado pre-InitializeAsync. Si la app fue
    // lanzada con un deep-link `spikit://auth-callback?...` o `spikit://auth-pending?...`
    // (caso "user clickeó magic link con app cerrada"), inyectamos los params al VM
    // directamente (sin pasar por SpikitUriDispatcher → toast redundante con el flow
    // visible).
    //
    // OnLoginWindowClosed decide qué hacer después (transición inline a Onboarding/
    // MainApp si la sesión quedó válida; Shutdown si no — D-11 del flows.md).
    private void ShowLoginWindow()
    {
        var window = _host.Services.GetRequiredService<LoginWindow>();
        _activeLoginWindow = window;

        var variant = (_host.Services.GetRequiredService<IAuthService>().State == AuthSessionState.LoggedOut
                       && _hadTokensAtBoot)
            ? LoginIdleVariant.SessionExpired
            : LoginIdleVariant.FirstLaunch;
        window.ViewModel.EnterIdle(variant);

        // Si argv trae un deep-link, inyectarlo al VM ANTES de Show — sino el user
        // ve un flash de Idle antes de la transición real al estado correcto.
        DispatchArgvUriToLoginVm(window.ViewModel);

        window.Closed += OnLoginWindowClosed;
        window.Show();
    }

    // Procesa el argv URI cuando el LoginWindow es la surface inicial. NO usa
    // SpikitUriDispatcher porque queremos saltar el toast (la window mostrará feedback
    // visual mejor en sus propios estados).
    private void DispatchArgvUriToLoginVm(LoginViewModel vm)
    {
        var raw = _cliArgs.SpikitUri;
        if (string.IsNullOrEmpty(raw)) return;

        var parsed = SpikitUriParser.TryParse(raw);
        if (parsed is null)
        {
            _logger.LogWarning("ShowLoginWindow: argv URI no parseable, ignorando ({Raw})", raw);
            return;
        }

        switch (parsed.Kind)
        {
            case SpikitUriKind.AuthCallback:
                // Fire-and-forget: el flow del VM va a transicionar Validating →
                // LoadingEntitlement → Success → RequestClose, y OnLoginWindowClosed
                // se ocupará del ruteo post-success.
                _ = vm.HandleAuthCallbackAsync(parsed.Params, CancellationToken.None);
                break;

            case SpikitUriKind.AuthPending:
                if (parsed.Params.TryGetValue("email", out var email) && !string.IsNullOrWhiteSpace(email))
                {
                    vm.HandleAuthPending(email);
                }
                break;

            case SpikitUriKind.BillingReturn:
                // Edge raro: app cerrada + retorno de Stripe + sin sesión. Sin token
                // no podemos refrescar entitlement. Loguear + ignorar — el user va a
                // necesitar loguearse igual.
                _logger.LogWarning("ShowLoginWindow: billing-return en argv sin sesión, ignorado");
                break;

            case SpikitUriKind.Unknown:
            default:
                _logger.LogWarning("ShowLoginWindow: argv URI kind desconocido, ignorado ({Raw})", raw);
                break;
        }
    }

    // Llamado por LoginWindow al iniciar el fade-out (D-14), antes del Close real.
    // Evita una race en la que un URI forwardeado entre el RequestClose y el Closed
    // termine ruteado al VM mid-fade. El handler de Closed después es no-op sobre
    // _activeLoginWindow porque ya está null.
    internal void ClearActiveLoginWindow()
    {
        _activeLoginWindow = null;
    }

    private void OnLoginWindowClosed(object? sender, EventArgs e)
    {
        if (sender is LoginWindow window)
        {
            window.Closed -= OnLoginWindowClosed;
        }
        // Idempotente: ClearActiveLoginWindow puede haberlo nulleado antes en el fade.
        _activeLoginWindow = null;

        var auth = _host.Services.GetRequiredService<IAuthService>();
        if (auth.State != AuthSessionState.LoggedIn)
        {
            // D-11 del flows.md: cerrar LoginWindow sin sesión = cerrar la app.
            _logger.LogInformation("LoginWindow cerrada sin sesión activa — shutdown");
            Shutdown();
            return;
        }

        // Sesión válida — transición inline al modo que corresponda (sin relanzar
        // proceso). Mismo split que el switch del OnStartup, pero a esta altura sabemos
        // que isLoggedIn=true y --diagnostics-poc no aplica (no llegamos por ese path).
        //
        // TODO(EP-11.6): cuando exista el flow de logout que vuelve a LoginWindow sin
        // reiniciar el proceso, `_cliArgs.Onboarding` queda "pegado" — un user que abrió
        // la app con `--onboarding` y después hizo logout/login va a re-entrar al
        // wizard aunque ya lo haya completado. Nullear el flag tras el primer consumo o
        // exponer `ConsumeOnboarding()` en CommandLineArgs.
        var completionStore = _host.Services.GetRequiredService<IOnboardingCompletionStore>();
        if (_cliArgs.Onboarding || !completionStore.IsCompleted())
        {
            _logger.LogInformation("LoginWindow cerrada con sesión válida — transición a Onboarding");
            ShowOnboardingWindow();
        }
        else
        {
            _logger.LogInformation("LoginWindow cerrada con sesión válida — transición a MainApp");
            EnterMainAppMode();
        }
    }

    // Levanta el OnboardingWindow modal y se suscribe al Closed para decidir qué hacer
    // después: si el flag quedó en true, transicionar al MainApp inline (sin reiniciar
    // la app); si no, cerrar la app — el usuario abandonó sin completar (RN-5).
    private void ShowOnboardingWindow()
    {
        var window = _host.Services.GetRequiredService<OnboardingWindow>();
        window.Closed += OnOnboardingWindowClosed;
        window.Show();
    }

    private void OnOnboardingWindowClosed(object? sender, EventArgs e)
    {
        if (sender is OnboardingWindow window)
        {
            window.Closed -= OnOnboardingWindowClosed;
        }

        var completionStore = _host.Services.GetRequiredService<IOnboardingCompletionStore>();
        if (!completionStore.IsCompleted())
        {
            _logger.LogInformation("Onboarding cerrado sin completar — shutdown");
            Shutdown();
            return;
        }

        _logger.LogInformation("Onboarding completado — transición inline a MainApp");
        EnterMainAppMode();
    }

    // Equivalente al flujo "no --diagnostics-poc, no --onboarding" anterior: pill flotante
    // pre-cargada, hotkey hidratado desde settings.json, orchestrator arrancado, tray icon
    // visible. Idempotente vía _mainAppActive — el OnboardingWindow del modo Onboarding ya
    // pudo haber dejado la pill + orchestrator en estado "started" durante el step Prueba;
    // en ese caso el cleanup del OnClosing los Stop()-eó y acá los volvemos a arrancar.
    //
    // El usuario en MainApp mode no tiene ninguna ventana visible — solo el ícono del tray.
    // Settings, FloatingResultWindow y la pill cubren todo lo que necesita ver.
    private void EnterMainAppMode()
    {
        if (_mainAppActive) return;
        _mainAppActive = true;

        var pill = _host.Services.GetRequiredService<DictationPillWindow>();
        pill.Show();

        BootstrapHotkey();

        _host.Services.GetRequiredService<DictationOrchestrator>().Start();

        // Tray icon es el entry point permanente (EP-4.2). Se inicializa solo en MainApp
        // mode — durante onboarding o --diagnostics-poc no tiene sentido tener tray.
        _host.Services.GetRequiredService<ITrayIconService>().Initialize();
    }

    private void BootstrapTheme()
    {
        try
        {
            var settings = _host.Services.GetRequiredService<ISettingsService>().Load();
            var theme = settings.General.TryToTheme();
            _host.Services.GetRequiredService<IThemeService>().Apply(theme);
            _logger.LogInformation("Tema bootstrapped: {Theme}", theme);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Bootstrap del tema falló — queda con el default de App.xaml");
        }
    }

    private void BootstrapHotkey()
    {
        var settings = _host.Services.GetRequiredService<ISettingsService>().Load();
        var hotkeyService = _host.Services.GetRequiredService<IHotkeyService>();
        var orchestrator = _host.Services.GetRequiredService<DictationOrchestrator>();

        if (!settings.Hotkey.TryToRuntime(out var definition, out var mode))
        {
            _logger.LogWarning("settings.json tiene un bloque hotkey inválido — usando defaults V1");
        }

        orchestrator.SetMode(mode);

        try
        {
            hotkeyService.Register(definition);
            _logger.LogInformation("Hotkey bootstrap OK: {Hotkey} / {Mode}", definition, mode);
        }
        catch (HotkeyRegistrationException ex)
        {
            // CB-7: la combinación persistida ya la tomó otra app entre sesiones. Logueamos,
            // seguimos arrancando (la app sigue siendo útil — Settings, historial, etc.) y
            // mostramos un toast warning ámbar invitando al usuario a cambiar la combinación.
            // Auto-dismiss más largo (8s) porque requiere atención del usuario (FLOW 5 / CB-7).
            _logger.LogError(ex, "No se pudo registrar el hotkey al bootstrap ({Hotkey})", definition);
            var toast = _host.Services.GetRequiredService<IToastService>();
            toast.Show(
                ToastSeverity.Warning,
                "Tu hotkey no pudo registrarse, otra app lo está usando. Cambialo en Settings.",
                action: new ToastAction("Abrir Settings → Hotkey", () => throw new NotImplementedException()),
                autoDismiss: TimeSpan.FromSeconds(8),
                dedupeKey: "hotkey-conflict-startup");
        }
    }

    private void OnExternalOpenRequested(object? sender, EventArgs e)
    {
        // El evento llega desde un thread del threadpool del listener IPC. WPF requiere
        // marshalar a la dispatcher antes de tocar windows. BeginInvoke (no Invoke) para
        // no bloquear al listener — la suscripción puede correr en un loop posterior.
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (_mainAppActive)
                {
                    _logger.LogInformation("OPEN_SETTINGS externo → abriendo SettingsWindow");
                    _host.Services.GetRequiredService<ISettingsWindowPresenter>().Open();
                    return;
                }

                // Caso edge: la primera instancia está en onboarding o en --diagnostics-poc.
                // No tiene tray ni Settings inicializados; lo más útil es traer al frente
                // la ventana principal del modo actual para que el usuario vea que la app
                // ya está corriendo y no quede confundido.
                if (TryBringActiveBootstrapWindowToFront())
                {
                    _logger.LogInformation("OPEN_SETTINGS externo → ventana de bootstrap traída al frente");
                    return;
                }

                _logger.LogWarning("OPEN_SETTINGS externo recibido pero no hay window que traer al frente");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error procesando OPEN_SETTINGS externo");
            }
        });
    }

    private void OnExternalUriRequested(object? sender, string uri)
    {
        // Mismo patrón que OnExternalOpenRequested — el evento llega del threadpool del
        // listener IPC. Marshal al UI thread con BeginInvoke.
        //
        // EP-11.4 — si hay LoginWindow activa y el URI es auth-callback / auth-pending,
        // delegamos al VM directamente para preservar el flow visual (microflash D-14
        // en el caso auth-callback). El dispatcher general saltaría esos estados.
        Dispatcher.BeginInvoke(() =>
        {
            if (_activeLoginWindow is { } login
                && TryRouteUriToLoginVm(uri, login.ViewModel))
            {
                return;
            }
            DispatchSpikitUri(uri);
        });
    }

    // Variante runtime de DispatchArgvUriToLoginVm: misma lógica pero devuelve bool
    // (false si el URI no aplica al LoginVM — caller cae al dispatcher general).
    private bool TryRouteUriToLoginVm(string uri, LoginViewModel vm)
    {
        var parsed = SpikitUriParser.TryParse(uri);
        if (parsed is null) return false;

        switch (parsed.Kind)
        {
            case SpikitUriKind.AuthCallback:
                _ = vm.HandleAuthCallbackAsync(parsed.Params, CancellationToken.None);
                return true;

            case SpikitUriKind.AuthPending:
                if (parsed.Params.TryGetValue("email", out var email) && !string.IsNullOrWhiteSpace(email))
                {
                    vm.HandleAuthPending(email);
                    return true;
                }
                return false;

            default:
                // billing-return / Unknown: que el dispatcher general lo maneje (toast).
                return false;
        }
    }

    private void DispatchSpikitUri(string uri)
    {
        try
        {
            var dispatcher = _host.Services.GetRequiredService<ISpikitUriDispatcher>();
            _ = dispatcher.DispatchAsync(uri, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dispatch de spikit:// falló al arrancar ({Uri})", uri);
        }
    }

    private bool TryBringActiveBootstrapWindowToFront()
    {
        foreach (Window window in Windows)
        {
            // EP-11.4 — LoginWindow agregado: si el user lanza spikit.exe sin args
            // mientras la primera instancia está en LoginRequired, traemos la window
            // al frente en lugar de evaporar silenciosamente la segunda instancia.
            if (window is OnboardingWindow or PocLatencyWindow or LoginWindow)
            {
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
                window.Topmost = true;
                window.Topmost = false;
                window.Focus();
                return true;
            }
        }
        return false;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _logger.LogInformation("App exiting");

        _instanceGuard.OpenRequested -= OnExternalOpenRequested;
        _instanceGuard.UriForwardRequested -= OnExternalUriRequested;
        try { _instanceGuard.Dispose(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Error disposing single-instance guard"); }

        if (_mainAppActive)
        {
            try { _host.Services.GetRequiredService<ITrayIconService>().Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disposing tray icon"); }

            try { _host.Services.GetRequiredService<DictationOrchestrator>().Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disposing orchestrator"); }
        }

        await _host.StopAsync();
        _host.Dispose();
        base.OnExit(e);
    }
}
