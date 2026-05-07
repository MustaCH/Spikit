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
public sealed class DictationOrchestrator : IDisposable, IDictationDemoMode
{
    private const int SampleRateHz = 16_000;
    private const int MinSessionSamples = SampleRateHz / 2; // 500 ms — CB-4
    private static readonly TimeSpan MaxRecordingDuration = TimeSpan.FromMinutes(10); // RN-8
    private static readonly TimeSpan DemoFlashDuration = TimeSpan.FromMilliseconds(600);

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
    private HotkeyMode _mode = HotkeyMode.PushToTalk;
    private IntPtr _targetHwnd;
    private CancellationTokenSource? _recordingTimeoutCts;
    // Token vinculado a la sesión completa (Recording → Transcribing). Lo cancela
    // CancelSessionAsync (Esc cancel global de Q-7). Distinto del recordingTimeoutCts
    // — ese se cancela al final de la grabación normal y abortaría el TranscribeAsync
    // si lo compartiéramos.
    private CancellationTokenSource? _sessionCts;
    private bool _isDemoMode;
    // True solo durante el flash visual de RunDemoFlashAsync. Lo consulta
    // UpdateCancelHotkeyRegistration para no registrar Esc-cancel global durante el demo
    // (no hay sesión real que cancelar; el HotkeySectionView atrapa Esc localmente vía
    // PreviewKeyDown para cerrar el toast).
    private bool _demoFlashing;
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

    // Modo del hotkey (PTT default V1 / Toggle). Se lee al recibir HotkeyPressed/Released
    // para decidir el comportamiento. Mutado vía SetMode desde HotkeyConfigWriter cuando
    // el usuario cambia el modo en onboarding (EP-3.6) o Settings (EP-4 futuro).
    public HotkeyMode Mode => _mode;

    public void SetMode(HotkeyMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        _logger.LogInformation("Dictation mode → {Mode}", mode);
    }

    // ============ Demo mode (EP-4.4) ============

    public bool IsDemoMode => _isDemoMode;

    public event EventHandler? DemoHotkeyDetected;

    // Activa el modo demo. Mientras esté activo, el próximo HotkeyPressed no inicia sesión
    // real — solo emite DemoHotkeyDetected y simula el flash visual de la pill. El flag se
    // auto-desactiva al completar el flash. EndDemoMode se llama solo si el usuario cancela
    // antes de apretar (Esc en la sección Settings → Hotkey).
    public void BeginDemoMode()
    {
        EnsureNotDisposed();
        if (_isDemoMode) return;
        _isDemoMode = true;
        _logger.LogInformation("Dictation orchestrator: modo demo activo");
    }

    public void EndDemoMode()
    {
        if (!_isDemoMode) return;
        _isDemoMode = false;
        _logger.LogInformation("Dictation orchestrator: modo demo desactivado");
    }

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

        // Subscripción a los eventos del hotkey + audio. EL REGISTRO DEL HOTKEY NO SE
        // HACE ACÁ — desde EP-3.6 lo dispara el bootstrap (Program.cs) leyendo settings,
        // o el HotkeyConfigWriter cuando el usuario cambia la combinación.
        _hotkey.HotkeyPressed += OnHotkeyPressed;
        _hotkey.HotkeyReleased += OnHotkeyReleased;
        _hotkey.CancelHotkeyPressed += OnCancelHotkeyPressed;
        _audio.SamplesAvailable += OnSamplesAvailable;
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;

        _hotkey.HotkeyPressed -= OnHotkeyPressed;
        _hotkey.HotkeyReleased -= OnHotkeyReleased;
        _hotkey.CancelHotkeyPressed -= OnCancelHotkeyPressed;
        _audio.SamplesAvailable -= OnSamplesAvailable;

        // El hotkey lo libera el dueño del registro: HotkeyService.Dispose en App.OnExit
        // ya hace cleanup. No tocamos Unregister acá para no pisar el state si la app
        // sigue viva pero el orchestrator fue Stop()-eado por algún motivo.
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _recordingTimeoutCts?.Cancel();
        _recordingTimeoutCts?.Dispose();
        _sessionCts?.Cancel();
        _sessionCts?.Dispose();
    }

    private async void OnHotkeyPressed(object? sender, EventArgs e)
    {
        // Modo demo (EP-4.4): cortocircuita ANTES de cualquier otra cosa. No iniciamos
        // AudioCapture/Whisper, solo emitimos el detected event + simulamos el flash de la
        // pill (Recording → 600 ms → Idle). La pill ya escucha StateChanged, por lo que aparece
        // en visual mode "Initializing" (3 dots gris) — feedback razonable de "el OS detectó
        // tu combinación". El audio nunca arranca, OnSamplesAvailable no recibe nada.
        if (_isDemoMode)
        {
            _isDemoMode = false; // auto-desactiva: un press = un flash, no se queda enganchado.
            _logger.LogDebug("Demo mode: HotkeyPressed detectado, flash simulado (sin AudioCapture)");
            DemoHotkeyDetected?.Invoke(this, EventArgs.Empty);
            _ = RunDemoFlashAsync();
            return;
        }

        // Toggle (V1 nuevo, EP-3.6): segundo press mientras estamos Recording = stop.
        // El primer press en estado Idle cae al flujo normal abajo.
        if (_mode == HotkeyMode.Toggle && _state == DictationState.Recording)
        {
            _logger.LogDebug("Toggle: segundo press en Recording → end session");
            await EndSessionAsync(reason: null).ConfigureAwait(true);
            return;
        }

        // RN-6: en cualquier estado activo distinto de Recording (Transcribing/Inserting/
        // ShowingFloatingResult), ignoramos nuevos press. Cancelación queda para EP-5.
        if (_state != DictationState.Idle)
        {
            _logger.LogDebug("HotkeyPressed ignorado, estado actual: {State} (RN-6)", _state);
            return;
        }

        _targetHwnd = User32.GetForegroundWindow();
        _logger.LogInformation("Sesión iniciada. Target HWND: 0x{Hwnd:X}", _targetHwnd.ToInt64());

        lock (_bufferLock) _sessionBuffer.Clear();

        // _sessionCts es para cancel via Esc (Q-7): vive desde el press hasta que la sesión
        // termina (normal o cancelada). El TranscribeAsync recibe su token; CancelSessionAsync
        // lo cancela para abortar tanto el recording como un transcribe en vuelo.
        _sessionCts?.Dispose();
        _sessionCts = new CancellationTokenSource();

        TransitionTo(DictationState.Recording);
        EmitPillMessage("Grabando…");

        _recordingTimeoutCts?.Dispose();
        _recordingTimeoutCts = new CancellationTokenSource();
        _ = StartRecordingAsync(_recordingTimeoutCts.Token);
    }

    private async void OnCancelHotkeyPressed(object? sender, EventArgs e)
    {
        // Q-7: Esc cancela en Recording y Transcribing. NO en Inserting (corre invisible
        // en background tras D-9 y cancelarlo dejaría un Ctrl+V parcial). El cancel hotkey
        // solo está registrado durante esos estados, así que en teoría no llegaríamos acá
        // en otros estados — el guard es defensivo.
        if (_state != DictationState.Recording && _state != DictationState.Transcribing)
        {
            _logger.LogDebug("Cancel hotkey ignorado, estado actual: {State}", _state);
            return;
        }

        _logger.LogInformation("Cancel hotkey (Esc) recibido en estado {State} — cancelando sesión", _state);
        await CancelSessionAsync().ConfigureAwait(true);
    }

    private async Task CancelSessionAsync()
    {
        // Cancelamos los dos tokens: recordingTimeoutCts aborta el Task.Delay(10 min) si
        // estamos en Recording, sessionCts aborta el TranscribeAsync si estamos en Transcribing.
        _recordingTimeoutCts?.Cancel();
        _sessionCts?.Cancel();

        try
        {
            await _audio.StopAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AudioCapture.StopAsync falló durante cancel");
        }

        // RN-1: descartar el audio capturado. La sesión se trunca silenciosa, sin floating
        // result ni toast — el usuario apretó Esc explícitamente.
        lock (_bufferLock) _sessionBuffer.Clear();

        // El TransitionTo a Idle desregistra el cancel hotkey vía UpdateCancelHotkeyRegistration
        // y la pill se va con leaving (StateChanged dispara el VM).
        TransitionTo(DictationState.Idle);
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
        // Toggle (V1 nuevo, EP-3.6): el release no termina la sesión; el segundo press lo hace.
        if (_mode == HotkeyMode.Toggle)
        {
            _logger.LogDebug("Toggle: HotkeyReleased ignorado (la sesión termina con el segundo press)");
            return;
        }

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
        var transcribeToken = _sessionCts?.Token ?? CancellationToken.None;
        try
        {
            var wav = WavWriter.WriteWavFromPcm16(snapshot, SampleRateHz, channels: 1);
            transcribedText = await _transcription.TranscribeAsync(wav, transcribeToken).ConfigureAwait(true);
            _logger.LogInformation(
                "Whisper OK en {DurationMs} ms ({SampleMs} ms de audio): {Text}",
                sw.ElapsedMilliseconds, snapshot.Length * 1000 / SampleRateHz, transcribedText);
        }
        catch (OperationCanceledException)
        {
            // Q-7: el usuario apretó Esc durante Transcribing. CancelSessionAsync ya hizo
            // todo el cleanup (TransitionTo Idle, descartar buffer); acá solo evitamos
            // continuar al insertion path. La pill ya está en leaving.
            _logger.LogInformation("Transcripción cancelada por Esc (Q-7)");
            return;
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

    private async Task RunDemoFlashAsync()
    {
        // Solo dispara la pill si estamos en Idle. Si por algún motivo el orchestrator
        // tiene otra sesión activa (raro: implica que el usuario abrió Settings durante
        // un dictado), no la interrumpimos.
        if (_state != DictationState.Idle) return;

        _demoFlashing = true;
        try
        {
            TransitionTo(DictationState.Recording);
            try
            {
                await Task.Delay(DemoFlashDuration).ConfigureAwait(true);
            }
            catch (TaskCanceledException) { /* ignore */ }
            // Si en el ínterin arrancó una sesión real (imposible bajo el flujo demo, pero
            // defensivo), no pisamos su estado.
            if (_state == DictationState.Recording)
            {
                TransitionTo(DictationState.Idle);
            }
        }
        finally
        {
            _demoFlashing = false;
        }
    }

    private void TransitionTo(DictationState next)
    {
        if (_state == next) return;
        _logger.LogDebug("State {From} → {To}", _state, next);
        _state = next;
        UpdateCancelHotkeyRegistration(next);
        StateChanged?.Invoke(this, next);
    }

    // Cancel hotkey (Esc) solo registrado durante estados cancelables. Q-7 exige Recording
    // y Transcribing (Initializing es sub-estado del audio cubierto por Recording del
    // orchestrator). Inserting NO — es muy corto y cancelarlo dejaría Ctrl+V parcial.
    // Idempotente: el HotkeyService no falla si registramos dos veces seguidas o
    // desregistramos sin haber registrado.
    private void UpdateCancelHotkeyRegistration(DictationState state)
    {
        // Durante el demo flash no registramos cancel global — no hay sesión real que abortar
        // y la sección Settings → Hotkey ya atrapa Esc localmente via PreviewKeyDown.
        if (_demoFlashing) return;

        var cancelable = state is DictationState.Recording or DictationState.Transcribing;
        if (cancelable)
        {
            _hotkey.RegisterCancelHotkey();
        }
        else
        {
            _hotkey.UnregisterCancelHotkey();
        }
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
