using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Spikit.Services.Velopack;

// EP-10.4 sub-task 3.1 — registro del protocol handler `spikit://`.
//
// Se invoca desde los hooks de Velopack en Program.Main (install/update/uninstall). Esos
// hooks corren con Environment.Exit() inmediatamente después y ANTES de que Serilog esté
// configurado, así que el logging propio va a un archivo dedicado en
// `%AppData%\Spikit\logs\velopack-hooks.log` con File.AppendAllText (no Serilog).
//
// Decisión: HKCU (no HKLM). Velopack instala per-user en %LocalAppData%\Spikit sin pedir
// UAC; escribir en HKLM nos forzaría a pedir elevación al instalador. La key per-user
// es suficiente porque la app se instala por usuario.
//
// Spec Windows protocol handler:
//   HKCU\Software\Classes\spikit\
//     (Default)             = "URL:Spikit Protocol"
//     URL Protocol          = ""           ← marker que Windows usa para reconocer scheme
//   HKCU\Software\Classes\spikit\DefaultIcon\
//     (Default)             = "<exe>,0"   ← primer icon del .exe (Spikit branding EP-8.2)
//   HKCU\Software\Classes\spikit\shell\open\command\
//     (Default)             = "\"<exe>\" \"%1\""  ← Windows reemplaza %1 con el URI
//
// Smoke test manual (con la app instalada):
//   start spikit://auth-callback?test=1
// Confirma que Spikit.exe arranca con argv[0] = "spikit://auth-callback?test=1" y el
// SpikitUriDispatcher (EP-10.4 / 3.3) lo procesa.
internal static class SpikitProtocolHandler
{
    // Scheme del protocol handler. Cambiarlo implica romper deep-links viejos — no tocar
    // sin coordinar con la página intermediaria `spikit.dev/auth-callback`.
    private const string Scheme = "spikit";

    // Path completo de la key dentro de HKCU.
    private const string RootKeyPath = @"Software\Classes\" + Scheme;
    private const string CommandKeyPath = RootKeyPath + @"\shell\open\command";
    private const string IconKeyPath = RootKeyPath + @"\DefaultIcon";

    // Texto que Windows muestra en el diálogo "Always use this app to open spikit:// links"
    // cuando hay múltiples handlers compitiendo. En la práctica solo Spikit registra el
    // scheme, así que el usuario nunca lo ve, pero la convención de Microsoft es marcarlo.
    private const string FriendlyName = "URL:Spikit Protocol";

    public static void Register()
    {
        var exePath = GetCurrentExecutablePath();
        if (string.IsNullOrWhiteSpace(exePath))
        {
            WriteHookLog("Register skipped: no se pudo resolver el path del .exe actual.");
            return;
        }

        try
        {
            using var root = Registry.CurrentUser.CreateSubKey(RootKeyPath, writable: true);
            // (Default) value del root = friendly name. Windows lo usa como display name.
            root.SetValue(string.Empty, FriendlyName, RegistryValueKind.String);
            // El marker "URL Protocol" (value vacío) es lo que le dice a Windows que este
            // ProgID representa un URL scheme, no un file association. Sin esto, el OS
            // ignora la key.
            root.SetValue("URL Protocol", string.Empty, RegistryValueKind.String);

            using var iconKey = Registry.CurrentUser.CreateSubKey(IconKeyPath, writable: true);
            // ",0" → primer icon embebido en el .exe. EP-8.2 metió el branding de Spikit
            // ahí; Windows lo muestra como icono del browser/dialog del deep-link.
            iconKey.SetValue(string.Empty, $"\"{exePath}\",0", RegistryValueKind.String);

            using var commandKey = Registry.CurrentUser.CreateSubKey(CommandKeyPath, writable: true);
            // Comillas alrededor del path (puede tener espacios) y de %1 (la URL completa
            // puede tener `&` y otros chars que un shell interpretaría). Windows reemplaza
            // %1 con la URL exacta — `spikit://auth-callback?access_token=...`.
            commandKey.SetValue(string.Empty, $"\"{exePath}\" \"%1\"", RegistryValueKind.String);

            WriteHookLog($"Register OK: {Scheme}:// → {exePath}");
        }
        catch (Exception ex)
        {
            // Tragamos la excepción: si la Registry falla durante install/update, no
            // queremos crashear el flow de Velopack (que rollbackearía el install). El
            // usuario va a poder usar la app sin login deep-link (workaround = ningún
            // workaround todavía; EP-10.0 va a meter UI manual de login en Settings).
            WriteHookLog($"Register FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(RootKeyPath, throwOnMissingSubKey: false);
            WriteHookLog($"Unregister OK: {Scheme}://");
        }
        catch (Exception ex)
        {
            // Mismo razonamiento que Register: silenciar fallos en uninstall (Velopack
            // está a punto de borrar la carpeta de install igual).
            WriteHookLog($"Unregister FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string? GetCurrentExecutablePath()
    {
        // Mismo patrón que RegistryAutostartService: en self-contained WPF, MainModule
        // apunta al .exe wrapper (no al dotnet host). Velopack invoca Spikit.exe durante
        // los hooks desde la carpeta versionada (.../app-X.Y.Z/), así que este path se
        // re-escribe en cada update (OnAfterUpdateFastCallback re-registra).
        var path = Process.GetCurrentProcess().MainModule?.FileName;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static void WriteHookLog(string message)
    {
        // Logger ad-hoc: Serilog aún no está configurado (los hooks corren antes que
        // ConfigureSerilog en Program.Main, y los FastCallbacks llaman Environment.Exit
        // después). Escribimos a un archivo dedicado para post-mortem de instaladores
        // problemáticos. Best-effort: si el filesystem falla, no podemos hacer nada.
        try
        {
            var logsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Spikit",
                "logs");
            Directory.CreateDirectory(logsDir);

            var line = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z] {message}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(logsDir, "velopack-hooks.log"), line);
        }
        catch
        {
            // Silencio total. El próximo arranque normal de la app va a poder detectar
            // si el scheme quedó bien registrado (test E2E manual).
        }
    }
}
