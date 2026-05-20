using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Spikit.Native;

namespace Spikit.Services.Insertion;

public sealed class ClipboardPasteService : ITextInsertionService
{
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private static readonly TimeSpan PostFocusDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan PostPasteDelay = TimeSpan.FromMilliseconds(500);

    private readonly ILogger<ClipboardPasteService> _logger;
    private readonly Dispatcher _dispatcher;

    public ClipboardPasteService(ILogger<ClipboardPasteService> logger)
    {
        _logger = logger;
        // El service se construye en el bootstrap del DI, que corre en STA UI thread.
        // Capturamos el dispatcher para marshalar todas las operaciones de Clipboard.
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public async Task<InsertionResult> InsertIntoForegroundAsync(string text, IntPtr targetHwnd)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        if (text.Length == 0) return InsertionResult.Pasted;

        if (!User32.IsWindow(targetHwnd))
        {
            _logger.LogWarning("Insertion target HWND {Hwnd} ya no existe", targetHwnd);
            return InsertionResult.TargetGone;
        }

        var saved = await GetClipboardSnapshotAsync().ConfigureAwait(true);
        if (!await TrySetClipboardTextAsync(text).ConfigureAwait(true))
        {
            return InsertionResult.Failed;
        }

        if (!User32.SetForegroundWindow(targetHwnd))
        {
            _logger.LogWarning(
                "SetForegroundWindow devolvió false para HWND {Hwnd} (foreground lock?)", targetHwnd);
        }
        await Task.Delay(PostFocusDelay).ConfigureAwait(true);

        var actualForeground = User32.GetForegroundWindow();
        if (actualForeground != targetHwnd)
        {
            _logger.LogWarning(
                "Foreground real {Actual} != target {Expected} — paste puede ir a otra app",
                actualForeground, targetHwnd);
        }

        try
        {
            SendCtrlV();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendInput Ctrl+V falló");
            await TryRestoreClipboardAsync(saved).ConfigureAwait(true);
            return InsertionResult.Failed;
        }

        await Task.Delay(PostPasteDelay).ConfigureAwait(true);
        await TryRestoreClipboardAsync(saved).ConfigureAwait(true);

        return InsertionResult.Pasted;
    }

    private async Task<ClipboardSnapshot> GetClipboardSnapshotAsync()
    {
        return await _dispatcher.InvokeAsync(() =>
        {
            try
            {
                return ClipboardSnapshot.FromDataObject(Clipboard.GetDataObject(), _logger);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "No se pudo capturar clipboard previo — la transcripción quedará residual " +
                    "(RN-4: preferimos eso a borrar contenido del usuario que no snapshoteamos)");
                return ClipboardSnapshot.Unknown;
            }
        });
    }

    private async Task<bool> TrySetClipboardTextAsync(string text)
    {
        return await _dispatcher.InvokeAsync(() =>
        {
            try
            {
                var data = new DataObject(DataFormats.UnicodeText, text);
                // Excluir la transcripción del Clipboard History (Win+V) y del Cloud
                // Clipboard. Es contenido transitorio que el usuario no copió
                // explícitamente — no debería ensuciar su historial entre lo que
                // realmente sí copió antes y después.
                data.SetData("ExcludeClipboardContentFromMonitorProcessing", new byte[] { 0 });
                data.SetData("CanIncludeInClipboardHistory", new byte[] { 0, 0, 0, 0 });
                data.SetData("CanUploadToCloudClipboard", new byte[] { 0, 0, 0, 0 });
                Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo escribir el texto al clipboard");
                return false;
            }
        });
    }

    private async Task TryRestoreClipboardAsync(ClipboardSnapshot saved)
    {
        await _dispatcher.InvokeAsync(() =>
        {
            try
            {
                switch (saved.State)
                {
                    case ClipboardSnapshot.Kind.HasContent:
                        // copy:true persiste los datos al system clipboard manager.
                        // Sin esto, WPF deja una referencia al DataObject que se
                        // invalida cuando este proceso libera el clipboard.
                        Clipboard.SetDataObject(saved.Data!, copy: true);
                        break;
                    case ClipboardSnapshot.Kind.Empty:
                        Clipboard.Clear();
                        break;
                    case ClipboardSnapshot.Kind.Unknown:
                        // Snapshot falló — dejamos el clipboard como está (con la transcripción).
                        // Borrarlo podría destruir datos del usuario que no pudimos capturar.
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Restore/clear del clipboard post-paste falló (estado {State}, RN-4: log + seguir)",
                    saved.State);
            }
        });
    }

    private static void SendCtrlV()
    {
        var inputs = new INPUT[]
        {
            MakeKeyInput(VK_CONTROL, KeyEventF.KeyDown),
            MakeKeyInput(VK_V, KeyEventF.KeyDown),
            MakeKeyInput(VK_V, KeyEventF.KeyUp),
            MakeKeyInput(VK_CONTROL, KeyEventF.KeyUp),
        };

        var sent = User32.SendInput((uint)inputs.Length, inputs, INPUT.Size);
        if (sent != inputs.Length)
        {
            var err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"SendInput despachó {sent}/{inputs.Length} eventos (Win32 error {err}). " +
                "Posible UIPI block (CB-13) si el target corre como admin y la app no.");
        }
    }

    private static INPUT MakeKeyInput(ushort vk, KeyEventF flags) => new()
    {
        type = InputType.Keyboard,
        U = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = IntPtr.Zero,
            },
        },
    };
}
