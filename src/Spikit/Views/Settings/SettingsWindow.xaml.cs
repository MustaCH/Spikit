using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Spikit.Native;
using Spikit.ViewModels.Settings;

namespace Spikit.Views.Settings;

public partial class SettingsWindow : Window
{
    // Glyphs del botón maximizar/restaurar. Igual que en otras apps fluent: cuando la
    // window está normal mostramos un cuadrado simple ("maximizar"); cuando está maximizada
    // mostramos dos cuadrados superpuestos ("restaurar abajo").
    private static readonly Geometry MaxGlyphData =
        Geometry.Parse("M 0,0 L 10,0 L 10,10 L 0,10 Z");

    private static readonly Geometry RestoreGlyphData =
        Geometry.Parse("M 2,0 L 10,0 L 10,8 L 8,8 M 0,2 L 8,2 L 8,10 L 0,10 Z");

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ViewModel = viewModel;

        StateChanged += OnWindowStateChanged;
    }

    public SettingsViewModel ViewModel { get; }

    // Win11 polish: bordes redondeados + dark title bar + Mica como backdrop.
    // En Win10 / Win11 < 22H2 cada llamada degrada al solid del XAML sin crash.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        DwmHelper.ApplyRoundedCorners(this, DwmWindowCornerPreference.Round);
        DwmHelper.ApplyDarkTitleBar(this);
        DwmHelper.ApplyBackdrop(this, DwmSystemBackdropType.MainWindow);

        // Hook al WndProc: Windows custom-chrome con WindowStyle=None tiene un bug clásico
        // al maximizar — extiende la window ~7-8px más allá de la pantalla, cortando el
        // contenido contra los bordes y la taskbar. Interceptamos WM_GETMINMAXINFO para
        // decirle a Windows: "tu MaxSize/MaxPosition es el WorkArea del monitor de esta
        // window". Así maximize llena el viewport visible exacto.
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProcHook);
    }

    private static IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == User32.WM_GETMINMAXINFO)
        {
            ApplyWorkAreaToMaxInfo(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void ApplyWorkAreaToMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var monitor = User32.MonitorFromWindow(hwnd, User32.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!User32.GetMonitorInfo(monitor, ref info)) return;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        // Origen del Maximize relativo al monitor (no al desktop): rcWork - rcMonitor.
        mmi.ptMaxPosition.X = Math.Abs(info.rcWork.Left - info.rcMonitor.Left);
        mmi.ptMaxPosition.Y = Math.Abs(info.rcWork.Top - info.rcMonitor.Top);
        // Tamaño del Maximize = ancho/alto del WorkArea (ya descuenta la taskbar).
        mmi.ptMaxSize.X = Math.Abs(info.rcWork.Right - info.rcWork.Left);
        mmi.ptMaxSize.Y = Math.Abs(info.rcWork.Bottom - info.rcWork.Top);
        // El track-size también debe respetar el WorkArea para que el dragging-to-edge
        // (snap arriba) no sobrepase la pantalla.
        mmi.ptMaxTrackSize.X = mmi.ptMaxSize.X;
        mmi.ptMaxTrackSize.Y = mmi.ptMaxSize.Y;
        Marshal.StructureToPtr(mmi, lParam, fDeleteOld: true);
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        // Toggle del glyph entre maximizar (cuadrado) y restaurar (dos cuadrados). El
        // recorte del contenido al maximizar lo soluciona WndProcHook → WM_GETMINMAXINFO,
        // no necesitamos compensar con padding al root.
        if (FindName("MaxGlyph") is System.Windows.Shapes.Path glyph)
        {
            glyph.Data = WindowState == WindowState.Maximized ? RestoreGlyphData : MaxGlyphData;
        }
    }

    // Title bar custom — los 3 botones de la franja superior pasan por acá.
    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
