using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Extensions.Logging;

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

// Helper centralizado para aplicar atributos del DWM con detección de versión y fallback silencioso.
// Decisión EP-6.4: usamos P/Invoke propio (no WPF-UI) porque queremos control fino sobre el
// momento de aplicación (OnSourceInitialized) y un único punto de verdad sobre cómo degrada
// en Win10 / Win11 < 22H2.
//
// Nota sobre Acrylic en la DictationPill: WPF requiere que el HWND tenga el "system frame"
// del DWM para renderizar Mica/Acrylic. Una window con AllowsTransparency=True (como la pill)
// elimina ese frame y el atributo SystemBackdropType es ignorado por DWM. La pill mantiene
// fallback solid #0A0A0A — ver DictationPillWindow.xaml.cs para el comentario completo.
internal static class DwmHelper
{
    // Win11 22H2 = build 22621. SystemBackdropType solo existe desde ahí; antes la API
    // devuelve E_INVALIDARG y el atributo se ignora.
    public static bool IsWin11Build22621OrLater()
    {
        var v = Environment.OSVersion.Version;
        return v.Major >= 10 && v.Build >= 22621;
    }

    // Activa dark title bar en Win10 1809+ / Win11. Idempotente. Silently no-op en Win7/8.
    public static bool ApplyDarkTitleBar(Window window, ILogger? logger = null)
    {
        var hwnd = TryGetHwnd(window);
        if (hwnd == IntPtr.Zero) return false;

        int dark = 1;
        var hr = Dwmapi.DwmSetWindowAttribute(hwnd, DwmWindowAttribute.UseImmersiveDarkMode, ref dark, sizeof(int));
        if (hr != 0)
        {
            logger?.LogDebug("DwmHelper.ApplyDarkTitleBar: hresult 0x{HResult:X} (esperable en Win < 1809)", hr);
            return false;
        }
        return true;
    }

    public static bool ApplyRoundedCorners(Window window, DwmWindowCornerPreference preference, ILogger? logger = null)
    {
        var hwnd = TryGetHwnd(window);
        if (hwnd == IntPtr.Zero) return false;

        int pref = (int)preference;
        var hr = Dwmapi.DwmSetWindowAttribute(hwnd, DwmWindowAttribute.WindowCornerPreference, ref pref, sizeof(int));
        if (hr != 0)
        {
            logger?.LogDebug("DwmHelper.ApplyRoundedCorners: hresult 0x{HResult:X} (esperable en Win10)", hr);
            return false;
        }
        return true;
    }

    // Aplica el system backdrop (Mica / Acrylic / Tabbed) sobre la window.
    //
    // Si la API se aplica con éxito (Win11 22H2+) además se setea Window.Background = Transparent
    // para que el efecto se vea — sin esto, el solid del XAML tapa el backdrop. El Border interno
    // de cada window mantiene su BorderBrush para conservar el marco visual.
    //
    // En Win10 / Win11 < 22H2 / si la API falla por cualquier razón → no toca el background y
    // la window queda con el solid del XAML. Sin crash, sin flicker.
    public static bool ApplyBackdrop(Window window, DwmSystemBackdropType type, ILogger? logger = null)
    {
        if (!IsWin11Build22621OrLater())
        {
            logger?.LogDebug("DwmHelper.ApplyBackdrop: Win11 < 22H2, fallback al solid del XAML");
            return false;
        }

        var hwnd = TryGetHwnd(window);
        if (hwnd == IntPtr.Zero) return false;

        int backdrop = (int)type;
        var hr = Dwmapi.DwmSetWindowAttribute(hwnd, DwmWindowAttribute.SystemBackdropType, ref backdrop, sizeof(int));
        if (hr != 0)
        {
            logger?.LogWarning("DwmHelper.ApplyBackdrop falló (type={Type}): hresult 0x{HResult:X} — fallback al solid del XAML", type, hr);
            return false;
        }

        // El backdrop solo se ve si lo que el XAML dibuja por encima permite ver a través.
        // En lugar de Transparent puro (Mica al 100% — depende del wallpaper) usamos un
        // brush "Mica-aware" del theme con alpha 92%: el color del theme predomina, Mica
        // se ve como textura de profundidad sutil, y el contraste de textos queda garantizado
        // sin importar qué wallpaper tenga el usuario.
        //
        // SetResourceReference (no asignación directa) hace que el background se actualice
        // automáticamente cuando ThemeService swappea el ResourceDictionary en runtime.
        window.SetResourceReference(Window.BackgroundProperty, "SpkBgCanvasMicaBrush");
        return true;
    }

    private static IntPtr TryGetHwnd(Window window)
    {
        // OnSourceInitialized garantiza HWND válido, pero un caller temprano puede llegar
        // antes — en ese caso el helper no hace nada.
        return new WindowInteropHelper(window).Handle;
    }
}
