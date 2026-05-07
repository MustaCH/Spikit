using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spikit.Cli;
using Spikit.Services.Orchestration;
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

            _host.Services.GetRequiredService<DictationOrchestrator>().Start();
        }

        base.OnStartup(e);
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
