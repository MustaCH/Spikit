using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Spikit.Views.Controls;

public enum LogoWaveMode
{
    Idle,
    Initializing,
    Recording,
    Transcribing,
    Logo,
}

// Render del símbolo de Spikit como waveform vivo. Specs en docs/design-system.md §10.2.
// 3 barras animadas según el modo:
//   Initializing → 3 dots gris con shimmer escalonado.
//   Recording    → 3 barras rojas oscilando con RMS (curva s1*s2*energy*center).
//   Transcribing → 3 barras con animación chase (pulso extremo→centro→extremo).
//   Logo         → 3 barras estáticas en proporciones 0.40/0.70/1.00 + chevron pegado.
public partial class LogoWave : UserControl
{
    private const double TickIntervalMs = 60.0;
    private const double FullHeight = 22.0;
    private const double MinBarHeight = 3.5;
    private static readonly Color BrandSolid = (Color)ColorConverter.ConvertFromString("#FF3B30")!;
    private static readonly Color InitDotColor = Color.FromArgb(0x6B, 0xFF, 0xFF, 0xFF); // ~0.42 alpha

    private readonly Rectangle[] _bars;
    private readonly DispatcherTimer _timer;
    private double _phase;
    private LogoWaveMode _mode = LogoWaveMode.Idle;
    private float _rmsLevel;

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(LogoWaveMode), typeof(LogoWave),
        new PropertyMetadata(LogoWaveMode.Idle, OnModeChanged));

    public static readonly DependencyProperty RmsLevelProperty = DependencyProperty.Register(
        nameof(RmsLevel), typeof(float), typeof(LogoWave),
        new PropertyMetadata(0f, OnRmsLevelChanged));

    public LogoWaveMode Mode
    {
        get => (LogoWaveMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    public float RmsLevel
    {
        get => (float)GetValue(RmsLevelProperty);
        set => SetValue(RmsLevelProperty, value);
    }

    public LogoWave()
    {
        InitializeComponent();
        _bars = new[] { Bar0, Bar1, Bar2 };
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(TickIntervalMs),
        };
        _timer.Tick += OnTick;

        Loaded += (_, _) => ApplyMode();
        Unloaded += (_, _) => _timer.Stop();
    }

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LogoWave lw)
        {
            lw._mode = (LogoWaveMode)e.NewValue;
            lw.ApplyMode();
        }
    }

    private static void OnRmsLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LogoWave lw)
        {
            lw._rmsLevel = (float)e.NewValue;
        }
    }

    private void ApplyMode()
    {
        switch (_mode)
        {
            case LogoWaveMode.Idle:
                _timer.Stop();
                Chevron.Visibility = Visibility.Collapsed;
                SetBarsColor(BrandSolid);
                ResetHeightsToMin();
                break;

            case LogoWaveMode.Initializing:
                Chevron.Visibility = Visibility.Collapsed;
                SetBarsColor(InitDotColor);
                ApplyInitializingDots();
                _phase = 0;
                _timer.Start();
                break;

            case LogoWaveMode.Recording:
            case LogoWaveMode.Transcribing:
                Chevron.Visibility = Visibility.Collapsed;
                SetBarsColor(BrandSolid);
                _phase = 0;
                _timer.Start();
                break;

            case LogoWaveMode.Logo:
                _timer.Stop();
                Chevron.Visibility = Visibility.Visible;
                SetBarsColor(BrandSolid);
                // Proporciones canónicas del logo: 0.40 / 0.70 / 1.00, centradas verticalmente.
                // El símbolo es 3 palitos redondeados de longitud creciente, no escalera bottom-aligned.
                _bars[0].Height = FullHeight * 0.40;
                _bars[1].Height = FullHeight * 0.70;
                _bars[2].Height = FullHeight * 1.00;
                _bars[0].Opacity = 1.0;
                _bars[1].Opacity = 1.0;
                _bars[2].Opacity = 1.0;
                break;
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        switch (_mode)
        {
            case LogoWaveMode.Initializing:
                AnimateInitShimmer();
                break;
            case LogoWaveMode.Recording:
                AnimateRecording();
                break;
            case LogoWaveMode.Transcribing:
                AnimateTranscribing();
                break;
        }
    }

    private void ApplyInitializingDots()
    {
        // En Initializing las "barras" son dots: altura ≈ width (cuadrados con radio).
        var dotHeight = Math.Max(MinBarHeight, FullHeight * 0.32);
        for (int i = 0; i < _bars.Length; i++)
        {
            _bars[i].Height = dotHeight;
            _bars[i].Opacity = 0.7;
        }
    }

    private void AnimateInitShimmer()
    {
        // Stagger de 0.15s entre dots, ciclo 1.1s, opacity 0.35 ↔ 1, scaleY 0.85 ↔ 1.15.
        _phase += 0.34; // ~5.6 rad/s = 1.1s ciclo a 60ms tick
        var dotHeight = Math.Max(MinBarHeight, FullHeight * 0.32);
        for (int i = 0; i < _bars.Length; i++)
        {
            var t = _phase + i * 0.94; // stagger 0.15s = 0.94 rad a 6.28 rad/ciclo
            var sin = Math.Sin(t);
            var opacityNorm = (sin + 1) / 2; // 0..1
            var opacity = 0.35 + opacityNorm * 0.65;
            var scaleY = 0.85 + opacityNorm * 0.30;

            _bars[i].Opacity = opacity;
            _bars[i].Height = dotHeight * scaleY;
        }
    }

    private void AnimateRecording()
    {
        _phase += 0.28;

        // Movimiento base SIEMPRE presente para que la pill "respire" aunque el usuario
        // esté en silencio. El RMS amplifica adicionalmente, no reemplaza el baseline.
        var baseEnergy = 0.40 + 0.20 * Math.Sin(_phase * 0.5); // 0.20-0.60 ciclo lento
        var rmsBoost = Math.Min(0.40, _rmsLevel * 6.0);        // hasta +0.40 con voz
        var energy = Math.Min(1.0, baseEnergy + rmsBoost);

        for (int i = 0; i < _bars.Length; i++)
        {
            var oscillation = Math.Sin(_phase + i * 1.5) * 0.5 + 0.5; // 0..1
            // Center modula menos: baseline más alto, swing más chico (spec §10.2).
            var baseline = i == 1 ? 0.40 : 0.15;
            var swing = i == 1 ? 0.45 : 0.85;
            var level = Math.Clamp(baseline + oscillation * energy * swing, 0.05, 1.0);

            _bars[i].Height = MinBarHeight + (FullHeight - MinBarHeight) * level;
            _bars[i].Opacity = 1.0;
        }
    }

    private void AnimateTranscribing()
    {
        _phase += 0.28;
        var cycle = Math.Sin(_phase * 0.9) * 0.5 + 0.5;
        var pulsePos = 1 - cycle;

        for (int i = 0; i < _bars.Length; i++)
        {
            var distFromCenter = Math.Abs(i - 1);
            var d = Math.Abs(distFromCenter - pulsePos);
            var pulse = Math.Exp(-d * d * 1.6);
            var level = 0.2 + pulse * 0.8;

            _bars[i].Height = MinBarHeight + (FullHeight - MinBarHeight) * level;
            _bars[i].Opacity = 1.0;
        }
    }

    private void ResetHeightsToMin()
    {
        for (int i = 0; i < _bars.Length; i++)
        {
            _bars[i].Height = MinBarHeight;
            _bars[i].Opacity = 1.0;
        }
    }

    private void SetBarsColor(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        for (int i = 0; i < _bars.Length; i++)
        {
            _bars[i].Fill = brush;
        }
    }
}
