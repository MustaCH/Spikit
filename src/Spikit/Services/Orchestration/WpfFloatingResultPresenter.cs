using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spikit.Services.Insertion;
using Spikit.ViewModels;
using Spikit.Views;

namespace Spikit.Services.Orchestration;

// Implementación real de IFloatingResultPresenter usando WPF. Reemplaza el stub
// LoggingFloatingResultPresenter cuando llegó la sub-task #7. Una sola window se
// crea on-demand y se reusa: si Show llega de nuevo mientras está visible, actualiza
// contenido y trae al frente.
internal sealed class WpfFloatingResultPresenter : IFloatingResultPresenter
{
    private readonly IServiceProvider _services;
    private readonly ILogger<WpfFloatingResultPresenter> _logger;
    private readonly Dispatcher _dispatcher;

    private FloatingResultWindow? _currentWindow;
    private FloatingResultViewModel? _currentViewModel;

    public WpfFloatingResultPresenter(
        IServiceProvider services,
        ILogger<WpfFloatingResultPresenter> logger)
    {
        _services = services;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public void Show(string text, InsertionResult reason)
    {
        _dispatcher.BeginInvoke(() => ShowOnUiThread(text, reason));
    }

    public void Hide()
    {
        _dispatcher.BeginInvoke(() =>
        {
            _currentWindow?.Close();
            _currentWindow = null;
            _currentViewModel = null;
        });
    }

    private void ShowOnUiThread(string text, InsertionResult reason)
    {
        try
        {
            if (_currentWindow is null || _currentViewModel is null)
            {
                _currentViewModel = _services.GetRequiredService<FloatingResultViewModel>();
                _currentWindow = new FloatingResultWindow(_currentViewModel);
                _currentWindow.Closed += OnWindowClosed;
            }

            _currentViewModel.Configure(text, reason);
            _currentWindow.Show();
            _currentWindow.Activate();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error mostrando FloatingResultWindow");
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is FloatingResultWindow w) w.Closed -= OnWindowClosed;
        _currentWindow = null;
        _currentViewModel = null;
    }
}
