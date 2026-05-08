using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace Spikit.Services.Autostart;

// Implementación de IAutostartService sobre HKCU\…\Run. Value name "Spikit", value es la
// ruta absoluta del ejecutable actual (Process.GetCurrentProcess().MainModule.FileName).
//
// Edge case: si el usuario mueve el .exe a otra carpeta entre toggles, la entrada queda
// stale. Lo aceptamos como limitación (los .exe self-contained de WPF no se mueven solos);
// si en producción aparece, se documenta en FAQ.
public sealed class RegistryAutostartService : IAutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Spikit";

    private readonly ILogger<RegistryAutostartService> _logger;

    public RegistryAutostartService(ILogger<RegistryAutostartService> logger)
    {
        _logger = logger;
    }

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            if (key is null) return false;
            var value = key.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo leer la entrada de autostart en el registry");
            return false;
        }
    }

    public void Enable()
    {
        var exePath = GetCurrentExecutablePath();
        if (string.IsNullOrWhiteSpace(exePath))
        {
            _logger.LogWarning("Autostart Enable: no se pudo resolver la ruta del .exe actual");
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            // Comillas alrededor del path por si está en una carpeta con espacios.
            key.SetValue(ValueName, $"\"{exePath}\"");
            _logger.LogInformation("Autostart habilitado → {Path}", exePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo escribir la entrada de autostart en el registry");
            throw;
        }
    }

    public void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is null) return;
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            _logger.LogInformation("Autostart deshabilitado");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo borrar la entrada de autostart del registry");
            throw;
        }
    }

    private static string? GetCurrentExecutablePath()
    {
        // En .NET 8 sobre WPF self-contained, MainModule.FileName apunta al .exe wrapper
        // (no al dotnet host) que es lo que queremos persistir en HKCU\Run.
        var path = Process.GetCurrentProcess().MainModule?.FileName;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }
}
