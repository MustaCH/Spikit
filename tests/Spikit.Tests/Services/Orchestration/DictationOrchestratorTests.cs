using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spikit.Models;
using Spikit.Services.Audio;
using Spikit.Services.Hotkey;
using Spikit.Services.Insertion;
using Spikit.Services.Orchestration;
using Spikit.Services.Transcription;

namespace Spikit.Tests.Services.Orchestration;

public class DictationOrchestratorTests
{
    private readonly Mock<IHotkeyService> _hotkey = new();
    private readonly Mock<IAudioCaptureService> _audio = new();
    private readonly Mock<ITranscriptionService> _transcription = new();
    private readonly Mock<ITextInsertionService> _insertion = new();
    private readonly Mock<IFloatingResultPresenter> _presenter = new();

    private DictationOrchestrator BuildAndStart()
    {
        var orchestrator = new DictationOrchestrator(
            _hotkey.Object, _audio.Object, _transcription.Object, _insertion.Object,
            _presenter.Object, NullLogger<DictationOrchestrator>.Instance);
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
        _presenter.Verify(p => p.Show(It.IsAny<string>(), It.IsAny<InsertionResult>()), Times.Never);
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

        _presenter.Verify(p => p.Show("texto a pegar", failureCode), Times.Once);
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

    private static async Task WaitForState(DictationOrchestrator orchestrator, DictationState target, int timeoutMs = 500)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (orchestrator.State != target && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.Equal(target, orchestrator.State);
    }
}
