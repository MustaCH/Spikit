using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Models;
using Spikit.Native;
using Spikit.Services.Hotkey;
using Spikit.Services.Orchestration;
using Spikit.Services.Settings;
using Spikit.ViewModels.Settings.Sections;

namespace Spikit.Tests.ViewModels.Settings.Sections;

public class HotkeySectionViewModelTests
{
    private static (HotkeySectionViewModel vm, FakeSettingsService settings, FakeConfigWriter writer, FakeDemoMode demo, FakeHotkeyService hotkey) MakeVm(
        HotkeyDefinition? persistedHotkey = null,
        HotkeyMode persistedMode = HotkeyMode.PushToTalk,
        IHotkeyConfigWriter? writerOverride = null,
        IDictationDemoMode? demoOverride = null,
        FakeHotkeyService? hotkeyOverride = null)
    {
        var settings = new FakeSettingsService
        {
            Saved = MakeAppSettings(persistedHotkey ?? HotkeyDefinition.Default, persistedMode),
        };
        var writer = (writerOverride as FakeConfigWriter) ?? new FakeConfigWriter();
        var demo = (demoOverride as FakeDemoMode) ?? new FakeDemoMode();
        var hotkey = hotkeyOverride ?? new FakeHotkeyService();

        var vm = new HotkeySectionViewModel(
            NullLogger<HotkeySectionViewModel>.Instance,
            writerOverride ?? writer,
            settings,
            demoOverride ?? demo,
            hotkey);

        return (vm, settings, writer, demo, hotkey);
    }

    private static AppSettings MakeAppSettings(HotkeyDefinition definition, HotkeyMode mode) => new()
    {
        Hotkey = HotkeySettings.From(definition, mode),
    };

    // ===== Bootstrap / precarga =====

    [Fact]
    public void Bootstrap_loads_persisted_hotkey_and_mode()
    {
        var custom = new HotkeyDefinition(
            HotkeyModifiers.Control | HotkeyModifiers.Shift,
            VirtualKeys.Space);
        var (vm, _, _, _, _) = MakeVm(persistedHotkey: custom, persistedMode: HotkeyMode.Toggle);

        Assert.Equal(custom, vm.Hotkey);
        Assert.Equal(HotkeyMode.Toggle, vm.Mode);
        Assert.True(vm.IsToggle);
        Assert.False(vm.IsPushToTalk);
    }

    [Fact]
    public void Bootstrap_falls_back_to_defaults_when_settings_missing()
    {
        // Sin settings persistidos previos → JsonSettingsService devuelve defaults
        // (Ctrl+Alt+M / PushToTalk).
        var (vm, _, _, _, _) = MakeVm();

        Assert.Equal(HotkeyDefinition.Default, vm.Hotkey);
        Assert.Equal(HotkeyMode.PushToTalk, vm.Mode);
    }

    // ===== HasPendingChanges =====

    [Fact]
    public void HasPendingChanges_false_on_fresh_load()
    {
        var (vm, _, _, _, _) = MakeVm();

        Assert.False(vm.HasPendingChanges);
        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void HasPendingChanges_true_when_combination_changes()
    {
        var (vm, _, _, _, _) = MakeVm();

        vm.Hotkey = new HotkeyDefinition(HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKeys.Space);

        Assert.True(vm.HasPendingChanges);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public void HasPendingChanges_true_when_mode_changes()
    {
        var (vm, _, _, _, _) = MakeVm();

        vm.IsToggle = true;

        Assert.True(vm.HasPendingChanges);
    }

    [Fact]
    public async Task HasPendingChanges_resets_to_false_after_successful_save()
    {
        var (vm, _, writer, _, _) = MakeVm();
        vm.IsToggle = true;
        Assert.True(vm.HasPendingChanges);

        await vm.SaveAsync();

        Assert.False(vm.HasPendingChanges);
        Assert.Equal(1, writer.CallCount);
    }

    // ===== Save =====

    [Fact]
    public async Task SaveAsync_invokes_writer_with_current_combo_and_mode()
    {
        var (vm, _, writer, _, _) = MakeVm();
        var newCombo = new HotkeyDefinition(HotkeyModifiers.Win, VirtualKeys.Space);
        vm.Hotkey = newCombo;
        vm.IsToggle = true;

        var ok = await vm.SaveAsync();

        Assert.True(ok);
        Assert.Equal(1, writer.CallCount);
        Assert.Equal(newCombo, writer.LastDefinition);
        Assert.Equal(HotkeyMode.Toggle, writer.LastMode);
    }

    [Fact]
    public async Task SaveAsync_shows_cb7_message_on_HotkeyRegistrationException()
    {
        var writer = new FakeConfigWriter
        {
            ThrowOnSave = new HotkeyRegistrationException("Win32 1409"),
        };
        var (vm, _, _, _, _) = MakeVm(writerOverride: writer);
        vm.Hotkey = new HotkeyDefinition(HotkeyModifiers.Control, VirtualKeys.M);

        var ok = await vm.SaveAsync();

        Assert.False(ok);
        Assert.True(vm.HasSaveError);
        Assert.Contains("en uso por el sistema o por otra app", vm.SaveError);
    }

    [Fact]
    public async Task SaveAsync_propagates_writer_save_failure_inline()
    {
        var writer = new FakeConfigWriter
        {
            ThrowOnSave = new HotkeyConfigSaveException("disk full"),
        };
        var (vm, _, _, _, _) = MakeVm(writerOverride: writer);
        vm.IsToggle = true;

        var ok = await vm.SaveAsync();

        Assert.False(ok);
        Assert.Equal("disk full", vm.SaveError);
    }

    [Fact]
    public async Task Capturing_a_new_hotkey_clears_previous_save_error()
    {
        var writer = new FakeConfigWriter
        {
            ThrowOnSave = new HotkeyRegistrationException("CB-7"),
        };
        var (vm, _, _, _, _) = MakeVm(writerOverride: writer);
        vm.Hotkey = new HotkeyDefinition(HotkeyModifiers.Control, VirtualKeys.M);

        await vm.SaveAsync();
        Assert.True(vm.HasSaveError);

        vm.Hotkey = new HotkeyDefinition(HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKeys.Space);

        Assert.Empty(vm.SaveError);
        Assert.False(vm.HasSaveError);
    }

    // ===== Demo mode =====

    [Fact]
    public void TestCommand_activates_demo_mode_and_shows_toast()
    {
        var demo = new FakeDemoMode();
        var (vm, _, _, _, _) = MakeVm(demoOverride: demo);

        vm.TestCommand.Execute(null);

        Assert.True(vm.IsDemoActive);
        Assert.Equal(1, demo.BeginDemoModeCalls);
        Assert.Equal(0, demo.EndDemoModeCalls);
        Assert.Contains("Probá tu hotkey", vm.ToastMessage);
        Assert.False(vm.ToastIsSuccess);
    }

    [Fact]
    public void Demo_mode_disables_form_editing()
    {
        var demo = new FakeDemoMode();
        var (vm, _, _, _, _) = MakeVm(demoOverride: demo);

        vm.TestCommand.Execute(null);

        // IsEditable es lo que la View bindea a IsEnabled de los inputs y radios.
        Assert.False(vm.IsEditable);
    }

    [Fact]
    public void CancelTest_ends_demo_mode_without_writing()
    {
        var demo = new FakeDemoMode();
        var (vm, _, writer, _, _) = MakeVm(demoOverride: demo);
        vm.TestCommand.Execute(null);

        vm.CancelTestCommand.Execute(null);

        Assert.False(vm.IsDemoActive);
        Assert.Equal(string.Empty, vm.ToastMessage);
        Assert.Equal(1, demo.EndDemoModeCalls);
        Assert.Equal(0, writer.CallCount);
    }

    [Fact]
    public void DemoHotkeyDetected_event_mutates_toast_to_success()
    {
        var demo = new FakeDemoMode();
        var (vm, _, _, _, _) = MakeVm(demoOverride: demo);
        vm.TestCommand.Execute(null);

        // El handler del VM usa Dispatcher.CheckAccess: cuando el evento se dispara desde
        // el thread del test (mismo dispatcher que construyó el VM), corre sincrónico.
        demo.RaiseDemoHotkeyDetected();

        Assert.Contains("Hotkey detectado", vm.ToastMessage);
        Assert.True(vm.ToastIsSuccess);
        Assert.False(vm.ToastIsWarning);
    }

    [Fact]
    public void DemoHotkeyDetected_does_not_set_warning_state()
    {
        // Reproduce el caso "el press llegó dentro del timeout": ToastIsSuccess=true,
        // ToastIsWarning permanece false. Cubre la transición desde el estado neutro inicial.
        var demo = new FakeDemoMode();
        var (vm, _, _, _, _) = MakeVm(demoOverride: demo);
        vm.TestCommand.Execute(null);

        Assert.False(vm.ToastIsSuccess);
        Assert.False(vm.ToastIsWarning);

        demo.RaiseDemoHotkeyDetected();

        Assert.True(vm.ToastIsSuccess);
        Assert.False(vm.ToastIsWarning);
    }

    // ===== Capture lifecycle (suspend/resume del hotkey global) =====

    // Bug fix de Nacho: si el usuario abre el campo de captura y aprieta la combinación que
    // estaba activa, Win32 se la traga antes de que llegue al control como KeyDown — la app
    // empieza a grabar en lugar de capturar la nueva combo. La fix suspende temporalmente
    // el hotkey global mientras el campo está abierto.
    [Fact]
    public void BeginCapture_suspends_hotkey_globally()
    {
        var hotkey = new FakeHotkeyService();
        var (vm, _, _, _, _) = MakeVm(hotkeyOverride: hotkey);

        vm.BeginCapture();

        Assert.Equal(1, hotkey.SuspendCalls);
        Assert.Equal(0, hotkey.ResumeCalls);
    }

    [Fact]
    public void EndCapture_resumes_hotkey_globally()
    {
        var hotkey = new FakeHotkeyService();
        var (vm, _, _, _, _) = MakeVm(hotkeyOverride: hotkey);

        vm.BeginCapture();
        vm.EndCapture();

        Assert.Equal(1, hotkey.SuspendCalls);
        Assert.Equal(1, hotkey.ResumeCalls);
    }

    // El AC del ticket pide explícitamente que el modo demo no invoque AudioCapture/Whisper.
    // En esta capa eso se traduce a "el VM no inicia ninguna sesión real al apretar Probar":
    // solo activa el flag a través de IDictationDemoMode. Ese flag es lo que en producción
    // cortocircuita el orchestrator antes de que llame AudioCaptureService.StartAsync.
    [Fact]
    public void TestCommand_does_not_invoke_writer_or_save()
    {
        var (vm, _, writer, demo, _) = MakeVm();

        vm.TestCommand.Execute(null);

        Assert.Equal(0, writer.CallCount);
        // BeginDemoMode es la única interacción con el orchestrator. El AC dice "no se llama
        // a AudioCaptureService ni a Whisper" — ambos están detrás de StartRecordingAsync,
        // que el orchestrator NO ejecuta cuando IsDemoMode=true (cubierto por
        // DictationOrchestratorTests si existieran; acá lo verificamos a nivel del contrato).
        Assert.Equal(1, demo.BeginDemoModeCalls);
    }

    // ===== Fakes =====

    private sealed class FakeConfigWriter : IHotkeyConfigWriter
    {
        public HotkeyDefinition? LastDefinition { get; private set; }
        public HotkeyMode? LastMode { get; private set; }
        public int CallCount { get; private set; }
        public Exception? ThrowOnSave { get; set; }

        public Task SaveAsync(HotkeyDefinition definition, HotkeyMode mode, CancellationToken ct = default)
        {
            CallCount++;
            LastDefinition = definition;
            LastMode = mode;
            if (ThrowOnSave is not null) throw ThrowOnSave;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings? Saved { get; set; }

        public event EventHandler? SettingsChanged;

        public AppSettings Load() => Saved ?? new AppSettings();

        public void Save(AppSettings settings)
        {
            Saved = settings;
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeHotkeyService : IHotkeyService
    {
        public int SuspendCalls { get; private set; }
        public int ResumeCalls { get; private set; }

        public HotkeyDefinition? CurrentRegistration => null;
        public bool IsPaused => false;

        public event EventHandler? HotkeyPressed;
        public event EventHandler? HotkeyReleased;
        public event EventHandler? CancelHotkeyPressed;
        public event EventHandler? PausedChanged;

        public void Register(HotkeyDefinition definition) { }
        public void Unregister() { }
        public void SetPaused(bool paused) { }
        public void TriggerManualPress() { }
        public void RegisterCancelHotkey() { }
        public void UnregisterCancelHotkey() { }
        public void SuspendForCapture() => SuspendCalls++;
        public void ResumeFromCapture() => ResumeCalls++;
        public void Dispose() { }

        // Suprime warnings de eventos no usados en este fake (los tests del VM solo
        // exercise SuspendForCapture / ResumeFromCapture).
        private void Unused()
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            HotkeyReleased?.Invoke(this, EventArgs.Empty);
            CancelHotkeyPressed?.Invoke(this, EventArgs.Empty);
            PausedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FakeDemoMode : IDictationDemoMode
    {
        public bool IsDemoMode { get; private set; }
        public int BeginDemoModeCalls { get; private set; }
        public int EndDemoModeCalls { get; private set; }

        public event EventHandler? DemoHotkeyDetected;

        public void BeginDemoMode()
        {
            BeginDemoModeCalls++;
            IsDemoMode = true;
        }

        public void EndDemoMode()
        {
            EndDemoModeCalls++;
            IsDemoMode = false;
        }

        public void RaiseDemoHotkeyDetected()
        {
            IsDemoMode = false;
            DemoHotkeyDetected?.Invoke(this, EventArgs.Empty);
        }
    }
}
