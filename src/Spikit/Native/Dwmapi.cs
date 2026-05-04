using System.Runtime.InteropServices;

namespace Spikit.Native;

internal static class Dwmapi
{
    private const string Dll = "dwmapi.dll";

    // Setea un atributo del DWM (dark mode, backdrop type, corner preference, etc.) sobre una ventana.
    [DllImport(Dll, PreserveSig = true)]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, DwmWindowAttribute attribute, ref int pvAttribute, int cbAttribute);
}

// Subset de DWMWINDOWATTRIBUTE relevante para Spikit (dark mode + Mica/Acrylic + corner radius).
// IDs completos: https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute
internal enum DwmWindowAttribute : uint
{
    UseImmersiveDarkMode = 20,
    WindowCornerPreference = 33,
    SystemBackdropType = 38,
}

// Tipo de backdrop del sistema (Win11 22H2+).
internal enum DwmSystemBackdropType : int
{
    Auto = 0,
    None = 1,
    MainWindow = 2,   // Mica
    Transient = 3,    // Acrylic
    TabbedWindow = 4, // Mica Alt
}

// Preferencia de redondeo de esquinas (Win11).
internal enum DwmWindowCornerPreference : int
{
    Default = 0,
    DoNotRound = 1,
    Round = 2,
    RoundSmall = 3,
}
