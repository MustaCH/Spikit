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

    private void RaiseSamples(short[] samples) =>
        _audio.Raise(a => a.SamplesAvailable += null, this, samples);

    [Fact]
    public void Initial_state_is_Idle()
    {
        var orchestrator = BuildAndStart();
        Assert.Equal(DictationState.Idle, orchestrator.State);
    }

    [Fact]
    public void Start_registers_hotkey_with_default_definition()
    {
        BuildAndStart();
        _hotkey.Verify(h => h.Register(HotkeyDefinition.Default), Times.Once);
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
