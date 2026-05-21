using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spikit.Models;
using Spikit.Services.Audio;
using Spikit.Services.Auth;
using Spikit.Services.History;
using Spikit.Services.Hotkey;
using Spikit.Services.Insertion;
using Spikit.Services.Orchestration;
using Spikit.Services.Settings;
using Spikit.Services.Toast;
using Spikit.Services.Transcription;
using Spikit.ViewModels.Settings;

namespace Spikit.Tests.Services.Orchestration;

public class DictationOrchestratorTests
{
    private readonly Mock<IHotkeyService> _hotkey = new();
    private readonly Mock<IAudioCaptureService> _audio = new();
    private readonly Mock<ITranscriptionService> _transcription = new();
    private readonly Mock<ITextInsertionService> _insertion = new();
    private readonly Mock<IFloatingResultPresenter> _presenter = new();
    private readonly Mock<IHistoryStore> _historyStore = new();
    private readonly Mock<IToastService> _toast = new();
    private readonly FakeSettingsService _settings = new();
    private readonly FakeProcessResolver _processResolver = new();

    private DictationOrchestrator BuildAndStart()
    {
        var orchestrator = new DictationOrchestrator(
            _hotkey.Object, _audio.Object, _transcription.Object, _insertion.Object,
            _presenter.Object, _historyStore.Object, _settings, _processResolver,
            _toast.Object,
            NullLogger<DictationOrchestrator>.Instance);
        orchestrator.Start();
        return orchestrator;
    }

    private DictationOrchestrator BuildAndStartWithAuth(
        IAuthService auth,
        ISettingsWindowPresenter? settingsPresenter = null)
    {
        var orchestrator = new DictationOrchestrator(
            _hotkey.Object, _audio.Object, _transcription.Object, _insertion.Object,
            _presenter.Object, _historyStore.Object, _settings, _processResolver,
            _toast.Object, auth, settingsPresenter,
            NullLogger<DictationOrchestrator>.Instance);
        orchestrator.Start();
        return orchestrator;
    }

    private static short[] SamplesOfDuration(int milliseconds)
    {
        var count = 16_000 * milliseconds / 1000;
        var samples = new short[count];
        // Llenamos con un patrón no-cero para simular audio real.
        for (var i = 0; i < count; i++) samples[i] = (short)(i % 1000);
        return samples;
    }

    private void RaiseHotkeyPressed() =>
        _hotkey.Raise(h => h.HotkeyPressed += null, this, EventArgs.Empty);

    private void RaiseHotkeyReleased() =>
        _hotkey.Raise(h => h.HotkeyReleased += null, this, EventArgs.Empty);

    private void RaiseCancelHotkeyPressed() =>
        _hotkey.Raise(h => h.CancelHotkeyPressed += null, this, EventArgs.Empty);

    private void RaiseSamples(short[] samples) =>
        _audio.Raise(a => a.SamplesAvailable += null, this, samples);

    [Fact]
    public void Initial_state_is_Idle()
    {
        var orchestrator = BuildAndStart();
        Assert.Equal(DictationState.Idle, orchestrator.State);
    }

    [Fact]
    public void Start_does_not_register_hotkey_directly()
    {
        // Desde EP-3.6 el Register lo dispara el bootstrap (App.xaml.cs leyendo settings.json)
        // o el HotkeyConfigWriter cuando el usuario cambia la combinación. El orchestrator
        // solo se suscribe a HotkeyPressed/Released.
        BuildAndStart();
        _hotkey.Verify(h => h.Register(It.IsAny<HotkeyDefinition>()), Times.Never);
    }

    [Fact]
    public void HotkeyPressed_transitions_to_Recording_and_starts_audio()
    {
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();

        Assert.Equal(DictationState.Recording, orchestrator.State);
        _audio.Verify(a => a.StartAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void HotkeyPressed_while_already_active_is_ignored()
    {
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();
        RaiseHotkeyPressed(); // segundo press mientras Recording

        _audio.Verify(a => a.StartAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(DictationState.Recording, orchestrator.State);
    }

    [Fact]
    public async Task Short_session_under_500ms_skips_transcription_and_returns_to_Idle()
    {
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _audio.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();
        RaiseSamples(SamplesOfDuration(milliseconds: 200)); // 200ms < 500ms threshold
        RaiseHotkeyReleased();
        await Task.Yield();

        _transcription.Verify(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
        _insertion.Verify(i => i.InsertIntoForegroundAsync(It.IsAny<string>(), It.IsAny<IntPtr>()), Times.Never);
        Assert.Equal(DictationState.Idle, orchestrator.State);
    }

    [Fact]
    public async Task Empty_whisper_response_skips_insertion_and_returns_to_Idle()
    {
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _audio.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);
        _transcription.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("   "); // whitespace-only

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();
        RaiseSamples(SamplesOfDuration(milliseconds: 1500));
        RaiseHotkeyReleased();
        await WaitForState(orchestrator, DictationState.Idle);

        _transcription.Verify(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Once);
        _insertion.Verify(i => i.InsertIntoForegroundAsync(It.IsAny<string>(), It.IsAny<IntPtr>()), Times.Never);
        Assert.Equal(DictationState.Idle, orchestrator.State);
    }

    [Fact]
    public async Task Successful_session_transcribes_inserts_and_returns_to_Idle()
    {
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _audio.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);
        _transcription.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("hola mundo");
        _insertion.Setup(i => i.InsertIntoForegroundAsync("hola mundo", It.IsAny<IntPtr>()))
            .ReturnsAsync(InsertionResult.Pasted);

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();
        RaiseSamples(SamplesOfDuration(milliseconds: 1500));
        RaiseHotkeyReleased();
        await WaitForState(orchestrator, DictationState.Idle);

        _insertion.Verify(i => i.InsertIntoForegroundAsync("hola mundo", It.IsAny<IntPtr>()), Times.Once);
        _presenter.Verify(p => p.Show(It.IsAny<ResultErrorReason>(), It.IsAny<string>(), It.IsAny<IntPtr>()), Times.Never);
    }

    [Theory]
    [InlineData(InsertionResult.TargetGone)]
    [InlineData(InsertionResult.Failed)]
    public async Task Insertion_failure_shows_floating_result_and_returns_to_Idle(InsertionResult failureCode)
    {
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _audio.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);
        _transcription.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("texto a pegar");
        _insertion.Setup(i => i.InsertIntoForegroundAsync(It.IsAny<string>(), It.IsAny<IntPtr>()))
            .ReturnsAsync(failureCode);

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();
        RaiseSamples(SamplesOfDuration(milliseconds: 1500));
        RaiseHotkeyReleased();
        await WaitForState(orchestrator, DictationState.Idle);

        _presenter.Verify(p => p.Show(ResultErrorReason.PasteFailed, "texto a pegar", It.IsAny<IntPtr>()), Times.Once);
    }

    [Fact]
    public async Task Transcription_exception_returns_to_Idle_without_insertion()
    {
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _audio.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);
        _transcription.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TranscriptionException("API error"));

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();
        RaiseSamples(SamplesOfDuration(milliseconds: 1500));
        RaiseHotkeyReleased();
        await WaitForState(orchestrator, DictationState.Idle);

        _insertion.Verify(i => i.InsertIntoForegroundAsync(It.IsAny<string>(), It.IsAny<IntPtr>()), Times.Never);
        Assert.Equal(DictationState.Idle, orchestrator.State);
    }

    [Fact]
    public void HotkeyReleased_when_idle_is_ignored()
    {
        var orchestrator = BuildAndStart();
        RaiseHotkeyReleased();

        _audio.Verify(a => a.StopAsync(), Times.Never);
        Assert.Equal(DictationState.Idle, orchestrator.State);
    }

    [Fact]
    public void StateChanged_event_fires_on_transitions()
    {
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var orchestrator = BuildAndStart();
        var transitions = new List<DictationState>();
        orchestrator.StateChanged += (_, s) => transitions.Add(s);

        RaiseHotkeyPressed();

        Assert.Contains(DictationState.Recording, transitions);
    }

    [Fact]
    public void Dispose_unregisters_handlers_and_disposes()
    {
        _hotkey.Setup(h => h.Unregister());

        var orchestrator = BuildAndStart();
        orchestrator.Dispose();

        // Después del dispose, raise events no debería disparar transitions.
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        RaiseHotkeyPressed();

        _audio.Verify(a => a.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ===== Modo Toggle (EP-3.6) =====

    [Fact]
    public async Task Toggle_mode_release_does_not_end_session()
    {
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var orchestrator = BuildAndStart();
        orchestrator.SetMode(HotkeyMode.Toggle);

        RaiseHotkeyPressed();
        Assert.Equal(DictationState.Recording, orchestrator.State);

        // En Toggle el release NO termina la sesión — el segundo press lo hace.
        RaiseHotkeyReleased();
        await Task.Yield();

        _audio.Verify(a => a.StopAsync(), Times.Never);
        Assert.Equal(DictationState.Recording, orchestrator.State);
    }

    [Fact]
    public async Task Toggle_mode_second_press_ends_session_and_transcribes()
    {
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _audio.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);
        _transcription.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("toggle test");
        _insertion.Setup(i => i.InsertIntoForegroundAsync("toggle test", It.IsAny<IntPtr>()))
            .ReturnsAsync(InsertionResult.Pasted);

        var orchestrator = BuildAndStart();
        orchestrator.SetMode(HotkeyMode.Toggle);

        RaiseHotkeyPressed(); // primer press → start
        RaiseSamples(SamplesOfDuration(milliseconds: 1500));
        RaiseHotkeyReleased(); // ignorado en Toggle
        Assert.Equal(DictationState.Recording, orchestrator.State);

        RaiseHotkeyPressed(); // segundo press → end + transcribe + insert
        await WaitForState(orchestrator, DictationState.Idle);

        _transcription.Verify(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Once);
        _insertion.Verify(i => i.InsertIntoForegroundAsync("toggle test", It.IsAny<IntPtr>()), Times.Once);
    }

    // ===== Esc cancel global (Q-7) =====

    [Fact]
    public void Recording_state_registers_cancel_hotkey()
    {
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        BuildAndStart();
        RaiseHotkeyPressed();

        _hotkey.Verify(h => h.RegisterCancelHotkey(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Cancel_hotkey_in_Recording_stops_audio_and_returns_to_Idle()
    {
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _audio.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();
        Assert.Equal(DictationState.Recording, orchestrator.State);

        RaiseCancelHotkeyPressed();
        await WaitForState(orchestrator, DictationState.Idle);

        _audio.Verify(a => a.StopAsync(), Times.AtLeastOnce);
        // Transcription NO se invoca: el buffer se descarta antes de tocar Whisper.
        _transcription.Verify(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cancel_hotkey_in_Recording_unregisters_cancel_hotkey_after_Idle()
    {
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _audio.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();
        RaiseCancelHotkeyPressed();
        await WaitForState(orchestrator, DictationState.Idle);

        _hotkey.Verify(h => h.UnregisterCancelHotkey(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Cancel_hotkey_during_Transcribing_aborts_transcription()
    {
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _audio.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);

        // Whisper bloquea hasta que el token se cancele (simula request en vuelo).
        var transcribeStarted = new TaskCompletionSource();
        _transcription.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Returns(async (byte[] _, CancellationToken ct) =>
            {
                transcribeStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return string.Empty;
            });

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();
        RaiseSamples(SamplesOfDuration(milliseconds: 1500));
        RaiseHotkeyReleased();

        await transcribeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(DictationState.Transcribing, orchestrator.State);

        RaiseCancelHotkeyPressed();
        await WaitForState(orchestrator, DictationState.Idle);

        // Insertion NO se llama (el TranscribeAsync nunca devolvió texto, el cancel atrapado
        // en el orchestrator devuelve antes del path de inserción).
        _insertion.Verify(i => i.InsertIntoForegroundAsync(It.IsAny<string>(), It.IsAny<IntPtr>()), Times.Never);
    }

    [Fact]
    public void Cancel_hotkey_when_Idle_is_no_op()
    {
        var orchestrator = BuildAndStart();

        RaiseCancelHotkeyPressed();

        Assert.Equal(DictationState.Idle, orchestrator.State);
        _audio.Verify(a => a.StopAsync(), Times.Never);
    }

    // ===== EP-11.7 — CancelActiveSessionAsync (consumido por ISessionLifecycleService) =====

    [Fact]
    public async Task CancelActiveSessionAsync_when_Idle_is_no_op()
    {
        var orchestrator = BuildAndStart();

        await orchestrator.CancelActiveSessionAsync();

        Assert.Equal(DictationState.Idle, orchestrator.State);
        _audio.Verify(a => a.StopAsync(), Times.Never);
    }

    [Fact]
    public async Task CancelActiveSessionAsync_during_Recording_returns_to_Idle_and_discards_audio()
    {
        // Mismo path interno que el cancel-via-Esc — para el lifecycle service (logout
        // mientras hay sesión activa) el comportamiento debe ser idéntico.
        var startAsyncBlocked = new TaskCompletionSource();
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                startAsyncBlocked.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            });
        _audio.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();

        await startAsyncBlocked.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(DictationState.Recording, orchestrator.State);

        await orchestrator.CancelActiveSessionAsync();
        await WaitForState(orchestrator, DictationState.Idle);

        _audio.Verify(a => a.StopAsync(), Times.AtLeastOnce);
        _transcription.Verify(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ===== EP-5.2 — Esc cancela en initializing / recording / transcribing (no en inserting) =====

    [Fact]
    public async Task Cancel_during_audio_cold_start_aborts_and_returns_to_Idle()
    {
        // El sub-estado "Initializing" del audio (cold-start ~600ms p50, hasta ~1.5s p99)
        // se mapea a DictationState.Recording desde el punto de vista del orchestrator —
        // la transición ocurre antes de que audio.StartAsync retorne. El cancel hotkey ya
        // está registrado en ese instante, por lo que Esc debe abortar limpiamente.
        var startAsyncBlocked = new TaskCompletionSource();
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                startAsyncBlocked.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            });
        _audio.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();

        await startAsyncBlocked.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(DictationState.Recording, orchestrator.State);
        _hotkey.Verify(h => h.RegisterCancelHotkey(), Times.AtLeastOnce);

        RaiseCancelHotkeyPressed();
        await WaitForState(orchestrator, DictationState.Idle);

        // Audio se cierra incluso si nunca llegó a emitir samples.
        _audio.Verify(a => a.StopAsync(), Times.AtLeastOnce);
        _transcription.Verify(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cancel_hotkey_is_unregistered_while_state_is_Inserting()
    {
        // D-3 / Q-7: durante Inserting el cancel hotkey NO debe estar registrado —
        // un Esc atrapado por nuestra app a mitad de un Ctrl+V dejaría texto parcial
        // pegado y el clipboard sin restaurar. La transición a Inserting llama
        // UnregisterCancelHotkey, devolviendo Esc al uso normal del usuario.
        var cancelRegistered = false;
        _hotkey.Setup(h => h.RegisterCancelHotkey()).Callback(() => cancelRegistered = true);
        _hotkey.Setup(h => h.UnregisterCancelHotkey()).Callback(() => cancelRegistered = false);

        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _audio.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);
        _transcription.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("texto");

        bool? cancelStateAtInserting = null;
        _insertion.Setup(i => i.InsertIntoForegroundAsync(It.IsAny<string>(), It.IsAny<IntPtr>()))
            .Returns(async () =>
            {
                // Capturamos el estado del cancel hotkey JUSTO al entrar en Inserting.
                cancelStateAtInserting = cancelRegistered;
                await Task.Yield();
                return InsertionResult.Pasted;
            });

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();
        RaiseSamples(SamplesOfDuration(milliseconds: 1500));
        RaiseHotkeyReleased();
        await WaitForState(orchestrator, DictationState.Idle);

        Assert.False(cancelStateAtInserting,
            "El cancel hotkey debería estar desregistrado al entrar en Inserting (D-3 / Q-7)");
    }

    // ===== EP-5.1 — Cancelación con re-press del hotkey =====

    [Fact]
    public async Task Re_press_in_transcribing_cancels_and_returns_to_idle_without_insertion()
    {
        // EP-5.1 / CB-2: simétrico con Esc cancel — el usuario aprieta su hotkey otra vez
        // mientras la transcripción está en vuelo y la sesión se aborta sin insertar nada.
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _audio.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);

        var transcribeStarted = new TaskCompletionSource();
        _transcription.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .Returns(async (byte[] _, CancellationToken ct) =>
            {
                transcribeStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return string.Empty;
            });

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();
        RaiseSamples(SamplesOfDuration(milliseconds: 1500));
        RaiseHotkeyReleased();

        await transcribeStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(DictationState.Transcribing, orchestrator.State);

        RaiseHotkeyPressed(); // re-press en Transcribing → cancela
        await WaitForState(orchestrator, DictationState.Idle);

        _insertion.Verify(i => i.InsertIntoForegroundAsync(It.IsAny<string>(), It.IsAny<IntPtr>()), Times.Never);
        // El audio se descarta (RN-1) — StopAsync se invoca durante la cancelación.
        _audio.Verify(a => a.StopAsync(), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Re_press_in_ptt_recording_is_ignored_and_session_continues()
    {
        // CB-1: en PTT, un segundo press en Recording NO cancela ni inicia sesión nueva —
        // se ignora. El release del original sigue siendo lo que termina la sesión.
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _audio.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);
        _transcription.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ptt continúa");
        _insertion.Setup(i => i.InsertIntoForegroundAsync(It.IsAny<string>(), It.IsAny<IntPtr>()))
            .ReturnsAsync(InsertionResult.Pasted);

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();
        RaiseHotkeyPressed(); // segundo press accidental en Recording — debe ignorarse
        Assert.Equal(DictationState.Recording, orchestrator.State);

        // El release del original cierra la sesión normalmente y va a Whisper.
        RaiseSamples(SamplesOfDuration(milliseconds: 1500));
        RaiseHotkeyReleased();
        await WaitForState(orchestrator, DictationState.Idle);

        _audio.Verify(a => a.StartAsync(It.IsAny<CancellationToken>()), Times.Once);
        _transcription.Verify(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Re_press_in_inserting_or_floating_result_is_ignored()
    {
        // RN-6: en Inserting (estado invisible <1s) y ShowingFloatingResult el press
        // tampoco cancela — no hay nada útil que abortar y un Ctrl+V parcial sería peor.
        // Acá disparamos el flujo de paste fallido para terminar en ShowingFloatingResult
        // brevemente y verificar que el press extra durante esa transición no rompe el cierre.
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _audio.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);
        _transcription.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("texto");

        var insertCalled = new TaskCompletionSource();
        _insertion.Setup(i => i.InsertIntoForegroundAsync(It.IsAny<string>(), It.IsAny<IntPtr>()))
            .Returns(async () =>
            {
                insertCalled.TrySetResult();
                await Task.Delay(50);
                return InsertionResult.Pasted;
            });

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();
        RaiseSamples(SamplesOfDuration(milliseconds: 1500));
        RaiseHotkeyReleased();

        await insertCalled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        // Estado puede ser Inserting durante este press — el handler debe ignorarlo.
        RaiseHotkeyPressed();

        await WaitForState(orchestrator, DictationState.Idle);

        // Solo una sesión real se completó; el re-press no inició otra.
        _audio.Verify(a => a.StartAsync(It.IsAny<CancellationToken>()), Times.Once);
        _insertion.Verify(i => i.InsertIntoForegroundAsync(It.IsAny<string>(), It.IsAny<IntPtr>()), Times.Once);
    }

    // ===== EP-5.3 — Toasts en CB-4 / CB-5 / CB-8 =====

    [Fact]
    public async Task CB4_short_session_shows_info_toast()
    {
        // CB-4: <500ms = audio muy corto. Toast info gris con acción "Abrir Settings → Audio".
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _audio.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();
        RaiseSamples(SamplesOfDuration(milliseconds: 200));
        RaiseHotkeyReleased();
        await WaitForState(orchestrator, DictationState.Idle);

        _toast.Verify(t => t.Show(
            ToastSeverity.Info,
            It.Is<string>(s => s.Contains("No detectamos audio")),
            It.IsAny<string?>(),
            It.Is<ToastAction?>(a => a != null && a.Label.Contains("Settings")),
            It.IsAny<TimeSpan?>(),
            It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task CB8_empty_whisper_response_shows_floating_result_v6()
    {
        // CB-8: Whisper devuelve texto vacío. EP-6.5 migró de toast a FloatingResult V6
        // (ResultErrorReason.EmptyResult) — alineado con resto de errores del provider.
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _audio.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);
        _transcription.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("   ");

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();
        RaiseSamples(SamplesOfDuration(milliseconds: 1500));
        RaiseHotkeyReleased();
        await WaitForState(orchestrator, DictationState.Idle);

        _presenter.Verify(p => p.Show(
            ResultErrorReason.EmptyResult,
            It.IsAny<string>(),
            It.IsAny<IntPtr>()), Times.Once);
    }

    [Fact]
    public async Task Successful_session_does_not_show_toast()
    {
        // FLOW 5 filosofía: el éxito no genera toast — el texto en el editor ES el feedback.
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _audio.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);
        _transcription.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("hola");
        _insertion.Setup(i => i.InsertIntoForegroundAsync(It.IsAny<string>(), It.IsAny<IntPtr>()))
            .ReturnsAsync(InsertionResult.Pasted);

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();
        RaiseSamples(SamplesOfDuration(milliseconds: 1500));
        RaiseHotkeyReleased();
        await WaitForState(orchestrator, DictationState.Idle);

        _toast.Verify(t => t.Show(
            It.IsAny<ToastSeverity>(),
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<ToastAction?>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task Audio_start_failure_shows_error_toast()
    {
        // US-7.5 amplio: si StartAsync tira (mic no disponible, driver), toast rojo.
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("device gone"));

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();
        await WaitForState(orchestrator, DictationState.Idle);

        _toast.Verify(t => t.Show(
            ToastSeverity.Error,
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<ToastAction?>(),
            It.IsAny<TimeSpan?>(),
            It.IsAny<string?>()), Times.Once);
    }

    // ===== EP-4.10 — cableado del HistoryStore =====

    [Fact]
    public async Task Successful_session_appends_to_history_when_toggle_on()
    {
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _audio.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);
        _transcription.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("hola desde test");
        _insertion.Setup(i => i.InsertIntoForegroundAsync(It.IsAny<string>(), It.IsAny<IntPtr>()))
            .ReturnsAsync(InsertionResult.Pasted);

        _settings.Saved.Privacy = new PrivacySettings { HistoryEnabled = true };
        _processResolver.ResolvedName = "cursor.exe";

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();
        RaiseSamples(SamplesOfDuration(milliseconds: 1500));
        RaiseHotkeyReleased();
        await WaitForState(orchestrator, DictationState.Idle);

        _historyStore.Verify(h => h.Append(It.Is<HistoryEntry>(e =>
            e.Text == "hola desde test"
            && e.TargetProcessName == "cursor.exe"
            && e.DurationMs >= 1400 && e.DurationMs <= 1600)), Times.Once);
    }

    [Fact]
    public async Task Successful_session_does_not_append_when_toggle_off()
    {
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _audio.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);
        _transcription.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("hola");
        _insertion.Setup(i => i.InsertIntoForegroundAsync(It.IsAny<string>(), It.IsAny<IntPtr>()))
            .ReturnsAsync(InsertionResult.Pasted);

        _settings.Saved.Privacy = new PrivacySettings { HistoryEnabled = false };

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();
        RaiseSamples(SamplesOfDuration(milliseconds: 1500));
        RaiseHotkeyReleased();
        await WaitForState(orchestrator, DictationState.Idle);

        _historyStore.Verify(h => h.Append(It.IsAny<HistoryEntry>()), Times.Never);
    }

    [Fact]
    public async Task History_append_happens_even_when_paste_fails()
    {
        // Acceptance criteria EP-4.10: el Append corre post-Whisper-OK, INDEPENDIENTE
        // de si el paste posterior pegó o no. El usuario sigue teniendo el texto en
        // FloatingResult; el historial debe reflejarlo.
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _audio.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);
        _transcription.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("texto que no se pegó");
        _insertion.Setup(i => i.InsertIntoForegroundAsync(It.IsAny<string>(), It.IsAny<IntPtr>()))
            .ReturnsAsync(InsertionResult.Failed);

        _settings.Saved.Privacy = new PrivacySettings { HistoryEnabled = true };

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();
        RaiseSamples(SamplesOfDuration(milliseconds: 1500));
        RaiseHotkeyReleased();
        await WaitForState(orchestrator, DictationState.Idle);

        _historyStore.Verify(h => h.Append(It.IsAny<HistoryEntry>()), Times.Once);
    }

    [Fact]
    public async Task Empty_whisper_response_does_not_append_to_history()
    {
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _audio.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);
        _transcription.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("   ");

        _settings.Saved.Privacy = new PrivacySettings { HistoryEnabled = true };

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();
        RaiseSamples(SamplesOfDuration(milliseconds: 1500));
        RaiseHotkeyReleased();
        await WaitForState(orchestrator, DictationState.Idle);

        _historyStore.Verify(h => h.Append(It.IsAny<HistoryEntry>()), Times.Never);
    }

    [Fact]
    public async Task History_append_failure_does_not_break_dictation_flow()
    {
        // El historial es secundario: si Append tira (disco lleno, archivo locked), el
        // flujo principal del dictado debe completarse igual y la state machine volver a Idle.
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _audio.Setup(a => a.StopAsync()).Returns(Task.CompletedTask);
        _transcription.Setup(t => t.TranscribeAsync(It.IsAny<byte[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("hola");
        _insertion.Setup(i => i.InsertIntoForegroundAsync(It.IsAny<string>(), It.IsAny<IntPtr>()))
            .ReturnsAsync(InsertionResult.Pasted);

        _settings.Saved.Privacy = new PrivacySettings { HistoryEnabled = true };
        _historyStore.Setup(h => h.Append(It.IsAny<HistoryEntry>()))
            .Throws(new IOException("disco lleno"));

        var orchestrator = BuildAndStart();
        RaiseHotkeyPressed();
        RaiseSamples(SamplesOfDuration(milliseconds: 1500));
        RaiseHotkeyReleased();
        await WaitForState(orchestrator, DictationState.Idle);

        _insertion.Verify(i => i.InsertIntoForegroundAsync("hola", It.IsAny<IntPtr>()), Times.Once);
        Assert.Equal(DictationState.Idle, orchestrator.State);
    }

    private static async Task WaitForState(DictationOrchestrator orchestrator, DictationState target, int timeoutMs = 500)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (orchestrator.State != target && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.Equal(target, orchestrator.State);
    }

    // ===== EP-10.12 — Gate de tier=Expired bloquea recording =====

    [Fact]
    public void Hotkey_press_with_Expired_tier_does_NOT_start_recording()
    {
        var auth = new FakeAuthService
        {
            State = AuthSessionState.LoggedIn,
            CurrentEntitlement = new Entitlement(Tier.Expired, null, null, null, 0),
        };
        var presenter = new Mock<ISettingsWindowPresenter>();
        var orchestrator = BuildAndStartWithAuth(auth, presenter.Object);

        RaiseHotkeyPressed();

        _audio.Verify(a => a.StartAsync(It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(DictationState.Idle, orchestrator.State);
    }

    [Fact]
    public void Hotkey_press_with_Expired_tier_fires_LockedHotkeyPressed_event()
    {
        var auth = new FakeAuthService
        {
            State = AuthSessionState.LoggedIn,
            CurrentEntitlement = new Entitlement(Tier.Expired, null, null, null, 0),
        };
        var orchestrator = BuildAndStartWithAuth(auth);
        var fired = 0;
        orchestrator.LockedHotkeyPressed += (_, _) => fired++;

        RaiseHotkeyPressed();

        Assert.Equal(1, fired);
    }

    [Fact]
    public void Hotkey_press_with_Expired_tier_shows_warning_toast_with_action()
    {
        var auth = new FakeAuthService
        {
            State = AuthSessionState.LoggedIn,
            CurrentEntitlement = new Entitlement(Tier.Expired, null, null, null, 0),
        };
        var presenter = new Mock<ISettingsWindowPresenter>();
        var orchestrator = BuildAndStartWithAuth(auth, presenter.Object);

        RaiseHotkeyPressed();

        _toast.Verify(t => t.Show(
            ToastSeverity.Warning,
            It.Is<string>(s => s.Contains("expiró", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<string?>(),
            It.Is<ToastAction>(a => a != null && a.Label.Contains("Pro", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<TimeSpan?>(),
            It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public void Toast_action_opens_Settings_to_Plan_section()
    {
        var auth = new FakeAuthService
        {
            State = AuthSessionState.LoggedIn,
            CurrentEntitlement = new Entitlement(Tier.Expired, null, null, null, 0),
        };
        var presenter = new Mock<ISettingsWindowPresenter>();
        ToastAction? capturedAction = null;
        _toast.Setup(t => t.Show(
                It.IsAny<ToastSeverity>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<ToastAction>(), It.IsAny<TimeSpan?>(), It.IsAny<string?>()))
            .Callback<ToastSeverity, string, string?, ToastAction?, TimeSpan?, string?>(
                (_, _, _, action, _, _) => capturedAction = action);

        var orchestrator = BuildAndStartWithAuth(auth, presenter.Object);
        RaiseHotkeyPressed();

        Assert.NotNull(capturedAction);
        capturedAction!.OnInvoke();

        presenter.Verify(p => p.Open(SettingsSection.Plan), Times.Once);
    }

    [Theory]
    [InlineData(Tier.Trial)]
    [InlineData(Tier.Pro)]
    [InlineData(Tier.Byok)]
    public void Hotkey_press_with_active_tier_starts_recording_normally(Tier activeTier)
    {
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var auth = new FakeAuthService
        {
            State = AuthSessionState.LoggedIn,
            CurrentEntitlement = new Entitlement(activeTier, null, null, null, 0),
        };
        var orchestrator = BuildAndStartWithAuth(auth);

        RaiseHotkeyPressed();

        _audio.Verify(a => a.StartAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(DictationState.Recording, orchestrator.State);
    }

    [Fact]
    public void Hotkey_press_when_LoggedOut_starts_recording_legacy_path()
    {
        // Legacy BYOK pre-EP-10.11: user no logueado, igual graba (la transcripción
        // pega contra OpenAI directo con la key del onboarding).
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var auth = new FakeAuthService
        {
            State = AuthSessionState.LoggedOut,
            CurrentEntitlement = null,
        };
        var orchestrator = BuildAndStartWithAuth(auth);

        RaiseHotkeyPressed();

        Assert.Equal(DictationState.Recording, orchestrator.State);
    }

    [Fact]
    public void Hotkey_press_when_no_AuthService_injected_starts_recording_legacy_path()
    {
        // Constructor legacy de 10 args (sin auth) — el gate no aplica, recording arranca.
        _audio.Setup(a => a.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var orchestrator = BuildAndStart();

        RaiseHotkeyPressed();

        Assert.Equal(DictationState.Recording, orchestrator.State);
    }

    // ===== Fakes para historial / process resolver =====

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Saved { get; set; } = new();
        public event EventHandler? SettingsChanged;
        public AppSettings Load() => Saved;
        public void Save(AppSettings settings) { Saved = settings; SettingsChanged?.Invoke(this, EventArgs.Empty); }
    }

    private sealed class FakeProcessResolver : ITargetProcessResolver
    {
        public string ResolvedName { get; set; } = "test.exe";
        public string Resolve(IntPtr hwnd) => ResolvedName;
    }

    // Fake IAuthService minimalista — solo expone State + CurrentEntitlement que es lo
    // que el orchestrator lee para el gate de EP-10.12. Los demás métodos son no-ops.
    private sealed class FakeAuthService : IAuthService
    {
        public AuthSessionState State { get; set; } = AuthSessionState.LoggedOut;
        public UserProfile? CurrentProfile { get; set; }
        public Entitlement? CurrentEntitlement { get; set; }
        public bool IsOfflineMode => false;
        public AuthInitOutcome LastInitializeOutcome => AuthInitOutcome.NotRun;
        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<string>? AuthPendingReceived
        {
            add { }
            remove { }
        }

        public void RaiseAuthPendingReceived(string email) { /* no-op para tests del orchestrator */ }

        public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task StartLoginAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<AuthCallbackResult> HandleAuthCallbackAsync(
            IReadOnlyDictionary<string, string> queryParams, CancellationToken ct) =>
            Task.FromResult(new AuthCallbackResult(false, null, null, null));
        public Task LogoutAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<string?> GetCurrentAccessTokenAsync(CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task<string?> ForceRefreshAccessTokenAsync(CancellationToken ct) =>
            Task.FromResult<string?>(null);
        public Task<Entitlement?> RefreshEntitlementAsync(CancellationToken ct) =>
            Task.FromResult(CurrentEntitlement);
        public Task<Entitlement?> RefreshEntitlementWithBackoffAsync(
            Func<Entitlement, bool> isAcceptable, CancellationToken ct) =>
            Task.FromResult(CurrentEntitlement);
    }
}
