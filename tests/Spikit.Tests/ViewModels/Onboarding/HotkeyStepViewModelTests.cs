using Microsoft.Extensions.Logging.Abstractions;
using Spikit.Models;
using Spikit.Native;
using Spikit.Services.Hotkey;
using Spikit.ViewModels.Onboarding;

namespace Spikit.Tests.ViewModels.Onboarding;

public class HotkeyStepViewModelTests
{
    private static HotkeyStepViewModel MakeVm(IHotkeyConfigWriter? writer = null) =>
        new(NullLogger<HotkeyStepViewModel>.Instance, writer ?? new FakeConfigWriter());

    // ===== Bootstrap =====

    [Fact]
    public void Bootstrap_uses_default_hotkey_ctrl_alt_m()
    {
        var vm = MakeVm();

        Assert.NotNull(vm.Hotkey);
        Assert.Equal(HotkeyDefinition.Default, vm.Hotkey);
        Assert.True(vm.HasHotkey);
        Assert.Equal("Ctrl + Alt + M", vm.HotkeyDisplay);
    }

    [Fact]
    public void Bootstrap_uses_push_to_talk_mode()
    {
        var vm = MakeVm();

        Assert.Equal(HotkeyMode.PushToTalk, vm.Mode);
        Assert.True(vm.IsPushToTalk);
        Assert.False(vm.IsToggle);
    }

    [Fact]
    public void Default_hotkey_has_no_warning()
    {
        var vm = MakeVm();

        Assert.False(vm.HasWarning);
        Assert.Empty(vm.WarningMessage);
    }

    // ===== Mode toggling =====

    [Fact]
    public void Setting_IsToggle_true_switches_mode()
    {
        var vm = MakeVm();

        vm.IsToggle = true;

        Assert.Equal(HotkeyMode.Toggle, vm.Mode);
        Assert.False(vm.IsPushToTalk);
        Assert.True(vm.IsToggle);
    }

    [Fact]
    public void Setting_IsPushToTalk_true_after_toggle_returns_to_PTT()
    {
        var vm = MakeVm();
        vm.IsToggle = true;

        vm.IsPushToTalk = true;

        Assert.Equal(HotkeyMode.PushToTalk, vm.Mode);
    }

    [Fact]
    public void Setting_IsPushToTalk_false_does_not_switch_mode()
    {
        // Two-way binding del RadioButton al uncheck dispara IsPushToTalk=false; ese
        // setter no debe hacer nada para no romper la mutua exclusión.
        var vm = MakeVm();

        vm.IsPushToTalk = false;

        Assert.Equal(HotkeyMode.PushToTalk, vm.Mode);
    }

    // ===== Hotkey changes =====

    [Fact]
    public void Setting_hotkey_to_null_disables_HasHotkey()
    {
        var vm = MakeVm();

        vm.Hotkey = null;

        Assert.False(vm.HasHotkey);
        Assert.Empty(vm.HotkeyDisplay);
    }

    [Fact]
    public void Setting_hotkey_without_modifier_raises_warning()
    {
        var vm = MakeVm();

        vm.Hotkey = new HotkeyDefinition(HotkeyModifiers.None, VirtualKeys.M);

        Assert.True(vm.HasWarning);
        Assert.Contains("conflicto", vm.WarningMessage);
        // El warning NO bloquea: HasHotkey sigue true para que CanGoNext del shell
        // permita avanzar igual (el usuario decide).
        Assert.True(vm.HasHotkey);
    }

    [Fact]
    public void Switching_back_to_modified_combo_clears_warning()
    {
        var vm = MakeVm();
        vm.Hotkey = new HotkeyDefinition(HotkeyModifiers.None, VirtualKeys.M);
        Assert.True(vm.HasWarning);

        vm.Hotkey = new HotkeyDefinition(HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKeys.Space);

        Assert.False(vm.HasWarning);
        Assert.Empty(vm.WarningMessage);
    }

    [Fact]
    public void HotkeyDisplay_uses_spaced_plus_format()
    {
        var vm = MakeVm();

        vm.Hotkey = new HotkeyDefinition(
            HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift,
            VirtualKeys.Space);

        // ToString() de HotkeyDefinition devuelve "Ctrl+Alt+Shift+Space"; el VM lo
        // expande a " + " para que respire en el HotkeyCaptureField.
        Assert.Equal("Ctrl + Alt + Shift + Space", vm.HotkeyDisplay);
    }

    // ===== HotkeyStateChanged event =====

    [Fact]
    public void HotkeyStateChanged_fires_on_capture()
    {
        var vm = MakeVm();
        var fired = 0;
        vm.HotkeyStateChanged += (_, _) => fired++;

        vm.Hotkey = new HotkeyDefinition(HotkeyModifiers.Control, VirtualKeys.M);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void HotkeyStateChanged_does_not_fire_when_value_unchanged()
    {
        var vm = MakeVm();
        var fired = 0;
        vm.HotkeyStateChanged += (_, _) => fired++;

        // Re-asignar el mismo HotkeyDefinition (record con value equality) → no debe
        // disparar el evento porque SetProperty cortocircuita.
        vm.Hotkey = HotkeyDefinition.Default;

        Assert.Equal(0, fired);
    }

    // ===== SaveAsync (EP-3.6) =====

    [Fact]
    public async Task SaveAsync_returns_false_when_no_hotkey_captured()
    {
        var writer = new FakeConfigWriter();
        var vm = MakeVm(writer);
        vm.Hotkey = null;

        var ok = await vm.SaveAsync();

        Assert.False(ok);
        Assert.Contains("Capturá", vm.SaveError);
        Assert.Equal(0, writer.CallCount);
    }

    [Fact]
    public async Task SaveAsync_persists_definition_and_mode()
    {
        var writer = new FakeConfigWriter();
        var vm = MakeVm(writer);

        var ok = await vm.SaveAsync();

        Assert.True(ok);
        Assert.Empty(vm.SaveError);
        Assert.False(vm.HasSaveError);
        Assert.False(vm.IsSaving);
        Assert.Equal(1, writer.CallCount);
        Assert.Equal(HotkeyDefinition.Default, writer.LastDefinition);
        Assert.Equal(HotkeyMode.PushToTalk, writer.LastMode);
    }

    [Fact]
    public async Task SaveAsync_passes_toggle_mode_when_selected()
    {
        var writer = new FakeConfigWriter();
        var vm = MakeVm(writer);
        vm.IsToggle = true;

        await vm.SaveAsync();

        Assert.Equal(HotkeyMode.Toggle, writer.LastMode);
    }

    [Fact]
    public async Task SaveAsync_shows_cb7_message_on_HotkeyRegistrationException()
    {
        var writer = new FakeConfigWriter
        {
            ThrowOnSave = new HotkeyRegistrationException("Win32 1409"),
        };
        var vm = MakeVm(writer);

        var ok = await vm.SaveAsync();

        Assert.False(ok);
        Assert.True(vm.HasSaveError);
        Assert.Contains("en uso por el sistema o por otra app", vm.SaveError);
    }

    [Fact]
    public async Task SaveAsync_shows_writer_message_on_HotkeyConfigSaveException()
    {
        var writer = new FakeConfigWriter
        {
            ThrowOnSave = new HotkeyConfigSaveException("disk full"),
        };
        var vm = MakeVm(writer);

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
        var vm = MakeVm(writer);

        await vm.SaveAsync();
        Assert.True(vm.HasSaveError);

        vm.Hotkey = new HotkeyDefinition(HotkeyModifiers.Control | HotkeyModifiers.Shift, VirtualKeys.Space);

        Assert.Empty(vm.SaveError);
        Assert.False(vm.HasSaveError);
    }

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
}
