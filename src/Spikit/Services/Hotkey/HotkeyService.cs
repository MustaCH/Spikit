using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Spikit.Models;
using Spikit.Native;

namespace Spikit.Services.Hotkey;

public sealed class HotkeyService : IHotkeyService
{
    private const int HotkeyId = 0xB001;
    private const int CancelHotkeyId = 0xB002;
    private static readonly TimeSpan ReleasePollInterval = TimeSpan.FromMilliseconds(30);

    private readonly ILogger<HotkeyService> _logger;
    private readonly Dispatcher _dispatcher;

    private HwndSource? _sink;
    private HwndSourceHook? _hook;
    private DispatcherTimer? _releaseTimer;
    private HotkeyDefinition? _registered;
    private HotkeyDefinition? _suspendedRegistration;
    private bool _cancelHotkeyRegistered;
    private bool _isPressed;
    private bool _isPaused;
    private bool _disposed;

    public event EventHandler? HotkeyPressed;
    public event EventHandler? HotkeyReleased;
    public event EventHandler? CancelHotkeyPressed;
    public event EventHandler? PausedChanged;

    public HotkeyDefinition? CurrentRegistration => _registered;

    public bool IsPaused => _isPaused;

    public void SetPaused(bool paused)
    {
        if (_isPaused == paused) return;
        _isPaused = paused;
        _logger.LogInformation("Hotkey {State}", paused ? "pausado" : "reanudado");
        PausedChanged?.Invoke(this, EventArgs.Empty);
    }

    public void TriggerManualPress()
    {
        if (_isPressed || _isPaused) return;
        _isPressed = true;
        _logger.LogDebug("Hotkey press manual disparado (tray menu)");
        HotkeyPressed?.Invoke(this, EventArgs.Empty);
        // Importante: NO StartReleasePolling — el caller (tray) solo invoca esto en Toggle.
        // En Toggle el orchestrator no espera HotkeyReleased; la sesión se cierra al
        // siguiente press (manual o físico).
        _isPressed = false;
    }

    public HotkeyService(ILogger<HotkeyService> logger)
    {
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public void Register(HotkeyDefinition definition)
    {
        EnsureNotDisposed();

        if (_registered is not null)
        {
            Unregister();
        }

        EnsureSink();

        var modifiers = (uint)(definition.Modifiers | HotkeyModifiers.NoRepeat);
        if (!User32.RegisterHotKey(_sink!.Handle, HotkeyId, modifiers, definition.VirtualKey))
        {
            var error = Marshal.GetLastWin32Error();
            throw new HotkeyRegistrationException(
                $"No se pudo registrar el hotkey '{definition}'. Probablemente lo tomó otra app. (Win32 error {error})");
        }

        _registered = definition;
        _logger.LogInformation("Hotkey registrado: {Hotkey}", definition);
    }

    public void Unregister()
    {
        if (_registered is null || _sink is null) return;

        StopReleasePolling();
        User32.UnregisterHotKey(_sink.Handle, HotkeyId);
        _logger.LogInformation("Hotkey liberado: {Hotkey}", _registered);
        _registered = null;
        _isPressed = false;
    }

    public void RegisterCancelHotkey()
    {
        EnsureNotDisposed();
        if (_cancelHotkeyRegistered) return;

        EnsureSink();

        // Esc puro (sin modificadores). MOD_NOREPEAT evita que mantener Esc apretado dispare
        // múltiples eventos. Si otra app tiene Esc reservado a nivel global (raro), Win32
        // lo rechaza y lo logueamos warning sin propagar — el cancel queda no disponible
        // para esa sesión, pero el dictado sigue funcional.
        var modifiers = (uint)HotkeyModifiers.NoRepeat;
        if (!User32.RegisterHotKey(_sink!.Handle, CancelHotkeyId, modifiers, VirtualKeys.Escape))
        {
            var error = Marshal.GetLastWin32Error();
            _logger.LogWarning("RegisterHotKey de Esc cancel falló (Win32 error {Error}). Sesión sin cancel global.", error);
            return;
        }

        _cancelHotkeyRegistered = true;
        _logger.LogDebug("Cancel hotkey (Esc) registrado");
    }

    public void UnregisterCancelHotkey()
    {
        if (!_cancelHotkeyRegistered || _sink is null) return;

        User32.UnregisterHotKey(_sink.Handle, CancelHotkeyId);
        _cancelHotkeyRegistered = false;
        _logger.LogDebug("Cancel hotkey (Esc) liberado");
    }

    public void SuspendForCapture()
    {
        EnsureNotDisposed();
        // Idempotente: si ya estamos suspendidos, no pisamos la referencia previa.
        if (_suspendedRegistration is not null) return;
        if (_registered is null) return;

        _suspendedRegistration = _registered;
        Unregister();
        _logger.LogDebug("Hotkey suspendido para captura: {Hotkey}", _suspendedRegistration);
    }

    public void ResumeFromCapture()
    {
        if (_suspendedRegistration is null) return;
        var def = _suspendedRegistration;
        _suspendedRegistration = null;

        try
        {
            Register(def);
            _logger.LogDebug("Hotkey re-registrado tras captura: {Hotkey}", def);
        }
        catch (HotkeyRegistrationException ex)
        {
            // Caso raro: otra app tomó la combinación durante el capture. Logueamos warning
            // y seguimos — la sesión queda sin hotkey activo, el usuario va a tener que
            // cambiarlo desde Settings. No queremos romper el flow del usuario que está en
            // Settings tipeando.
            _logger.LogWarning(ex, "No se pudo re-registrar hotkey tras captura ({Hotkey})", def);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterCancelHotkey();
        Unregister();

        if (_sink is not null)
        {
            if (_hook is not null) _sink.RemoveHook(_hook);
            _sink.Dispose();
            _sink = null;
            _hook = null;
        }
    }

    private void EnsureSink()
    {
        if (_sink is not null) return;

        // Message-only window: no recibe input ni pinta nada, solo procesa mensajes.
        // Vive desacoplada de cualquier window visible para que el hotkey siga funcionando
        // independientemente de que la pill o el SettingsWindow estén abiertos o no.
        var parameters = new HwndSourceParameters("SpikitHotkeySink")
        {
            Width = 0,
            Height = 0,
            ParentWindow = SpecialWindowHandles.HWND_MESSAGE,
        };
        _sink = new HwndSource(parameters);
        _hook = WndProc;
        _sink.AddHook(_hook);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WindowMessages.WM_HOTKEY) return IntPtr.Zero;

        var id = wParam.ToInt32();

        // Cancel hotkey (Esc): solo registrado durante estados cancelables. La pausa NO
        // bloquea el cancel — si el usuario pausó pero hay un dictado en curso, Esc tiene
        // que poder cortarlo igual.
        if (id == CancelHotkeyId)
        {
            handled = true;
            CancelHotkeyPressed?.Invoke(this, EventArgs.Empty);
            return IntPtr.Zero;
        }

        if (id != HotkeyId) return IntPtr.Zero;

        handled = true;

        if (_isPressed)
        {
            // NoRepeat debería evitar esto, pero por seguridad ignoramos doble-press.
            return IntPtr.Zero;
        }

        // Pausa via tray menu: cortocircuita el evento sin desregistrar la combinación.
        // Logueamos a debug porque el press es esperado (el OS sigue mandando WM_HOTKEY)
        // — solo lo silenciamos a nivel app.
        if (_isPaused)
        {
            _logger.LogDebug("Hotkey press ignorado: app pausada");
            return IntPtr.Zero;
        }

        _isPressed = true;
        HotkeyPressed?.Invoke(this, EventArgs.Empty);
        StartReleasePolling();
        return IntPtr.Zero;
    }

    private void StartReleasePolling()
    {
        // RegisterHotKey no emite WM_KEYUP. Polleamos GetAsyncKeyState durante la sesión
        // activa: es ~30 ticks/seg, CPU despreciable, y solo corre mientras hay press.
        StopReleasePolling();

        _releaseTimer = new DispatcherTimer(DispatcherPriority.Input, _dispatcher)
        {
            Interval = ReleasePollInterval,
        };
        _releaseTimer.Tick += OnReleasePoll;
        _releaseTimer.Start();
    }

    private void StopReleasePolling()
    {
        if (_releaseTimer is null) return;
        _releaseTimer.Stop();
        _releaseTimer.Tick -= OnReleasePoll;
        _releaseTimer = null;
    }

    private void OnReleasePoll(object? sender, EventArgs e)
    {
        if (_registered is null) return;
        if (AnyKeyReleased(_registered))
        {
            _isPressed = false;
            StopReleasePolling();
            HotkeyReleased?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool AnyKeyReleased(HotkeyDefinition def)
    {
        if (def.Modifiers.HasFlag(HotkeyModifiers.Control) && !IsKeyDown(VirtualKeys.Control)) return true;
        if (def.Modifiers.HasFlag(HotkeyModifiers.Alt) && !IsKeyDown(VirtualKeys.Alt)) return true;
        if (def.Modifiers.HasFlag(HotkeyModifiers.Shift) && !IsKeyDown(VirtualKeys.Shift)) return true;
        if (def.Modifiers.HasFlag(HotkeyModifiers.Win)
            && !IsKeyDown(VirtualKeys.LWin) && !IsKeyDown(VirtualKeys.RWin)) return true;
        return !IsKeyDown(def.VirtualKey);
    }

    private static bool IsKeyDown(uint vk) => (User32.GetAsyncKeyState((int)vk) & 0x8000) != 0;

    private void EnsureNotDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(HotkeyService));
    }
}
