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
    private static readonly TimeSpan ReleasePollInterval = TimeSpan.FromMilliseconds(30);

    private readonly ILogger<HotkeyService> _logger;
    private readonly Dispatcher _dispatcher;

    private HwndSource? _sink;
    private HwndSourceHook? _hook;
    private DispatcherTimer? _releaseTimer;
    private HotkeyDefinition? _registered;
    private bool _isPressed;
    private bool _disposed;

    public event EventHandler? HotkeyPressed;
    public event EventHandler? HotkeyReleased;

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

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
        // Vive desacoplada de MainWindow para que el hotkey siga funcionando aunque
        // la UI principal se cierre/oculte (pill flotante es la cara visible de la app).
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
        if (msg != WindowMessages.WM_HOTKEY || wParam.ToInt32() != HotkeyId) return IntPtr.Zero;

        handled = true;

        if (_isPressed)
        {
            // NoRepeat debería evitar esto, pero por seguridad ignoramos doble-press.
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
