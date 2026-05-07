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

    // Win11 polish: bordes redondeados nativos + dark title bar + Mica como backdrop.
    // Mismos atributos que OnboardingWindow para consistencia visual. En sistemas que no
    // soportan los atributos (Win10, Win11 pre-22H2) los DwmSetWindowAttribute devuelven
    // hresult de "no soportado" sin romper nada.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        int round = (int)DwmWindowCornerPreference.Round;
        Dwmapi.DwmSetWindowAttribute(hwnd, DwmWindowAttribute.WindowCornerPreference, ref round, sizeof(int));

        int dark = 1;
        Dwmapi.DwmSetWindowAttribute(hwnd, DwmWindowAttribute.UseImmersiveDarkMode, ref dark, sizeof(int));

        int mica = (int)DwmSystemBackdropType.MainWindow;
        Dwmapi.DwmSetWindowAttribute(hwnd, DwmWindowAttribute.SystemBackdropType, ref mica, sizeof(int));
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        // Toggle del glyph entre maximizar y restaurar. También compensamos el offset de
        // ~7px que mete WindowChrome al maximizar (la window se hace más grande que la
        // pantalla y el contenido queda recortado contra los bordes); el Padding del Border
        // outermost lo absorbe.
        if (FindName("MaxGlyph") is System.Windows.Shapes.Path glyph)
        {
            glyph.Data = WindowState == WindowState.Maximized ? RestoreGlyphData : MaxGlyphData;
        }

        // Compensar el offset clásico de WindowChrome cuando se maximiza una window con
        // WindowStyle=None: WPF agranda la window unos 7px en cada lado, lo que cortaría
        // el contenido. Aplicamos el padding al root Border del Content (Window.Content
        // es un Border en este XAML).
        if (Content is Border root)
        {
            root.Padding = WindowState == WindowState.Maximized
                ? new Thickness(7)
                : new Thickness(0);
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
