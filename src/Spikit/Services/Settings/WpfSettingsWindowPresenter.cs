using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spikit.ViewModels.Settings;
using Spikit.Views.Settings;

namespace Spikit.Services.Settings;

// Implementación WPF del ISettingsWindowPresenter. Mismo patrón que WpfFloatingResultPresenter:
// guarda referencia a la window viva, la reusa si Open() vuelve a llegar, y la limpia cuando
// la window se cierra para que el próximo Open() resuelva una instancia nueva del DI.
internal sealed class WpfSettingsWindowPresenter : ISettingsWindowPresenter
{
    private readonly IServiceProvider _services;
    private readonly ILogger<WpfSettingsWindowPresenter> _logger;
    private readonly Dispatcher _dispatcher;

    private SettingsWindow? _currentWindow;

    public WpfSettingsWindowPresenter(
        IServiceProvider services,
        ILogger<WpfSettingsWindowPresenter> logger)
    {
        _services = services;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public void Open(SettingsSection? section = null)
    {
        _dispatcher.BeginInvoke(() => OpenOnUiThread(section));
    }

    private void OpenOnUiThread(SettingsSection? section)
    {
        try
        {
            if (_currentWindow is null)
            {
                _currentWindow = _services.GetRequiredService<SettingsWindow>();
                _currentWindow.Closed += OnWindowClosed;
                if (section is { } s) _currentWindow.ViewModel.NavigateTo(s);
                _currentWindow.Show();
                _logger.LogDebug("SettingsWindow abierta (instancia nueva), section={Section}", section);
                return;
            }

            // Ya estaba abierta — restaurar si estaba minimizada y traer al frente.
            if (_currentWindow.WindowState == WindowState.Minimized)
            {
                _currentWindow.WindowState = WindowState.Normal;
            }

            if (section is { } existing) _currentWindow.ViewModel.NavigateTo(existing);

            _currentWindow.Activate();
            _currentWindow.Topmost = true;
            _currentWindow.Topmost = false;
            _currentWindow.Focus();
            _logger.LogDebug("SettingsWindow ya abierta — bring-to-front, section={Section}", section);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error abriendo SettingsWindow");
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is SettingsWindow w) w.Closed -= OnWindowClosed;
        _currentWindow = null;
    }
}
