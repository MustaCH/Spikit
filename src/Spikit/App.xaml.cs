using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Spikit.Views;

namespace Spikit;

public partial class App : Application
{
    private readonly IHost _host;
    private readonly ILogger<App> _logger;

    public App(IHost host, ILogger<App> logger)
    {
        _host = host;
        _logger = logger;
        InitializeComponent();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        await _host.StartAsync();

        _logger.LogInformation("App started");

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

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
