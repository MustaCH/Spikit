using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spikit.Models;
using Spikit.Native;
using Spikit.ViewModels;
using Spikit.Views;

namespace Spikit.Services.Toast;

// Implementación WPF del IToastHost: crea ToastWindow por cada toast, las posiciona
// bottom-right del monitor donde está el cursor (FLOW 5 — D-1), y las apila vertical
// con gap 8px. Reposiciona toda la pila cuando entra/sale un toast.
//
// Mantiene el orden de inserción explícito (List<ActiveToast>) — el Dictionary del
// service trackea la cola lógica, pero el host necesita orden visual estable
// independiente.
internal sealed class WpfToastHost : IToastHost, IDisposable
{
    // Gap visual del borde del workArea al toast. El RootGrid del XAML ya tiene 24px de
    // padding extra a cada lado para que el drop shadow blur (BlurRadius=32) no se corte
    // contra el borde de la Window (AllowsTransparency=true). Sumamos 24 acá para que el
    // visible Border quede ~24+24=48px del borde físico, alineado con FLOW 5 perceptible.
    private const double Margin = 24.0;
    private const double Gap = 8.0;

    private readonly IServiceProvider _services;
    private readonly ILogger<WpfToastHost> _logger;
    private readonly Dispatcher _dispatcher;
    private readonly List<ActiveToast> _active = new();
    private bool _disposed;

    public event EventHandler<Guid>? Dismissed;

    public WpfToastHost(IServiceProvider services, ILogger<WpfToastHost> logger)
    {
        _services = services;
        _logger = logger;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public void Show(Guid id, ToastNotification notification)
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(() => ShowOnUi(id, notification));
    }

    public void Refresh(Guid id, ToastNotification notification)
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(() =>
        {
            var existing = _active.FirstOrDefault(t => t.Id == id);
            existing?.ViewModel.Apply(notification);
        });
    }

    public void Dismiss(Guid id)
    {
        if (_disposed) return;
        _dispatcher.BeginInvoke(() =>
        {
            var existing = _active.FirstOrDefault(t => t.Id == id);
            if (existing is null) return;
            // BeginLeave anima fade-out y al terminar dispara Close() → OnWindowClosed
            // limpia _active y emite Dismissed. Idempotente si BeginLeave ya corrió.
            existing.Window.BeginLeave(() => { });
        });
    }

    private void ShowOnUi(Guid id, ToastNotification notification)
    {
        try
        {
            var vmLogger = _services.GetService<ILogger<ToastViewModel>>();
            var vm = new ToastViewModel(notification, vmLogger);
            var window = new ToastWindow(vm);
            window.Closed += (_, _) => OnWindowClosed(id);

            _active.Add(new ActiveToast { Id = id, Window = window, ViewModel = vm });

            // Pre-Show la window vive sin PresentationSource → DesiredSize = 0×0 y un
            // RepositionAll inicial saldría con tamaño cero. Truco: posicionamos fuera de
            // pantalla, hacemos Show() para que el visual tree se monte (mide y arregla),
            // y al Loaded reposicionamos sobre la pantalla. RootGrid arranca en Opacity=0
            // así que el flash inicial es invisible incluso fuera de pantalla.
            window.Left = -10_000;
            window.Top = -10_000;
            window.Loaded += (_, _) => RepositionAll();
            window.Show();

            _logger.LogInformation("Toast shown: severity={Sev} title={Title}",
                notification.Severity, notification.Title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error mostrando ToastWindow {Id}", id);
            _active.RemoveAll(t => t.Id == id);
        }
    }

    private void OnWindowClosed(Guid id)
    {
        var index = _active.FindIndex(t => t.Id == id);
        if (index >= 0) _active.RemoveAt(index);

        RepositionAll();
        Dismissed?.Invoke(this, id);
    }

    // Recalcula Left/Top de los toasts visibles. Más nuevo (último en _active) abajo,
    // más antiguos arriba. Ancla bottom-right del monitor donde está el cursor.
    //
    // El ActualWidth/Height ya INCLUYE el RootGrid Margin=24 del XAML (espacio del shadow
    // bleed). Para que el Border visible quede a ~24px del borde físico del workArea,
    // pegamos el borde de la Window al borde físico — el margin del RootGrid hace la cuenta.
    private void RepositionAll()
    {
        if (_active.Count == 0) return;

        if (!TryGetCursorMonitorWorkArea(out var workArea))
        {
            // Fallback: usar primary screen via SystemParameters (DIPs).
            workArea = new Rect(
                SystemParameters.WorkArea.X,
                SystemParameters.WorkArea.Y,
                SystemParameters.WorkArea.Width,
                SystemParameters.WorkArea.Height);
        }

        var bottom = workArea.Bottom;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var toast = _active[i];
            if (toast.Window.ActualHeight <= 0)
            {
                toast.Window.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            }

            var width = toast.Window.ActualWidth > 0 ? toast.Window.ActualWidth : toast.Window.DesiredSize.Width;
            var height = toast.Window.ActualHeight > 0 ? toast.Window.ActualHeight : toast.Window.DesiredSize.Height;

            toast.Window.Left = workArea.Right - width;
            toast.Window.Top = bottom - height;
            bottom -= height + Gap;

            _logger.LogDebug(
                "Toast positioned: id={Id} workArea=({WAR},{WAB}) size=({W}x{H}) → left={L} top={T}",
                toast.Id, workArea.Right, workArea.Bottom, width, height, toast.Window.Left, toast.Window.Top);
        }
    }

    // Obtiene el work area del monitor donde está el cursor en DIPs.
    // GetMonitorInfo devuelve pixels físicos; dividimos por DPI scaling para WPF.
    private bool TryGetCursorMonitorWorkArea(out Rect workArea)
    {
        workArea = default;

        if (!User32.GetCursorPos(out var cursor)) return false;

        var hMonitor = User32.MonitorFromPoint(cursor, User32.MONITOR_DEFAULTTONEAREST);
        if (hMonitor == IntPtr.Zero) return false;

        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!User32.GetMonitorInfo(hMonitor, ref info)) return false;

        // Si MainWindow ya existe la usamos para resolver DPI; si no (la app recién arranca),
        // asumimos 1.0 y dejamos que un reposition posterior lo corrija. Crear una Window
        // efímera solo para medir DPI tiene side-effects feos (parpadeo en taskbar).
        var anchor = Application.Current?.MainWindow;
        var (scaleX, scaleY) = anchor is not null
            ? (VisualTreeHelper.GetDpi(anchor).DpiScaleX, VisualTreeHelper.GetDpi(anchor).DpiScaleY)
            : (1.0, 1.0);

        workArea = new Rect(
            info.rcWork.Left / scaleX,
            info.rcWork.Top / scaleY,
            (info.rcWork.Right - info.rcWork.Left) / scaleX,
            (info.rcWork.Bottom - info.rcWork.Top) / scaleY);

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _dispatcher.BeginInvoke(() =>
        {
            foreach (var toast in _active.ToList())
            {
                try { toast.Window.Close(); }
                catch { /* swallow during shutdown */ }
            }
            _active.Clear();
        });
    }

    private sealed class ActiveToast
    {
        public Guid Id { get; init; }
        public ToastWindow Window { get; init; } = null!;
        public ToastViewModel ViewModel { get; init; } = null!;
    }
}
