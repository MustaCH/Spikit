using System.Text;
using Spikit.Native;

namespace Spikit.Models;

public sealed record HotkeyDefinition(HotkeyModifiers Modifiers, uint VirtualKey)
{
    // Ctrl+Alt+M — push-to-talk default cableado en V1.
    // Cambiado desde Ctrl+Alt+Space (conflicto con otras apps en el sistema de Nacho).
    public static HotkeyDefinition Default { get; } = new(
        HotkeyModifiers.Control | HotkeyModifiers.Alt,
        VirtualKeys.M);

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) sb.Append("Win+");
        sb.Append(VirtualKeys.GetName(VirtualKey));
        return sb.ToString();
    }
}
