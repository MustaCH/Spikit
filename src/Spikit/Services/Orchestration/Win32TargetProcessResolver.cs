using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Spikit.Native;

namespace Spikit.Services.Orchestration;

// Implementación productiva del ITargetProcessResolver. User32.GetWindowThreadProcessId
// devuelve el PID dueño del HWND; Process.GetProcessById nos da el ProcessName (que en
// Win32 NO incluye la extensión). Concatenamos ".exe" porque el wireframe de flows.md
// muestra explícitamente "cursor.exe" / "Code.exe" — coherencia visual con lo que el
// usuario ve en Task Manager.
//
// Cualquier fallo (HWND ya inválido, proceso muerto entre captura y resolve, etc.) se
// trata como ausencia: devolvemos string.Empty y logueamos. El historial muestra
// "(desconocido)" en esos casos.
public sealed class Win32TargetProcessResolver : ITargetProcessResolver
{
    private readonly ILogger<Win32TargetProcessResolver> _logger;

    public Win32TargetProcessResolver(ILogger<Win32TargetProcessResolver> logger)
    {
        _logger = logger;
    }

    public string Resolve(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;

        try
        {
            User32.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return string.Empty;

            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            if (string.IsNullOrEmpty(name)) return string.Empty;

            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name
                : name + ".exe";
        }
        catch (ArgumentException)
        {
            // PID válido pero el proceso ya no existe (cerró entre captura y resolve).
            return string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Resolve target process falló para HWND 0x{Hwnd:X}", hwnd.ToInt64());
            return string.Empty;
        }
    }
}
