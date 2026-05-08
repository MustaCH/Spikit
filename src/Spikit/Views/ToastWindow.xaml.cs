using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using Spikit.Native;
using Spikit.ViewModels;

namespace Spikit.Views;

// Toast bottom-right (EP-5.3 / FLOW 5). Window borderless top-most que no roba foco
// y se cierra sola tras la animación de salida. WS_EX_NOACTIVATE para que el press de
// la pill no se distraiga; WS_EX_TOOLWINDOW para no aparecer en alt-tab.
//
// Accept clicks en la acción opcional — la window NO usa WS_EX_TRANSPARENT (a diferencia
// de la pill) para que el botón sea interactivo. El resto del área no captura input por
// el código del Border (no tiene handlers).
public partial class ToastWindow : Window
{
    private static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(240);
    private static readonly TimeSpan LeaveDuration = TimeSpan.FromMilliseconds(200);

    private readonly ToastViewModel _viewModel;
    private bool _leaving;

    public ToastWindow(ToastViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        _viewModel.DismissRequested += OnDismissRequested;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var current = User32.GetWindowLong(hwnd, WindowExStyles.GWL_EXSTYLE);
        // NoActivate evita que mostrar el toast robe foco al editor del usuario.
        // ToolWindow lo saca de alt-tab.
        var newStyle = current
            | WindowExStyles.WS_EX_NOACTIVATE
            | WindowExStyles.WS_EX_TOOLWINDOW;
        User32.SetWindowLong(hwnd, WindowExStyles.GWL_EXSTYLE, newStyle);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        PlayEnterAnimation();
    }

    private void PlayEnterAnimation()
    {
        // Slide-in desde la derecha (+40px → 0) + fade (0 → 1). 240ms ease-out.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fadeIn = new DoubleAnimation(0, 1, EnterDuration) { EasingFunction = ease };
        var slideIn = new DoubleAnimation(40, 0, EnterDuration) { EasingFunction = ease };

        RootGrid.BeginAnimation(OpacityProperty, fadeIn);
        RootTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slideIn);
    }

    // Llamado por el host cuando el ToastService quiere cerrar este toast (auto-dismiss
    // expirado, max-3 evict, o usuario ejecutó la acción).
    public void BeginLeave(Action onComplete)
    {
        if (_leaving) return;
        _leaving = true;

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fadeOut = new DoubleAnimation(1, 0, LeaveDuration) { EasingFunction = ease };
        fadeOut.Completed += (_, _) =>
        {
            try { onComplete(); }
            finally { Close(); }
        };

        RootGrid.BeginAnimation(OpacityProperty, fadeOut);
    }

    private void OnDismissRequested(object? sender, EventArgs e)
    {
        // El click en la acción dispara DismissRequested. Iniciamos animación de salida;
        // el host se entera del cierre via OnClosed.
        BeginLeave(() => { });
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.DismissRequested -= OnDismissRequested;
        base.OnClosed(e);
    }
}
