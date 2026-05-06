using Spikit.Models;
using Spikit.Native;

namespace Spikit.Tests.Models;

public class HotkeyDefinitionTests
{
    [Fact]
    public void Default_is_Ctrl_Alt_M()
    {
        var def = HotkeyDefinition.Default;

        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, def.Modifiers);
        Assert.Equal(VirtualKeys.M, def.VirtualKey);
    }

    [Fact]
    public void ToString_renders_default_as_human_readable()
    {
        Assert.Equal("Ctrl+Alt+M", HotkeyDefinition.Default.ToString());
    }

    [Theory]
    [InlineData(HotkeyModifiers.Control, VirtualKeys.Space, "Ctrl+Space")]
    [InlineData(HotkeyModifiers.Shift | HotkeyModifiers.Win, VirtualKeys.Enter, "Shift+Win+Enter")]
    [InlineData(HotkeyModifiers.None, 0x70u, "F1")]
    [InlineData(HotkeyModifiers.Alt, 0x41u, "Alt+A")]
    public void ToString_orders_modifiers_consistently(HotkeyModifiers mods, uint vk, string expected)
    {
        var def = new HotkeyDefinition(mods, vk);
        Assert.Equal(expected, def.ToString());
    }

    [Fact]
    public void Records_with_same_values_are_equal()
    {
        var a = new HotkeyDefinition(HotkeyModifiers.Control, VirtualKeys.Space);
        var b = new HotkeyDefinition(HotkeyModifiers.Control, VirtualKeys.Space);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Records_with_different_values_are_not_equal()
    {
        var a = new HotkeyDefinition(HotkeyModifiers.Control, VirtualKeys.Space);
        var b = new HotkeyDefinition(HotkeyModifiers.Alt, VirtualKeys.Space);

        Assert.NotEqual(a, b);
    }
}
