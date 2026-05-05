using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spikit.Cli;
using Spikit.Views;
using Spikit.Views.Diagnostics;

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
        Window startupWindow = _cliArgs.DiagnosticsPoc
            ? _host.Services.GetRequiredService<PocLatencyWindow>()
            : _host.Services.GetRequiredService<MainWindow>();

        startupWindow.Show();

        base.OnStartup(e);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _logger.LogInformation("App exiting");

        await _host.StopAsync();
        _host.Dispose();
        base.OnExit(e);
    }
}
