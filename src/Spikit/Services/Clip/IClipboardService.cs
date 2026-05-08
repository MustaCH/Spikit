namespace Spikit.Services.Clip;

// Wrapper inyectable del System.Windows.Clipboard. La razón es testabilidad: el clipboard
// requiere STA + un message pump que en xUnit no siempre está garantizado. Inyectando una
// interfaz, el VM se puede testar sin tocar Win32.
public interface IClipboardService
{
    void SetText(string text);
}
