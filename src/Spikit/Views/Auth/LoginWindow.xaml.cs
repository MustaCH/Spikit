using System.Windows;
using System.Windows.Media.Animation;
using Microsoft.Extensions.Logging;
using Spikit.Native;
using Spikit.ViewModels.Auth;

namespace Spikit.Views.Auth;

// Window standalone que es la única UI visible cuando no hay sesión activa (FLOW 0 +
// ADR-0008). El bootstrap del shell la instancia desde el StartupRouter; el ciclo de
// vida (mostrar / cerrar / rutear a la next surface) lo orquesta App.xaml.cs en EP-11.4.
//
// Esta ventana es **autocontenida**: tiene su propio chrome custom, su propio glow,
// y delega toda la lógica al LoginViewModel. El code-behind sólo maneja:
//   1) Aplicar Mica/dark title bar/rounded corners post-OnSourceInitialized.
//   2) Suscribirse a vm.RequestClose → fade-out + Close.
//   3) Cerrar la app cuando el usuario aprieta X (D-11 del flows.md).
public partial class LoginWindow : Window
{
    private readonly LoginViewModel _viewModel;
    private readonly ILogger<LoginWindow> _logger;

    // Cuando el VM dispara RequestClose hacemos el fade-out de la window (200ms opacity
    // + scale 1→0.98) y al terminar invocamos Close(). Sin el flag, un Close() entrante
    // del usuario (X / Alt+F4) podría disparar el fade-out de nuevo y dejar la window
    // visible mientras la app intenta apagar — flag corta el handler de Closing.
    private bool _isFadingOut;

    public LoginWindow(LoginViewModel viewModel, ILogger<LoginWindow> logger)
    {
        _viewModel = viewModel;
        _logger = logger;
        InitializeComponent();
        DataContext = viewModel;

        _viewModel.RequestClose += OnRequestClose;
    }

    public LoginViewModel ViewModel => _viewModel;

    // Win11 polish — chrome custom + Mica como backdrop. Idéntico al OnboardingWindow
    // y SettingsWindow. En Win10 / Win11 < 22H2 cada llamada degrada silenciosamente
    // al solid del XAML.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DwmHelper.ApplyRoundedCorners(this, DwmWindowCornerPreference.Round, _logger);
        DwmHelper.ApplyDarkTitleBar(this, _logger);
        DwmHelper.ApplyBackdrop(this, DwmSystemBackdropType.MainWindow, _logger);
    }

    // El ✕ del title bar custom dispara Close. Sin lógica de confirmación: cerrar la
    // LoginWindow == cerrar la app (D-11). EP-11.4 va a interceptar esto a nivel de
    // App.xaml.cs.
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // Fade-out + Close cuando el VM lo pide (típicamente tras el microflash success o
    // un LoggedIn externo detectado vía StateChanged). 200ms para que se sienta
    // orgánico — D-14 del flows.md.
    private void OnRequestClose(object? sender, EventArgs e)
    {
        if (_isFadingOut) return;
        _isFadingOut = true;

        // EP-11.4 — clear `_activeLoginWindow` en App.xaml.cs antes del fade para
        // evitar que un URI forwardeado durante los 200ms del fade ruteen al VM
        // mid-cierre (race teórica baja pero el patrón limpio cuesta cero).
        if (Application.Current is App app)
        {
            app.ClearActiveLoginWindow();
        }

        // RenderTransform sobre la Window vive en code-behind para no ensuciar el XAML
        // (la window XAML no tiene RenderTransform por default). Asignamos uno justo
        // antes de animar.
        var scale = new System.Windows.Media.ScaleTransform(1.0, 1.0);
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = scale;

        var sb = new Storyboard();

        var opacity = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(opacity, this);
        Storyboard.SetTargetProperty(opacity, new PropertyPath(OpacityProperty));
        sb.Children.Add(opacity);

        var sx = new DoubleAnimation
        {
            From = 1.0,
            To = 0.98,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(sx, scale);
        Storyboard.SetTargetProperty(sx, new PropertyPath(System.Windows.Media.ScaleTransform.ScaleXProperty));
        sb.Children.Add(sx);

        var sy = new DoubleAnimation
        {
            From = 1.0,
            To = 0.98,
            Duration = TimeSpan.FromMilliseconds(200),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(sy, scale);
        Storyboard.SetTargetProperty(sy, new PropertyPath(System.Windows.Media.ScaleTransform.ScaleYProperty));
        sb.Children.Add(sy);

        sb.Completed += (_, _) => Close();
        sb.Begin();
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.RequestClose -= OnRequestClose;
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
