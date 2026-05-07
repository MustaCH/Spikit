using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Spikit.Services.Orchestration;

namespace Spikit.ViewModels;

// La MainWindow es chrome de debug del orchestrator: muestra estado + pill message + último
// texto transcripto. El entry point real a Settings ahora vive en el TrayIcon (EP-4.2);
// esta window queda como utilidad de desarrollo. Cerrarla NO cierra la app — el ShutdownMode
// es OnExplicitShutdown y la app sigue viva por el tray.
public class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly DictationOrchestrator _orchestrator;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly Dispatcher _dispatcher;

    private string _state = nameof(DictationState.Idle);
    private string _pillMessage = "Iniciando…";
    private string _lastTranscription = string.Empty;
    private int _sessionCount;
    private bool _disposed;

    public MainWindowViewModel(
        DictationOrchestrator orchestrator,
        ILogger<MainWindowViewModel> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _orchestrator.StateChanged += OnStateChanged;
        _orchestrator.PillMessageChanged += OnPillMessageChanged;
        _orchestrator.TranscriptionCompleted += OnTranscriptionCompleted;
    }

    public string State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public string PillMessage
    {
        get => _pillMessage;
        private set => SetProperty(ref _pillMessage, value);
    }

    public string LastTranscription
    {
        get => _lastTranscription;
        private set => SetProperty(ref _lastTranscription, value);
    }

    public int SessionCount
    {
        get => _sessionCount;
        private set => SetProperty(ref _sessionCount, value);
    }

    private void OnStateChanged(object? sender, DictationState state)
    {
        _dispatcher.BeginInvoke(() =>
        {
            State = state.ToString();
            if (state == DictationState.Recording) SessionCount++;
        });
    }

    private void OnPillMessageChanged(object? sender, string message)
    {
        _dispatcher.BeginInvoke(() => PillMessage = message);
    }

    private void OnTranscriptionCompleted(object? sender, string text)
    {
        _dispatcher.BeginInvoke(() => LastTranscription = text);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _orchestrator.StateChanged -= OnStateChanged;
        _orchestrator.PillMessageChanged -= OnPillMessageChanged;
        _orchestrator.TranscriptionCompleted -= OnTranscriptionCompleted;
    }
}
