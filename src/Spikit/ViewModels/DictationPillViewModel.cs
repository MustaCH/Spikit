using System.Threading;
using System.Windows.Threading;
using Spikit.Services.Audio;
using Spikit.Services.Orchestration;

namespace Spikit.ViewModels;

public enum PillVisualMode
{
    Hidden,
    Initializing,
    Recording,
    Transcribing,
    Logo,
    Leaving,
}

// Mapea estado del DictationOrchestrator + sub-estado del IAudioCaptureService al
// modo visual de la DictationPillWindow. Specs en docs/design-system.md §10.1.
public sealed class DictationPillViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan LogoFlashDuration = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan LeaveDuration = TimeSpan.FromMilliseconds(420);

    private readonly DictationOrchestrator _orchestrator;
    private readonly IAudioCaptureService _audio;
    private readonly Dispatcher _dispatcher;

    private PillVisualMode _visualMode = PillVisualMode.Hidden;
    private DictationState _orchState = DictationState.Idle;
    private AudioCaptureState _audioState = AudioCaptureState.Idle;
    private float _rmsLatest;
    private CancellationTokenSource? _logoFlashCts;
    private bool _disposed;

    public PillVisualMode VisualMode
    {
        get => _visualMode;
        private set
        {
            if (SetProperty(ref _visualMode, value))
            {
                VisualModeChanged?.Invoke(this, value);
            }
        }
    }

    public event EventHandler<PillVisualMode>? VisualModeChanged;
    public event EventHandler<float>? RmsLevelChanged;

    public DictationPillViewModel(DictationOrchestrator orchestrator, IAudioCaptureService audio)
    {
        _orchestrator = orchestrator;
        _audio = audio;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _orchestrator.StateChanged += OnOrchestratorStateChanged;
        _audio.StateChanged += OnAudioStateChanged;
        _audio.RmsLevelChanged += OnRmsLevelChanged;
    }

    private void OnOrchestratorStateChanged(object? sender, DictationState newState)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var prev = _orchState;
            _orchState = newState;
            ApplyVisualMode(orchPrev: prev);
        });
    }

    private void OnAudioStateChanged(object? sender, AudioCaptureState newState)
    {
        _dispatcher.BeginInvoke(() =>
        {
            _audioState = newState;
            ApplyVisualMode(orchPrev: _orchState);
        });
    }

    private void OnRmsLevelChanged(object? sender, float rms)
    {
        Volatile.Write(ref _rmsLatest, rms);
        RmsLevelChanged?.Invoke(this, rms);
    }

    private void ApplyVisualMode(DictationState orchPrev)
    {
        switch (_orchState)
        {
            case DictationState.Idle:
                // Si veníamos de Inserting con éxito → flash Logo → Leaving → Hidden.
                if (orchPrev == DictationState.Inserting)
                {
                    StartLogoFlash();
                }
                else if (orchPrev != DictationState.Idle && _visualMode != PillVisualMode.Hidden)
                {
                    // CB-4 / CB-8 / error de transcripción / shortcut → leaving directo.
                    StartLeavingThenHide();
                }
                break;

            case DictationState.Recording:
                // Sub-estado depende del audio: si todavía está Initializing, mostrar dots.
                VisualMode = _audioState == AudioCaptureState.Initializing
                    ? PillVisualMode.Initializing
                    : PillVisualMode.Recording;
                break;

            case DictationState.Transcribing:
            case DictationState.Inserting:
                VisualMode = PillVisualMode.Transcribing;
                break;

            case DictationState.ShowingFloatingResult:
                StartLeavingThenHide();
                break;
        }
    }

    private void StartLogoFlash()
    {
        VisualMode = PillVisualMode.Logo;

        _logoFlashCts?.Cancel();
        _logoFlashCts = new CancellationTokenSource();
        var ct = _logoFlashCts.Token;

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(LogoFlashDuration, ct); }
            catch (OperationCanceledException) { return; }

            await _dispatcher.InvokeAsync(() =>
            {
                if (_visualMode == PillVisualMode.Logo) StartLeavingThenHide();
            });
        });
    }

    private void StartLeavingThenHide()
    {
        VisualMode = PillVisualMode.Leaving;

        _logoFlashCts?.Cancel();
        _logoFlashCts = new CancellationTokenSource();
        var ct = _logoFlashCts.Token;

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(LeaveDuration, ct); }
            catch (OperationCanceledException) { return; }

            await _dispatcher.InvokeAsync(() =>
            {
                if (_visualMode == PillVisualMode.Leaving) VisualMode = PillVisualMode.Hidden;
            });
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _orchestrator.StateChanged -= OnOrchestratorStateChanged;
        _audio.StateChanged -= OnAudioStateChanged;
        _audio.RmsLevelChanged -= OnRmsLevelChanged;
        _logoFlashCts?.Cancel();
        _logoFlashCts?.Dispose();
    }
}
