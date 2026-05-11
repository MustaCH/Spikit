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
    // Cross-fade dots → bars al pasar de Initializing a Recording (§10.1 tabla de transiciones).
    // 80ms fade-out + 80ms fade-in = 160ms total, swap del modo en el medio.
    private static readonly TimeSpan CrossFadeHalfDuration = TimeSpan.FromMilliseconds(80);
    // Morph Transcribing → Logo: scale 0.85→1 + opacity hold (las barras vienen visibles).
    // §10.1 pide cubic-bezier(.2,.7,.2,1) que en WPF aproximamos con QuarticEase EaseOut.
    private static readonly TimeSpan MorphDuration = TimeSpan.FromMilliseconds(420);

    private readonly DictationPillViewModel _viewModel;
    private readonly IPillPositionService _positionService;
    private readonly ISettingsService _settingsService;
    private PillVisualMode _previousMode = PillVisualMode.Hidden;
    private ScaleTransform? _logoWaveScale;
    private bool _isEntering;
    private bool _isLeaving;

    // Reduced-motion (`prefers-reduced-motion` equivalente WPF). Si está activo, los cross-fade
    // y la slide-in se reemplazan por swap inmediato; los morphs internos del LogoWave
    // (animaciones tick a 60ms) los maneja el control mismo, no la pill.
    private static bool ReducedMotion => !SystemParameters.ClientAreaAnimation;

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

        // Refuerzo explícito del z-order topmost. El Topmost=True del XAML lo intenta vía
        // WPF, pero cuando la app arranca por autostart (HKCU\...\Run) antes de que el
        // DWM/shell estén estables, el WS_EX_TOPMOST se setea pero el z-order interno
        // queda degradado y la pill cae al desktop layer. Llamar SetWindowPos directo
        // sincroniza el HWND con el z-order TOPMOST sin depender del bootstrap de WPF.
        ForceTopmost(hwnd);

        // Sobre Acrylic en la pill (decisión EP-6.4 — design-system §10.1):
        // El ticket pedía Acrylic vía DwmHelper.ApplyBackdrop(this, DwmSystemBackdropType.Transient).
        // No es viable: la pill usa AllowsTransparency=True para el shadow externo + slide-in
        // animation, y eso elimina el "system frame" del DWM que necesita SystemBackdropType.
        // El atributo se aplicaría sin error pero DWM lo ignora y no se renderiza nada.
        //
        // Alternativas evaluadas (descartadas):
        //   1. Sacar AllowsTransparency=True → rompe el DropShadowEffect externo y la slide-in.
        //   2. AccentPolicy (API legacy undocumented de Win10) → calidad visual menor.
        //
        // Decisión: la pill mantiene fallback solid #0A0A0A (PillBg). Visual aprobado, los
        // shadows + glow rojo siguen funcionando y el contraste sobre cualquier app es bueno.
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

    // State machine de transiciones visuales según prev → new (§10.1 tabla de transiciones).
    // El VM ya gestiona el timing entre estados (LogoFlashDuration 600ms, LeaveDuration 420ms);
    // acá solo decidimos qué animación corre en la window al recibir cada evento.
    private void ApplyVisualMode(PillVisualMode newMode, bool animate)
    {
        var prev = _previousMode;
        _previousMode = newMode;
        if (newMode == prev) return;

        var instant = !animate || ReducedMotion;

        // hidden → cualquier estado visible: slide-in 520ms (FadeIn).
        if (prev == PillVisualMode.Hidden)
        {
            // Si la app arrancó por autostart y el HWND quedó con z-order degradado
            // (ver OnSourceInitialized), este momento es el primer hotkey post-boot — el
            // DWM ya estabilizó. Re-aplicar HWND_TOPMOST rescata el z-order sin que el
            // usuario note nada (ocurre antes del fade-in).
            ForceTopmost(new WindowInteropHelper(this).Handle);

            ApplyBorderAndShadow(quiet: newMode == PillVisualMode.Initializing);
            LogoWaveControl.Mode = MapToLogoMode(newMode);
            ResetLogoWaveTransform();
            if (instant) { RootGrid.Opacity = 1; RootTranslate.Y = 0; }
            else FadeIn();
            return;
        }

        // cualquier estado visible → leaving o hidden: slide-out 420ms (FadeOut).
        if (newMode == PillVisualMode.Leaving || newMode == PillVisualMode.Hidden)
        {
            if (instant) { RootGrid.Opacity = 0; RootTranslate.Y = 140; LogoWaveControl.Mode = LogoWaveMode.Idle; }
            else FadeOut();
            return;
        }

        // transcribing → logo: morph 420ms (scale 0.85 → 1 con QuarticEase EaseOut).
        if (newMode == PillVisualMode.Logo)
        {
            ApplyBorderAndShadow(quiet: false);
            LogoWaveControl.Mode = LogoWaveMode.Logo;
            if (instant) ResetLogoWaveTransform();
            else MorphToLogo();
            return;
        }

        // initializing → recording: cross-fade dots → bars 160ms + border quiet → active.
        if (prev == PillVisualMode.Initializing && newMode == PillVisualMode.Recording)
        {
            if (instant)
            {
                ApplyBorderAndShadow(quiet: false);
                LogoWaveControl.Mode = LogoWaveMode.Recording;
            }
            else CrossFadeTo(LogoWaveMode.Recording, quietBorder: false);
            return;
        }

        // recording → transcribing: in-place change. El smoothing 80ms del LogoWave maneja
        // la transición de heights; solo cambiamos mode. Border/shadow no cambian.
        if (prev == PillVisualMode.Recording && newMode == PillVisualMode.Transcribing)
        {
            LogoWaveControl.Mode = LogoWaveMode.Transcribing;
            return;
        }

        // Cualquier otra transición visible→visible (ej. saltos no esperados de la state machine):
        // swap directo de border + mode, sin animación. Más seguro que asumir un cross-fade.
        ApplyBorderAndShadow(quiet: newMode == PillVisualMode.Initializing);
        LogoWaveControl.Mode = MapToLogoMode(newMode);
    }

    private static void ForceTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        User32.SetWindowPos(
            hwnd,
            User32.HWND_TOPMOST,
            0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
    }

    private static LogoWaveMode MapToLogoMode(PillVisualMode mode) => mode switch
    {
        PillVisualMode.Initializing => LogoWaveMode.Initializing,
        PillVisualMode.Recording => LogoWaveMode.Recording,
        PillVisualMode.Transcribing => LogoWaveMode.Transcribing,
        PillVisualMode.Logo => LogoWaveMode.Logo,
        _ => LogoWaveMode.Idle,
    };

    // Cross-fade en dos fases: opacity 1 → 0 (80ms) → swap mode + border → opacity 0 → 1 (80ms).
    // El swap visual ocurre en el punto de invisibilidad para que no se vea ningún glitch.
    private void CrossFadeTo(LogoWaveMode targetMode, bool quietBorder)
    {
        var fadeOut = new DoubleAnimation
        {
            From = LogoWaveControl.Opacity,
            To = 0,
            Duration = CrossFadeHalfDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        fadeOut.Completed += (_, _) =>
        {
            ApplyBorderAndShadow(quietBorder);
            LogoWaveControl.Mode = targetMode;
            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = CrossFadeHalfDuration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            LogoWaveControl.BeginAnimation(OpacityProperty, fadeIn);
        };
        LogoWaveControl.BeginAnimation(OpacityProperty, fadeOut);
    }

    // Morph al estado Logo: las barras ya están visibles (vienen de transcribing), así que
    // no hace falta cross-fade de opacity. Solo un scale 0.85 → 1 con easing morph para que
    // el cambio de heights del LogoWave (recording chase → logo silhouette) se sienta como
    // que la pill "respira hacia su forma final" en vez de un snap.
    private void MorphToLogo()
    {
        EnsureLogoWaveScale();
        LogoWaveControl.Opacity = 1;

        var easing = new QuarticEase { EasingMode = EasingMode.EaseOut };
        var scaleX = new DoubleAnimation { From = 0.85, To = 1, Duration = MorphDuration, EasingFunction = easing };
        var scaleY = new DoubleAnimation { From = 0.85, To = 1, Duration = MorphDuration, EasingFunction = easing };
        _logoWaveScale!.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        _logoWaveScale!.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
    }

    private void EnsureLogoWaveScale()
    {
        if (_logoWaveScale != null) return;
        _logoWaveScale = new ScaleTransform(1, 1);
        LogoWaveControl.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        LogoWaveControl.RenderTransform = _logoWaveScale;
    }

    // Limpieza del scale al volver a Hidden / iniciar una sesión nueva. Si quedó animado
    // a 1 (post-MorphToLogo) técnicamente no haría falta, pero preferimos defensivo.
    private void ResetLogoWaveTransform()
    {
        if (_logoWaveScale == null) return;
        _logoWaveScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _logoWaveScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _logoWaveScale.ScaleX = 1;
        _logoWaveScale.ScaleY = 1;
        LogoWaveControl.Opacity = 1;
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
