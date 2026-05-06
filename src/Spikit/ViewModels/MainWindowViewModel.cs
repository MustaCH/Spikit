using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Spikit.Services.Orchestration;

namespace Spikit.ViewModels;

// La MainWindow se reduce a chrome de debug del orchestrator: muestra el estado
// actual + el último mensaje de pill + el último texto transcripto. La pill real
// y el ciclo de vida del flow viven en DictationOrchestrator + sub-task #6
// (DictationPillWindow). Esta ventana se va a ocultar cuando llegue el tray icon.
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
