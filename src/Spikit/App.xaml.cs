using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spikit.Cli;
using Spikit.Models;
using Spikit.Services.Hotkey;
using Spikit.Services.Orchestration;
using Spikit.Services.Settings;
using Spikit.Views;
using Spikit.Views.Diagnostics;
using Spikit.Views.Onboarding;

namespace Spikit;

public partial class App : Application
{
    private readonly IHost _host;
    private readonly ILogger<App> _logger;
    private readonly CommandLineArgs _cliArgs;

    public App(IHost host, ILogger<App> logger, CommandLineArgs cliArgs)
    {
        _host = host;
        _logger = logger;
        _cliArgs = cliArgs;
        InitializeComponent();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        await _host.StartAsync();

        _logger.LogInformation("App started");

        // Acceso a la herramienta de diagnóstico EP-1 vía --diagnostics-poc. Ver ADR-0003.
        // Acceso al shell de Onboarding (EP-3.1) vía --onboarding. EP-3.8 reemplaza este
        // routing manual por el bootstrap gate (lectura del flag onboardingCompleted).
        Window startupWindow = _cliArgs switch
        {
            { DiagnosticsPoc: true } => _host.Services.GetRequiredService<PocLatencyWindow>(),
            { Onboarding: true } => _host.Services.GetRequiredService<OnboardingWindow>(),
            _ => _host.Services.GetRequiredService<MainWindow>(),
        };

        startupWindow.Show();

        // Arrancamos el orchestrator después de que la UI principal exista, para que
        // captura Dispatcher.CurrentDispatcher correctamente y el hotkey global se registre.
        // En modos de preview (POC y Onboarding shell) no levantamos el orchestrator.
        if (!_cliArgs.DiagnosticsPoc && !_cliArgs.Onboarding)
        {
            // La pill flotante se muestra primero (off-screen, modo Hidden) para que esté
            // pre-cargada al primer press y la animación de entrada arranque sin hiccup.
            var pill = _host.Services.GetRequiredService<DictationPillWindow>();
            pill.Show();

            // Hidratar la config del hotkey desde settings.json antes de arrancar el
            // orchestrator (EP-3.6). Si el JSON no existe / está corrupto, los defaults V1
            // (Ctrl+Alt+M / PushToTalk) están encapsulados en HotkeySettings.
            BootstrapHotkey();

            _host.Services.GetRequiredService<DictationOrchestrator>().Start();
        }

        base.OnStartup(e);
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
            // Caso raro pero posible: la combinación persistida ya la tomó otra app.
            // Logueamos y seguimos arrancando — el usuario va a tener que abrir Settings
            // (post-EP-4) o re-ejecutar el onboarding para cambiarla.
            _logger.LogError(ex, "No se pudo registrar el hotkey al bootstrap ({Hotkey})", definition);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _logger.LogInformation("App exiting");

        if (!_cliArgs.DiagnosticsPoc && !_cliArgs.Onboarding)
        {
            try { _host.Services.GetRequiredService<DictationOrchestrator>().Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Error disposing orchestrator"); }
        }

        await _host.StopAsync();
        _host.Dispose();
        base.OnExit(e);
    }
}
