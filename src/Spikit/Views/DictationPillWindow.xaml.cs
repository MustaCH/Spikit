using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Spikit.Models;
using Spikit.Native;
using Spikit.Services.PillPosition;
using Spikit.Services.Settings;
using Spikit.ViewModels;
using Spikit.Views.Controls;

namespace Spikit.Views;

// Pill flotante topmost. Specs en docs/design-system.md §10.1.
// Configura WS_EX_NOACTIVATE + WS_EX_TRANSPARENT en SourceInitialized para no
// robar foco al target del paste y no interceptar clicks.
public partial class DictationPillWindow : Window
{
    // El Window contenedor incluye 70 DIPs de padding inferior para el shadow externo —
    // el "visual" de la pill termina ese pedazo ANTES del Window.Bottom. Para anchors
    // bottom hay que compensar sumando el padding al Top calculado por el service; para
    // anchors top no hace falta porque el visual arranca en Window.Top.
    private const double WindowBottomPaddingForShadow = 70.0;
    private static readonly TimeSpan EntryDuration = TimeSpan.FromMilliseconds(520);
    private static readonly TimeSpan LeaveDuration = TimeSpan.FromMilliseconds(420);
    private static readonly TimeSpan CrossFadeDuration = TimeSpan.FromMilliseconds(160);

    private readonly DictationPillViewModel _viewModel;
    private readonly IPillPositionService _positionService;
    private readonly ISettingsService _settingsService;
    private bool _isEntering;
    private bool _isLeaving;

    public DictationPillWindow(
        DictationPillViewModel viewModel,
        IPillPositionService positionService,
        ISettingsService settingsService)
    {
        _viewModel = viewModel;
        _positionService = positionService;
        _settingsService = settingsService;
        InitializeComponent();
        DataContext = viewModel;

        _viewModel.VisualModeChanged += OnVisualModeChanged;
        _viewModel.RmsLevelChanged += OnRmsLevelChanged;

        // Cuando el usuario cambia el anchor en Settings → General, el VM persiste y
        // SettingsChanged se dispara. Reposicionamos solo si la pill ya está cargada
        // (Loaded), evitando race con el bootstrap inicial.
        _settingsService.SettingsChanged += OnSettingsChanged;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        // WS_EX_NOACTIVATE: la pill no recibe foco al mostrarse.
        // WS_EX_TRANSPARENT: clicks pasan al contenido debajo.
        // WS_EX_TOOLWINDOW: no aparece en alt-tab.
        var current = User32.GetWindowLong(hwnd, WindowExStyles.GWL_EXSTYLE);
        var newStyle = current
            | WindowExStyles.WS_EX_NOACTIVATE
            | WindowExStyles.WS_EX_TRANSPARENT
            | WindowExStyles.WS_EX_TOOLWINDOW;
        User32.SetWindowLong(hwnd, WindowExStyles.GWL_EXSTYLE, newStyle);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ApplyAnchorFromSettings();
        ApplyVisualMode(_viewModel.VisualMode, animate: false);
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // SettingsChanged puede llegar desde otro thread. Marshall a UI antes de
        // tocar Window.Left/Top. Si la pill todavía no estaba Loaded, omite — Loaded
        // hará la primera posición usando el setting vigente.
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsLoaded) return;
            ApplyAnchorFromSettings();
        });
    }

    private void ApplyAnchorFromSettings()
    {
        var settings = _settingsService.Load();
        var anchor = settings.General.TryToAnchor();
        var placement = _positionService.Calculate(anchor, Width, Height);
        Left = placement.Left;
        // Para anchors bottom, el Window se extiende 70 DIPs por debajo del visual (shadow
        // padding). Compensamos para que el visual termine a 32 DIPs del workArea.Bottom.
        var isBottomAnchor = anchor is PillAnchor.BottomLeft or PillAnchor.BottomCenter or PillAnchor.BottomRight;
        Top = isBottomAnchor
            ? placement.Top + WindowBottomPaddingForShadow
            : placement.Top;
    }

    private void OnVisualModeChanged(object? sender, PillVisualMode mode)
    {
        Dispatcher.BeginInvoke(() => ApplyVisualMode(mode, animate: true));
    }

    private void OnRmsLevelChanged(object? sender, float rms)
    {
        Dispatcher.BeginInvoke(() => LogoWaveControl.RmsLevel = rms);
    }

    private void ApplyVisualMode(PillVisualMode mode, bool animate)
    {
        switch (mode)
        {
            case PillVisualMode.Hidden:
                if (animate) FadeOut();
                else { RootGrid.Opacity = 0; RootTranslate.Y = 140; }
                LogoWaveControl.Mode = LogoWaveMode.Idle;
                break;

            case PillVisualMode.Initializing:
                ApplyBorderAndShadow(quiet: true);
                LogoWaveControl.Mode = LogoWaveMode.Initializing;
                if (animate) FadeIn();
                break;

            case PillVisualMode.Recording:
                ApplyBorderAndShadow(quiet: false);
                LogoWaveControl.Mode = LogoWaveMode.Recording;
                if (animate) FadeIn();
                break;

            case PillVisualMode.Transcribing:
                ApplyBorderAndShadow(quiet: false);
                LogoWaveControl.Mode = LogoWaveMode.Transcribing;
                break;

            case PillVisualMode.Logo:
                ApplyBorderAndShadow(quiet: false);
                LogoWaveControl.Mode = LogoWaveMode.Logo;
                break;

            case PillVisualMode.Leaving:
                if (animate) FadeOut();
                break;
        }
    }

    private void ApplyBorderAndShadow(bool quiet)
    {
        if (quiet)
        {
            PillBorder.BorderBrush = (Brush)Resources["PillBorderQuiet"]!;
            // shadow.pill-quiet: solo drop shadow oscura. El glow rojo apagado.
            PillGlow.Opacity = 0;
        }
        else
        {
            PillBorder.BorderBrush = (Brush)Resources["PillBorderActive"]!;
            // shadow.pill-active: glow rojo centrado + drop shadow oscura (ya en outer).
            PillGlow.Color = (Color)ColorConverter.ConvertFromString("#FF3B30")!;
            PillGlow.Opacity = 0.55;
            PillGlow.BlurRadius = 32;
        }
    }

    private void FadeIn()
    {
        if (_isEntering) return;
        _isEntering = true;
        _isLeaving = false;

        var opacityAnim = new DoubleAnimation
        {
            From = RootGrid.Opacity,
            To = 1.0,
            Duration = EntryDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var translateAnim = new DoubleAnimation
        {
            From = RootTranslate.Y,
            To = 0,
            Duration = EntryDuration,
            // Overshoot sutil: cubic-bezier(.34, 1.4, .5, 1)
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 },
        };

        opacityAnim.Completed += (_, _) => _isEntering = false;
        RootGrid.BeginAnimation(OpacityProperty, opacityAnim);
        RootTranslate.BeginAnimation(TranslateTransform.YProperty, translateAnim);
    }

    private void FadeOut()
    {
        if (_isLeaving) return;
        _isLeaving = true;
        _isEntering = false;

        var opacityAnim = new DoubleAnimation
        {
            From = RootGrid.Opacity,
            To = 0,
            Duration = LeaveDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        var translateAnim = new DoubleAnimation
        {
            From = RootTranslate.Y,
            To = 140,
            Duration = LeaveDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };

        opacityAnim.Completed += (_, _) => _isLeaving = false;
        RootGrid.BeginAnimation(OpacityProperty, opacityAnim);
        RootTranslate.BeginAnimation(TranslateTransform.YProperty, translateAnim);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.VisualModeChanged -= OnVisualModeChanged;
        _viewModel.RmsLevelChanged -= OnRmsLevelChanged;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
