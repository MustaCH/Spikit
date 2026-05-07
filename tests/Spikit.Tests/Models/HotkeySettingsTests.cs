using Spikit.Models;
using Spikit.Native;

namespace Spikit.Tests.Models;

public class HotkeySettingsTests
{
    [Fact]
    public void From_serializes_modifiers_with_comma_space_separator()
    {
        var def = new HotkeyDefinition(HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKeys.M);

        var settings = HotkeySettings.From(def, HotkeyMode.PushToTalk);

        // Enum.ToString() ordena flags por valor ascendente: Alt (0x1) antes de Control (0x2).
        // El parser de TryToRuntime acepta cualquier orden, así que es estable y reversible.
        Assert.Equal("Alt, Control", settings.Modifiers);
        Assert.Equal(VirtualKeys.M, settings.VirtualKey);
        Assert.Equal("PushToTalk", settings.Mode);
    }

    [Fact]
    public void From_handles_single_modifier()
    {
        var def = new HotkeyDefinition(HotkeyModifiers.Win, VirtualKeys.Space);

        var settings = HotkeySettings.From(def, HotkeyMode.Toggle);

        Assert.Equal("Win", settings.Modifiers);
        Assert.Equal("Toggle", settings.Mode);
    }

    [Fact]
    public void From_handles_no_modifiers()
    {
        var def = new HotkeyDefinition(HotkeyModifiers.None, 0x70 /* F1 */);

        var settings = HotkeySettings.From(def, HotkeyMode.PushToTalk);

        Assert.Equal("None", settings.Modifiers);
    }

    [Fact]
    public void TryToRuntime_parses_default_settings()
    {
        var settings = new HotkeySettings(); // defaults

        var ok = settings.TryToRuntime(out var def, out var mode);

        Assert.True(ok);
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, def.Modifiers);
        Assert.Equal(VirtualKeys.M, def.VirtualKey);
        Assert.Equal(HotkeyMode.PushToTalk, mode);
    }

    [Fact]
    public void TryToRuntime_strips_NoRepeat_from_persisted_modifiers()
    {
        // NoRepeat es flag de la API Win32 RegisterHotKey, no debería estar en settings.json
        // pero por las dudas el mapper lo limpia para no leakearlo al runtime.
        var settings = new HotkeySettings
        {
            Modifiers = "Control, Alt, NoRepeat",
            VirtualKey = VirtualKeys.M,
            Mode = "PushToTalk",
        };

        var ok = settings.TryToRuntime(out var def, out _);

        Assert.True(ok);
        Assert.False(def.Modifiers.HasFlag(HotkeyModifiers.NoRepeat));
        Assert.True(def.Modifiers.HasFlag(HotkeyModifiers.Control));
        Assert.True(def.Modifiers.HasFlag(HotkeyModifiers.Alt));
    }

    [Fact]
    public void TryToRuntime_returns_false_with_invalid_modifiers_string()
    {
        var settings = new HotkeySettings
        {
            Modifiers = "Garbage",
            VirtualKey = VirtualKeys.M,
            Mode = "PushToTalk",
        };

        var ok = settings.TryToRuntime(out var def, out var mode);

        Assert.False(ok);
        Assert.Equal(HotkeyDefinition.Default, def);
        Assert.Equal(HotkeyMode.PushToTalk, mode);
    }

    [Fact]
    public void TryToRuntime_returns_false_with_invalid_mode_string()
    {
        var settings = new HotkeySettings
        {
            Modifiers = "Control, Alt",
            VirtualKey = VirtualKeys.M,
            Mode = "BananaPress",
        };

        var ok = settings.TryToRuntime(out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryToRuntime_returns_false_when_VirtualKey_is_zero()
    {
        var settings = new HotkeySettings
        {
            Modifiers = "Control",
            VirtualKey = 0,
            Mode = "PushToTalk",
        };

        Assert.False(settings.TryToRuntime(out _, out _));
    }

    [Fact]
    public void From_then_TryToRuntime_roundtrips()
    {
        var original = new HotkeyDefinition(
            HotkeyModifiers.Control | HotkeyModifiers.Shift | HotkeyModifiers.Win,
            VirtualKeys.Space);

        var serialized = HotkeySettings.From(original, HotkeyMode.Toggle);
        Assert.True(serialized.TryToRuntime(out var roundtrip, out var mode));

        Assert.Equal(original, roundtrip);
        Assert.Equal(HotkeyMode.Toggle, mode);
    }
}
