using System.Diagnostics;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Spikit.Models;
using Spikit.Native;
using Spikit.Services.Audio;
using Spikit.Services.Hotkey;
using Spikit.Services.Insertion;
using Spikit.Services.Transcription;

namespace Spikit.Services.Orchestration;

// Coordina el flow completo del dictado. State machine explícita Idle → Recording →
// Transcribing → Inserting → Idle (o ShowingFloatingResult si paste falla).
// Decisiones de comportamiento en docs/architecture.md § "Arquitectura del feature crítico".
public sealed class DictationOrchestrator : IDisposable
{
    private const int SampleRateHz = 16_000;
    private const int MinSessionSamples = SampleRateHz / 2; // 500 ms — CB-4
    private static readonly TimeSpan MaxRecordingDuration = TimeSpan.FromMinutes(10); // RN-8

    private readonly IHotkeyService _hotkey;
    private readonly IAudioCaptureService _audio;
    private readonly ITranscriptionService _transcription;
    private readonly ITextInsertionService _insertion;
    private readonly IFloatingResultPresenter _floatingPresenter;
    private readonly ILogger<DictationOrchestrator> _logger;
    private readonly Dispatcher _dispatcher;

    private readonly List<short> _sessionBuffer = new(SampleRateHz * 30);
    private readonly object _bufferLock = new();

    private DictationState _state = DictationState.Idle;
    private IntPtr _targetHwnd;
    private CancellationTokenSource? _recordingTimeoutCts;
    private bool _started;
    private bool _disposed;

    public DictationOrchestrator(
        IHotkeyService hotkey,
        IAudioCaptureService audio,
        ITranscriptionService transcription,
        ITextInsertionService insertion,
        IFloatingResultPresenter floatingPresenter,
        ILogger<DictationOrchestrator> logger)
    {
        _hotkey = hotkey;
        _audio = audio;
        _transcription = transcription;
        _insertion = insertion;
        _floatingPresenter = floatingPresenter;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public DictationState State => _state;

    // Disparado en cada transición de estado. La pill se suscribe acá para refrescar visual.
    public event EventHandler<DictationState>? StateChanged;

    // Mensaje user-facing para mostrar en la pill (incluye estados informativos como
    // "Audio muy corto" / "No detectamos audio" / errores de Whisper).
    public event EventHandler<string>? PillMessageChanged;

    // Texto transcripto exitoso (post-Whisper, pre-insertion). El consumer puede usarlo
    // para mostrar feedback visual o registrar telemetría.
    public event EventHandler<string>? TranscriptionCompleted;

    public void Start()
    {
        EnsureNotDisposed();
        if (_started) return;
        _started = true;

        _hotkey.HotkeyPressed += OnHotkeyPressed;
        _hotkey.HotkeyReleased += OnHotkeyReleased;
        _audio.SamplesAvailable += OnSamplesAvailable;

        try
        {
            _hotkey.Register(HotkeyDefinition.Default);
            EmitPillMessage($"Apretá {HotkeyDefinition.Default} para dictar");
        }
        catch (HotkeyRegistrationException ex)
        {
            _logger.LogError(ex, "No se pudo registrar el hotkey al iniciar el orchestrator");
            EmitPillMessage($"⚠ {ex.Message}");
        }
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;

        _hotkey.HotkeyPressed -= OnHotkeyPressed;
        _hotkey.HotkeyReleased -= OnHotkeyReleased;
        _audio.SamplesAvailable -= OnSamplesAvailable;

        try { _hotkey.Unregister(); } catch { /* tragado: shutdown */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _recordingTimeoutCts?.Cancel();
        _recordingTimeoutCts?.Dispose();
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        // RN-6: en estado activo (Recording/Transcribing/Inserting/ShowingFloatingResult),
        // ignoramos nuevos press. Cancelación queda para EP-5.
        if (_state != DictationState.Idle)
        {
            _logger.LogDebug("HotkeyPressed ignorado, estado actual: {State} (RN-6)", _state);
            return;
        }

        _targetHwnd = User32.GetForegroundWindow();
        _logger.LogInformation("Sesión iniciada. Target HWND: 0x{Hwnd:X}", _targetHwnd.ToInt64());

        lock (_bufferLock) _sessionBuffer.Clear();

        TransitionTo(DictationState.Recording);
        EmitPillMessage("Grabando…");

        _recordingTimeoutCts?.Dispose();
        _recordingTimeoutCts = new CancellationTokenSource();
        _ = StartRecordingAsync(_recordingTimeoutCts.Token);
    }

    private async Task StartRecordingAsync(CancellationToken ct)
    {
        try
        {
            await _audio.StartAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AudioCapture.StartAsync falló");
            EmitPillMessage($"⚠ Audio start falló: {ex.Message}");
            TransitionTo(DictationState.Idle);
            return;
        }

        // RN-8: auto-stop a los 10 min. Si HotkeyReleased dispara antes, cancelamos.
        try
        {
            await Task.Delay(MaxRecordingDuration, ct).ConfigureAwait(true);
            _logger.LogWarning("Recording excedió {Minutes} min — auto-stop (RN-8)", MaxRecordingDuration.TotalMinutes);
            await EndSessionAsync(reason: "Cortado a 10 min").ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancelado por HotkeyReleased — flujo normal.
        }
    }

    private async void OnHotkeyReleased(object? sender, EventArgs e)
    {
        if (_state != DictationState.Recording)
        {
            _logger.LogDebug("HotkeyReleased ignorado, estado actual: {State}", _state);
            return;
        }

        await EndSessionAsync(reason: null).ConfigureAwait(true);
    }

    private async Task EndSessionAsync(string? reason)
    {
        // Cancelar el timeout para que no dispare un segundo EndSession.
        _recordingTimeoutCts?.Cancel();

        try
        {
            await _audio.StopAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AudioCapture.StopAsync falló");
        }

        short[] snapshot;
        lock (_bufferLock) snapshot = _sessionBuffer.ToArray();

        // CB-4: <500 ms = audio muy corto, no llamamos Whisper.
        if (snapshot.Length < MinSessionSamples)
        {
            _logger.LogInformation(
                "Sesión muy corta ({Samples} samples = {Ms} ms), no transcribimos (CB-4)",
                snapshot.Length, snapshot.Length * 1000 / SampleRateHz);
            EmitPillMessage(reason ?? "Audio muy corto");
            TransitionTo(DictationState.Idle);
            EmitIdlePrompt();
            return;
        }

        TransitionTo(DictationState.Transcribing);
        EmitPillMessage("Transcribiendo…");

        var sw = Stopwatch.StartNew();
        string transcribedText;
        try
        {
            var wav = WavWriter.WriteWavFromPcm16(snapshot, SampleRateHz, channels: 1);
            transcribedText = await _transcription.TranscribeAsync(wav, CancellationToken.None).ConfigureAwait(true);
            _logger.LogInformation(
                "Whisper OK en {DurationMs} ms ({SampleMs} ms de audio): {Text}",
                sw.ElapsedMilliseconds, snapshot.Length * 1000 / SampleRateHz, transcribedText);
        }
        catch (TranscriptionException ex)
        {
            _logger.LogError(ex, "Transcripción falló");
            EmitPillMessage($"⚠ {ex.Message}");
            TransitionTo(DictationState.Idle);
            EmitIdlePrompt();
            return;
        }

        // CB-8: Whisper devolvió vacío/whitespace.
        if (string.IsNullOrWhiteSpace(transcribedText))
        {
            EmitPillMessage("No detectamos audio");
            TransitionTo(DictationState.Idle);
            EmitIdlePrompt();
            return;
        }

        TranscriptionCompleted?.Invoke(this, transcribedText);

        TransitionTo(DictationState.Inserting);
        EmitPillMessage("Pegando…");

        InsertionResult insertResult;
        try
        {
            insertResult = await _insertion.InsertIntoForegroundAsync(transcribedText, _targetHwnd).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Insertion lanzó excepción inesperada");
            insertResult = InsertionResult.Failed;
        }

        if (insertResult == InsertionResult.Pasted)
        {
            EmitPillMessage("✓ Pegado");
            TransitionTo(DictationState.Idle);
            EmitIdlePrompt();
            return;
        }

        // Paste falló → mostrar FloatingResultWindow (US-2.5).
        TransitionTo(DictationState.ShowingFloatingResult);
        EmitPillMessage(insertResult == InsertionResult.TargetGone
            ? "Ventana destino se cerró — texto en ventana flotante"
            : "No se pudo pegar — texto en ventana flotante");

        try
        {
            _floatingPresenter.Show(transcribedText, insertResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FloatingResultPresenter.Show lanzó excepción");
        }

        // Una vez mostrada la floating window, volvemos a Idle. La ventana en sí
        // tiene su propio lifecycle y se cierra cuando el usuario la cierra.
        TransitionTo(DictationState.Idle);
        EmitIdlePrompt();
    }

    private void OnSamplesAvailable(object? sender, short[] samples)
    {
        if (_state != DictationState.Recording) return;
        lock (_bufferLock) _sessionBuffer.AddRange(samples);
    }

    private void TransitionTo(DictationState next)
    {
        if (_state == next) return;
        _logger.LogDebug("State {From} → {To}", _state, next);
        _state = next;
        StateChanged?.Invoke(this, next);
    }

    private void EmitPillMessage(string message)
    {
        PillMessageChanged?.Invoke(this, message);
    }

    private void EmitIdlePrompt()
    {
        // Pequeña espera implícita: la pill mantiene el último mensaje hasta que el
        // próximo estado se dispara. No hace falta un timer acá — la sub-task #6
        // (DictationPillWindow) maneja el fade out y vuelta al estado calmo.
    }

    private void EnsureNotDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DictationOrchestrator));
    }
}
