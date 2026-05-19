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
using Spikit.Views;
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

        // EP-10.4: si hay tokens persistidos de una sesión previa, validamos contra
        // Supabase y poblamos State + Profile + Entitlement cache. Fire-and-forget —
        // la red puede tardar y no queremos bloquear startup. AuthService maneja errores
        // de red preservando los tokens; errores de validación los borra y deja LoggedOut.
        _ = _host.Services.GetRequiredService<IAuthService>().InitializeAsync(CancellationToken.None);

        // Si la app fue lanzada con un deep-link (Windows abre Spikit con `spikit://...`
        // y nosotros somos la primera instancia), procesamos el URI ya con el host arriba.
        if (!string.IsNullOrEmpty(_cliArgs.SpikitUri))
        {
            DispatchSpikitUri(_cliArgs.SpikitUri);
        }

        // Bootstrap del tema: leemos el setting persistido y lo aplicamos antes de mostrar
        // ventanas. Si el archivo no existe (primera ejecución) o está corrupto, queda
        // System por default (Dark salvo que Windows reporte Light). Hacerlo acá evita
        // un flash de tema incorrecto cuando la app arranca con un tema custom guardado.
        BootstrapTheme();

        var completionStore = _host.Services.GetRequiredService<IOnboardingCompletionStore>();
        var mode = StartupRouter.Decide(_cliArgs, completionStore.IsCompleted());
        _logger.LogInformation("Startup mode → {Mode}", mode);

        switch (mode)
        {
            case StartupRouter.StartupMode.DiagnosticsPoc:
                _host.Services.GetRequiredService<PocLatencyWindow>().Show();
                break;

            case StartupRouter.StartupMode.Onboarding:
                ShowOnboardingWindow();
                break;

            case StartupRouter.StartupMode.MainApp:
                EnterMainAppMode();
                break;
        }

        base.OnStartup(e);
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
        // listener IPC. Marshal al UI thread con BeginInvoke; el dispatcher hace IO async.
        Dispatcher.BeginInvoke(() => DispatchSpikitUri(uri));
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
            if (window is OnboardingWindow or PocLatencyWindow)
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
