using System.Windows;

namespace Spikit.Services.Clip;

// Implementación productiva del IClipboardService. Usa el clipboard de WPF sobre Win32.
// El SetText puede tirar COMException si otra app tiene el clipboard locked — lo dejamos
// burbujear; el caller (HistorySectionVM) lo trata como error de UX.
public sealed class WpfClipboardService : IClipboardService
{
    public void SetText(string text) => Clipboard.SetText(text ?? string.Empty);
}
