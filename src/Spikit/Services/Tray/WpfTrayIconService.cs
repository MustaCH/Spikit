using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using H.NotifyIcon;
using Microsoft.Extensions.Logging;
using Spikit.Models;
using Spikit.Services.Hotkey;
using Spikit.Services.Orchestration;
using Spikit.Services.Settings;

namespace Spikit.Services.Tray;

// Tray icon de Spikit. Construido programáticamente sobre H.NotifyIcon.Wpf.
//
// Click izquierdo = abrir Settings (atajo más usado).
// Click derecho   = ContextMenu con header (info), Iniciar dictado, Pausar app, Settings, Salir.
//
// Refresh del tooltip + header del menú: vía SettingsChanged del ISettingsService (cualquier
// Save de Provider/Hotkey lo dispara — los config writers ya van a través de Save) y vía
// PausedChanged del IHotkeyService (toggle del menu). Si el modo PTT/Toggle cambia, también
// se refresca porque ese cambio pasa por settings.json y dispara SettingsChanged.
//
// Limitación conocida del item "Iniciar dictado":
//   - En modo Toggle: enabled, dispara TriggerManualPress() y el orchestrator arranca/cierra.
//   - En modo PushToTalk: deshabilitado, porque no se puede "mantener apretado" desde un menu
//     (el polling de release detectaría release inmediato y cerraría la sesión al instante).
//     El item igual muestra el hotkey display al lado, sirviendo de recordatorio (heurística #6).
internal sealed class WpfTrayIconService : ITrayIconService
{
    private readonly IHotkeyService _hotkey;
    private readonly DictationOrchestrator _orchestrator;
    private readonly ISettingsService _settings;
    private readonly ISettingsWindowPresenter _settingsPresenter;
    private readonly ILogger<WpfTrayIconService> _logger;
    private readonly Dispatcher _dispatcher;

    private TaskbarIcon? _icon;
    private Icon? _nativeIcon;
    private IntPtr _hicon;
    private MenuItem? _headerItem;
    private MenuItem? _startItem;
    private MenuItem? _pauseItem;
    private MenuItem? _settingsItem;
    private MenuItem? _exitItem;
    private bool _disposed;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public WpfTrayIconService(
        IHotkeyService hotkey,
        DictationOrchestrator orchestrator,
        ISettingsService settings,
        ISettingsWindowPresenter settingsPresenter,
        ILogger<WpfTrayIconService> logger)
    {
        _hotkey = hotkey;
        _orchestrator = orchestrator;
        _settings = settings;
        _settingsPresenter = settingsPresenter;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public void Initialize()
    {
        _dispatcher.BeginInvoke(InitializeOnUiThread);
    }

    private void InitializeOnUiThread()
    {
        if (_disposed) return;
        if (_icon is not null) return;

        try
        {
            // System.Drawing.Icon nativo desde el PNG: H.NotifyIcon's `IconSource` (ImageSource)
            // hacía la conversión a HICON internamente y producía un icono transparente en
            // Win11 (slot clickeable pero invisible). Cargar el PNG → Bitmap → GetHicon() →
            // Icon.FromHandle() da un icon real con alpha que el shell sí renderiza.
            _nativeIcon = LoadNativeIcon();

            _icon = new TaskbarIcon
            {
                Icon = _nativeIcon,
                ToolTipText = BuildTooltip(),
                ContextMenu = BuildContextMenu(),
                NoLeftClickDelay = true,
            };

            _icon.TrayLeftMouseDown += OnLeftClick;

            _settings.SettingsChanged += OnSettingsChanged;
            _hotkey.PausedChanged += OnPausedChanged;
            _orchestrator.StateChanged += OnOrchestratorStateChanged;

            _icon.ForceCreate();
            RefreshMenuState();
            _logger.LogInformation("TrayIcon inicializado (icon nativo {Width}x{Height})",
                _nativeIcon.Width, _nativeIcon.Height);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inicializando TrayIcon");
        }
    }

    private Icon LoadNativeIcon()
    {
        // Cargar el PNG embedido como resource, escalar a 32x32 (tamaño que el shell de Win11
        // pide a 100% DPI para la barra principal — DPI mayores le piden tamaños mayores
        // pero 32x32 escala razonable). Bitmap.GetHicon() devuelve un HICON nuevo que hay
        // que destruir manualmente con DestroyIcon en Dispose para no leak.
        var resourceStream = Application.GetResourceStream(
            new Uri("pack://application:,,,/Resources/Brand/logo_rojo.png"))
            ?? throw new InvalidOperationException("logo_rojo.png no se encontró como resource");

        using var stream = resourceStream.Stream;
        using var source = new Bitmap(stream);
        using var resized = new Bitmap(source, new System.Drawing.Size(32, 32));

        _hicon = resized.GetHicon();
        return Icon.FromHandle(_hicon);
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        // Header info: "○ Spikit · {preset} · Lifetime access". Disabled — solo decorativo.
        // El bullet inicial cambia según pause state (◯ pausado, • activo) — RefreshMenuState
        // lo actualiza.
        _headerItem = new MenuItem
        {
            IsEnabled = false,
            Header = BuildHeaderText(),
        };
        menu.Items.Add(_headerItem);
        menu.Items.Add(new Separator());

        // ▶ Iniciar dictado · {hotkey}. Enabled solo en modo Toggle (ver class doc).
        _startItem = new MenuItem
        {
            Header = BuildStartText(),
        };
        _startItem.Click += OnStartClick;
        menu.Items.Add(_startItem);

        // ⏸ Pausar app / ▶ Reanudar app. Toggle.
        _pauseItem = new MenuItem
        {
            Header = BuildPauseText(),
        };
        _pauseItem.Click += OnPauseToggleClick;
        menu.Items.Add(_pauseItem);

        menu.Items.Add(new Separator());

        // ⚙ Abrir Settings — equivalente al click izquierdo.
        _settingsItem = new MenuItem { Header = "⚙  Abrir Settings" };
        _settingsItem.Click += OnOpenSettingsClick;
        menu.Items.Add(_settingsItem);

        menu.Items.Add(new Separator());

        // ✕ Salir — Application.Shutdown().
        _exitItem = new MenuItem { Header = "✕  Salir" };
        _exitItem.Click += OnExitClick;
        menu.Items.Add(_exitItem);

        return menu;
    }

    private string BuildTooltip()
    {
        if (_hotkey.IsPaused) return "Spikit pausado";
        var preset = ProviderPresetDefaults.DisplayName(ParsePresetId(_settings.Load().Provider.PresetId));
        return $"Spikit · {preset} · Lifetime access";
    }

    private string BuildHeaderText()
    {
        var bullet = _hotkey.IsPaused ? "◯" : "●";
        var preset = ProviderPresetDefaults.DisplayName(ParsePresetId(_settings.Load().Provider.PresetId));
        return $"{bullet}  Spikit · {preset} · Lifetime access";
    }

    private string BuildStartText()
    {
        // Hotkey display tomado del registered si existe, o del settings si todavía no se
        // registró (escenario raro pero posible en bootstrap temprano).
        var hotkey = _hotkey.CurrentRegistration?.ToString()
                     ?? (_settings.Load().Hotkey.TryToRuntime(out var def, out _) ? def.ToString() : "—");
        return $"▶  Iniciar dictado    {hotkey}";
    }

    private string BuildPauseText()
    {
        return _hotkey.IsPaused
            ? "▶  Reanudar app"
            : "⏸  Pausar app";
    }

    private static ProviderPreset ParsePresetId(string presetId) => presetId switch
    {
        "openai" => ProviderPreset.OpenAI,
        "groq" => ProviderPreset.Groq,
        "custom" => ProviderPreset.Custom,
        _ => ProviderPreset.OpenAI,
    };

    private void RefreshMenuState()
    {
        if (_icon is null) return;

        if (_headerItem is not null) _headerItem.Header = BuildHeaderText();
        if (_startItem is not null)
        {
            _startItem.Header = BuildStartText();
            // En modo PTT no podemos simular un "hold" desde el menu — disabled. En Toggle
            // el item dispara el press manual via IHotkeyService.TriggerManualPress().
            // También bloqueamos si el hotkey está pausado (la pausa cortocircuita el flow).
            _startItem.IsEnabled = !_hotkey.IsPaused
                                   && _orchestrator.Mode == HotkeyMode.Toggle
                                   && _orchestrator.State == DictationState.Idle;
        }
        if (_pauseItem is not null) _pauseItem.Header = BuildPauseText();

        _icon.ToolTipText = BuildTooltip();
    }

    // ============ Event handlers ============

    private void OnLeftClick(object? sender, RoutedEventArgs e)
    {
        _settingsPresenter.Open();
    }

    private void OnStartClick(object? sender, RoutedEventArgs e)
    {
        if (_hotkey.IsPaused) return;
        if (_orchestrator.Mode != HotkeyMode.Toggle) return;
        _hotkey.TriggerManualPress();
    }

    private void OnPauseToggleClick(object? sender, RoutedEventArgs e)
    {
        _hotkey.SetPaused(!_hotkey.IsPaused);
    }

    private void OnOpenSettingsClick(object? sender, RoutedEventArgs e)
    {
        _settingsPresenter.Open();
    }

    private void OnExitClick(object? sender, RoutedEventArgs e)
    {
        _logger.LogInformation("Salir solicitado desde tray");
        Application.Current?.Shutdown();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // Siempre marshalear: Save() puede llegar desde un thread no-UI (HttpClient callback,
        // file watcher futuro, etc.).
        _dispatcher.BeginInvoke(RefreshMenuState);
    }

    private void OnPausedChanged(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(RefreshMenuState);
    }

    private void OnOrchestratorStateChanged(object? sender, DictationState state)
    {
        // El item "Iniciar dictado" se deshabilita mientras hay sesión activa (Recording,
        // Transcribing, etc.) — el rebuild del IsEnabled del item tiene que correr en UI thread.
        _dispatcher.BeginInvoke(RefreshMenuState);
    }

    public void Shutdown()
    {
        // EP-11.7: cleanup transitorio para el logout flow — libera el icono + recursos
        // nativos pero NO marca _disposed = true. Un Initialize posterior re-arma todo.
        // Idempotente (re-llamar tras Shutdown previo es no-op porque _icon ya es null).
        if (_icon is null && _nativeIcon is null && _hicon == IntPtr.Zero) return;

        _dispatcher.BeginInvoke(ShutdownOnUiThread);
    }

    private void ShutdownOnUiThread()
    {
        _logger.LogInformation("TrayIcon shutdown (logout)");
        ReleaseNativeResources();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _logger.LogInformation("TrayIcon disposing");
        ReleaseNativeResources();
    }

    // Cleanup compartido entre Shutdown (transitorio) y Dispose (terminal). Desuscribe
    // events + libera tray + native icon + HICON handle. Idempotente.
    private void ReleaseNativeResources()
    {
        _settings.SettingsChanged -= OnSettingsChanged;
        _hotkey.PausedChanged -= OnPausedChanged;
        _orchestrator.StateChanged -= OnOrchestratorStateChanged;

        if (_icon is not null)
        {
            _icon.TrayLeftMouseDown -= OnLeftClick;
            _icon.Dispose();
            _icon = null;
        }

        _nativeIcon?.Dispose();
        _nativeIcon = null;

        if (_hicon != IntPtr.Zero)
        {
            DestroyIcon(_hicon);
            _hicon = IntPtr.Zero;
        }
    }
}
