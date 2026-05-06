using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Spikit.Models;
using Spikit.Services.Audio;
using Spikit.Services.Hotkey;
using Spikit.Services.Transcription;

namespace Spikit.ViewModels;

// NOTA TEMPORAL: este VM hace de smoke-test del HotkeyService + AudioCaptureService
// + WhisperApiTranscriptionService hasta que llegue DictationOrchestrator (EP-2 sub-task #5).
// Cuando exista, mover el flow al orchestrator y dejar este VM acotado al chrome.
public class MainWindowViewModel : ViewModelBase, IDisposable
{
    private const int RefreshIntervalMs = 60;
    private const int SampleRateHz = 16_000;

    private readonly IHotkeyService _hotkey;
    private readonly IAudioCaptureService _audio;
    private readonly ITranscriptionService _transcription;
    private readonly ILogger<MainWindowViewModel> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;
    private readonly List<short> _sessionBuffer = new(SampleRateHz * 30);
    private readonly object _bufferLock = new();

    private string _hotkeyStatus = "Inicializando…";
    private string _hotkeyLabel = HotkeyDefinition.Default.ToString();
    private int _pressCount;
    private string _audioState = "Idle";
    private float _rmsLevel;
    private float _rmsLatest;
    private long _samplesAccumulator;
    private long _samplesInSession;
    private string _transcriptionState = "—";
    private string _transcribedText = string.Empty;
    private long _transcriptionDurationMs;
    private CancellationTokenSource? _audioCts;
    private CancellationTokenSource? _transcribeCts;
    private bool _disposed;

    public MainWindowViewModel(
        IHotkeyService hotkey,
        IAudioCaptureService audio,
        ITranscriptionService transcription,
        ILogger<MainWindowViewModel> logger)
    {
        _hotkey = hotkey;
        _audio = audio;
        _transcription = transcription;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _hotkey.HotkeyPressed += OnHotkeyPressed;
        _hotkey.HotkeyReleased += OnHotkeyReleased;
        _audio.StateChanged += OnAudioStateChanged;
        _audio.RmsLevelChanged += OnRmsLevelChanged;
        _audio.SamplesAvailable += OnSamplesAvailable;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(RefreshIntervalMs),
        };
        _refreshTimer.Tick += OnRefreshTick;

        try
        {
            _hotkey.Register(HotkeyDefinition.Default);
            HotkeyStatus = "Esperando press…";
        }
        catch (HotkeyRegistrationException ex)
        {
            _logger.LogWarning(ex, "No se pudo registrar el hotkey");
            HotkeyStatus = $"⚠ {ex.Message}";
        }
    }

    public string HotkeyLabel
    {
        get => _hotkeyLabel;
        private set => SetProperty(ref _hotkeyLabel, value);
    }

    public string HotkeyStatus
    {
        get => _hotkeyStatus;
        private set => SetProperty(ref _hotkeyStatus, value);
    }

    public int PressCount
    {
        get => _pressCount;
        private set => SetProperty(ref _pressCount, value);
    }

    public string AudioState
    {
        get => _audioState;
        private set => SetProperty(ref _audioState, value);
    }

    public float RmsLevel
    {
        get => _rmsLevel;
        private set => SetProperty(ref _rmsLevel, value);
    }

    public long SamplesInSession
    {
        get => _samplesInSession;
        private set
        {
            if (SetProperty(ref _samplesInSession, value))
            {
                OnPropertyChanged(nameof(SessionDurationMs));
            }
        }
    }

    public long SessionDurationMs => _samplesInSession * 1000 / SampleRateHz;

    public string TranscriptionState
    {
        get => _transcriptionState;
        private set => SetProperty(ref _transcriptionState, value);
    }

    public string TranscribedText
    {
        get => _transcribedText;
        private set => SetProperty(ref _transcribedText, value);
    }

    public long TranscriptionDurationMs
    {
        get => _transcriptionDurationMs;
        private set => SetProperty(ref _transcriptionDurationMs, value);
    }

    private async void OnHotkeyPressed(object? sender, EventArgs e)
    {
        PressCount++;
        HotkeyStatus = $"● Pressed (#{PressCount})";
        Interlocked.Exchange(ref _samplesAccumulator, 0);
        SamplesInSession = 0;
        RmsLevel = 0;
        TranscriptionState = "—";
        TranscribedText = string.Empty;
        TranscriptionDurationMs = 0;

        lock (_bufferLock) _sessionBuffer.Clear();

        _refreshTimer.Start();

        _audioCts?.Dispose();
        _audioCts = new CancellationTokenSource();

        try
        {
            await _audio.StartAsync(_audioCts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AudioCapture.StartAsync falló");
            HotkeyStatus = $"⚠ Audio start falló: {ex.Message}";
            _refreshTimer.Stop();
        }
    }

    private async void OnHotkeyReleased(object? sender, EventArgs e)
    {
        HotkeyStatus = "○ Released — cerrando captura…";
        try
        {
            await _audio.StopAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AudioCapture.StopAsync falló");
        }

        _refreshTimer.Stop();
        OnRefreshTick(this, EventArgs.Empty);
        HotkeyStatus = $"○ Sesión cerrada — {SamplesInSession:N0} samples ({SessionDurationMs} ms)";

        await TranscribeSessionAsync().ConfigureAwait(true);
    }

    private async Task TranscribeSessionAsync()
    {
        short[] snapshot;
        lock (_bufferLock) snapshot = _sessionBuffer.ToArray();

        if (snapshot.Length == 0)
        {
            TranscriptionState = "skip (sin samples)";
            return;
        }

        TranscriptionState = "Transcribiendo…";
        var wav = WavWriter.WriteWavFromPcm16(snapshot, SampleRateHz, channels: 1);
        _logger.LogInformation("WAV armado: {Bytes} bytes para {Samples} samples", wav.Length, snapshot.Length);

        _transcribeCts?.Dispose();
        _transcribeCts = new CancellationTokenSource();

        var sw = Stopwatch.StartNew();
        try
        {
            var text = await _transcription.TranscribeAsync(wav, _transcribeCts.Token).ConfigureAwait(true);
            sw.Stop();
            TranscriptionDurationMs = sw.ElapsedMilliseconds;
            TranscriptionState = "✓ OK";
            TranscribedText = string.IsNullOrWhiteSpace(text) ? "(vacío)" : text;
            _logger.LogInformation(
                "Transcripción OK en {DurationMs} ms: {Text}",
                sw.ElapsedMilliseconds, text);
        }
        catch (TranscriptionException ex)
        {
            sw.Stop();
            TranscriptionDurationMs = sw.ElapsedMilliseconds;
            TranscriptionState = ex.StatusCode is { } code
                ? $"✗ HTTP {(int)code}"
                : "✗ Error";
            TranscribedText = ex.Message;
            _logger.LogError(ex, "Transcripción falló");
        }
        catch (OperationCanceledException)
        {
            TranscriptionState = "✗ Cancelada";
            sw.Stop();
            TranscriptionDurationMs = sw.ElapsedMilliseconds;
        }
    }

    private void OnAudioStateChanged(object? sender, AudioCaptureState state)
    {
        _dispatcher.BeginInvoke(() => AudioState = state.ToString());
    }

    private void OnRmsLevelChanged(object? sender, float rms)
    {
        Volatile.Write(ref _rmsLatest, rms);
    }

    private void OnSamplesAvailable(object? sender, short[] samples)
    {
        Interlocked.Add(ref _samplesAccumulator, samples.Length);
        lock (_bufferLock) _sessionBuffer.AddRange(samples);
    }

    private void OnRefreshTick(object? sender, EventArgs e)
    {
        RmsLevel = Volatile.Read(ref _rmsLatest);
        SamplesInSession = Interlocked.Read(ref _samplesAccumulator);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTick;
        _hotkey.HotkeyPressed -= OnHotkeyPressed;
        _hotkey.HotkeyReleased -= OnHotkeyReleased;
        _audio.StateChanged -= OnAudioStateChanged;
        _audio.RmsLevelChanged -= OnRmsLevelChanged;
        _audio.SamplesAvailable -= OnSamplesAvailable;
        _audioCts?.Cancel();
        _audioCts?.Dispose();
        _transcribeCts?.Cancel();
        _transcribeCts?.Dispose();
    }
}
