using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spikit.Models;
using Spikit.Native;
using Spikit.Services.Audio;
using Spikit.Services.Hotkey;
using Spikit.Services.Insertion;
using Spikit.Services.Orchestration;
using Spikit.Services.Settings;
using Spikit.Services.Toast;
using Spikit.Services.Transcription;

namespace Spikit.Tests.Services.Hotkey;

// Tests del writer con un fake in-memory del HotkeyService que simula CB-7 a demanda.
// El DictationOrchestrator se construye con mocks de sus dependencias — solo nos importa
// observar SetMode al final.
public class HotkeyConfigWriterTests
{
    private static readonly HotkeyDefinition NewHotkey =
        new(HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKeys.Space);

    private static readonly HotkeyDefinition PreviousHotkey =
        new(HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKeys.M);

    private static (HotkeyConfigWriter writer, FakeHotkeyService hotkey, FakeSettingsService settings, DictationOrchestrator orchestrator)
        Build(HotkeyDefinition? preRegistered = null)
    {
        var hotkey = new FakeHotkeyService();
        if (preRegistered is not null)
        {
            hotkey.Register(preRegistered);
        }

        var settings = new FakeSettingsService();
        var orchestrator = new DictationOrchestrator(
            hotkey,
            new Mock<IAudioCaptureService>().Object,
            new Mock<ITranscriptionService>().Object,
            new Mock<ITextInsertionService>().Object,
            new Mock<IFloatingResultPresenter>().Object,
            new Mock<Spikit.Services.History.IHistoryStore>().Object,
            settings,
            new Mock<ITargetProcessResolver>().Object,
            new Mock<IToastService>().Object,
            NullLogger<DictationOrchestrator>.Instance);
        var writer = new HotkeyConfigWriter(hotkey, settings, orchestrator, NullLogger<HotkeyConfigWriter>.Instance);
        return (writer, hotkey, settings, orchestrator);
    }

    [Fact]
    public async Task SaveAsync_registers_new_hotkey_and_persists()
    {
        var (writer, hotkey, settings, _) = Build(preRegistered: PreviousHotkey);

        await writer.SaveAsync(NewHotkey, HotkeyMode.Toggle);

        Assert.Equal(NewHotkey, hotkey.CurrentRegistration);
        Assert.NotNull(settings.Saved);
        Assert.Equal("Control, Shift", settings.Saved!.Hotkey.Modifiers);
        Assert.Equal(VirtualKeys.Space, settings.Saved.Hotkey.VirtualKey);
        Assert.Equal("Toggle", settings.Saved.Hotkey.Mode);
    }

    [Fact]
    public async Task SaveAsync_updates_orchestrator_mode_after_persist()
    {
        var (writer, _, _, orchestrator) = Build(preRegistered: PreviousHotkey);

        await writer.SaveAsync(NewHotkey, HotkeyMode.Toggle);

        Assert.Equal(HotkeyMode.Toggle, orchestrator.Mode);
    }

    [Fact]
    public async Task SaveAsync_propagates_HotkeyRegistrationException_and_restores_previous()
    {
        var (writer, hotkey, settings, orchestrator) = Build(preRegistered: PreviousHotkey);
        hotkey.ThrowOnRegister = (def) => def.Equals(NewHotkey)
            ? new HotkeyRegistrationException("CB-7")
            : null;

        await Assert.ThrowsAsync<HotkeyRegistrationException>(() =>
            writer.SaveAsync(NewHotkey, HotkeyMode.Toggle));

        // CB-7: la previa queda re-registrada para que el usuario no quede sin hotkey activo.
        Assert.Equal(PreviousHotkey, hotkey.CurrentRegistration);
        // Ni settings ni orchestrator se tocaron.
        Assert.Null(settings.Saved);
        Assert.Equal(HotkeyMode.PushToTalk, orchestrator.Mode);
    }

    [Fact]
    public async Task SaveAsync_rollbacks_when_settings_save_fails()
    {
        var (writer, hotkey, settings, orchestrator) = Build(preRegistered: PreviousHotkey);
        settings.ThrowOnSave = new IOException("disk full");

        await Assert.ThrowsAsync<HotkeyConfigSaveException>(() =>
            writer.SaveAsync(NewHotkey, HotkeyMode.Toggle));

        // Rollback: la previa volvió a registrarse, el orchestrator no cambió de modo.
        Assert.Equal(PreviousHotkey, hotkey.CurrentRegistration);
        Assert.Equal(HotkeyMode.PushToTalk, orchestrator.Mode);
    }

    [Fact]
    public async Task SaveAsync_works_with_no_previous_registration()
    {
        // Caso bootstrap inicial / onboarding sin previo.
        var (writer, hotkey, _, _) = Build(preRegistered: null);

        await writer.SaveAsync(NewHotkey, HotkeyMode.PushToTalk);

        Assert.Equal(NewHotkey, hotkey.CurrentRegistration);
    }

    private sealed class FakeHotkeyService : IHotkeyService
    {
        private HotkeyDefinition? _current;
        private bool _isPaused;

        public HotkeyDefinition? CurrentRegistration => _current;
        public bool IsPaused => _isPaused;

        public Func<HotkeyDefinition, Exception?>? ThrowOnRegister { get; set; }

        public event EventHandler? HotkeyPressed;
        public event EventHandler? HotkeyReleased;
        public event EventHandler? CancelHotkeyPressed;
        public event EventHandler? PausedChanged;

        public int RegisterCancelCalls { get; private set; }
        public int UnregisterCancelCalls { get; private set; }

        public void Register(HotkeyDefinition definition)
        {
            var ex = ThrowOnRegister?.Invoke(definition);
            if (ex is not null) throw ex;
            _current = definition;
        }

        public void Unregister() => _current = null;

        public void SetPaused(bool paused)
        {
            if (_isPaused == paused) return;
            _isPaused = paused;
            PausedChanged?.Invoke(this, EventArgs.Empty);
        }

        public void TriggerManualPress() => HotkeyPressed?.Invoke(this, EventArgs.Empty);

        public void RegisterCancelHotkey() => RegisterCancelCalls++;
        public void UnregisterCancelHotkey() => UnregisterCancelCalls++;

        public void SuspendForCapture() { /* no-op para los tests del config writer */ }
        public void ResumeFromCapture() { /* idem */ }

        public void Dispose() { }

        // Suprime warnings de eventos no usados en este fake.
        private void Unused()
        {
            HotkeyReleased?.Invoke(this, EventArgs.Empty);
            CancelHotkeyPressed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings? Saved { get; private set; }
        public Exception? ThrowOnSave { get; set; }

        public event EventHandler? SettingsChanged;

        public AppSettings Load() => Saved ?? new AppSettings();

        public void Save(AppSettings settings)
        {
            if (ThrowOnSave is not null) throw ThrowOnSave;
            Saved = settings;
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
