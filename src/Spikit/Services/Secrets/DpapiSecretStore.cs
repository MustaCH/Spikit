using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Spikit.Services.Secrets;

// Almacén DPAPI por archivo, una key = un archivo dentro de %AppData%\Spikit\secrets.
// Cada archivo guarda los bytes cifrados con DataProtectionScope.CurrentUser, alineado
// con RN-3 + CB-14 (la cuenta A no descifra los secretos de la cuenta B).
//
// Por qué archivo por key en lugar de un container único JSON:
//   - Sirve para inspección manual ("¿qué secretos hay?" → ls del directorio).
//   - Las rotaciones (Delete + Write) son atómicas a nivel filesystem por archivo,
//     sin races con otros secretos no relacionados.
//   - Path inyectable para tests; cada test usa su propio tmpdir.
public sealed class DpapiSecretStore : ISecretStore
{
    private readonly string _directory;
    private readonly ILogger<DpapiSecretStore> _logger;

    public DpapiSecretStore(ILogger<DpapiSecretStore> logger)
        : this(DefaultDirectory(), logger)
    {
    }

    // Constructor para tests: redirigir a tmpdir aislado.
    public DpapiSecretStore(string directory, ILogger<DpapiSecretStore> logger)
    {
        _directory = directory;
        _logger = logger;
    }

    public string Directory => _directory;

    public string? Read(string key)
    {
        var path = ResolvePath(key);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var encrypted = File.ReadAllBytes(path);
            var plaintext = ProtectedData.Unprotect(encrypted, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException ex)
        {
            // CB-14: el archivo existe pero el usuario actual no es el que lo cifró.
            // Lo tratamos como ausencia para que el bootstrap dispare el onboarding de nuevo
            // (esperado, no bug). El log queda como evidencia para debug remoto.
            _logger.LogWarning(ex, "No se pudo descifrar secreto {Key} (¿usuario distinto?)", key);
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "No se pudo leer secreto {Key}", key);
            return null;
        }
    }

    public void Write(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        System.IO.Directory.CreateDirectory(_directory);

        var plaintext = Encoding.UTF8.GetBytes(value);
        var encrypted = ProtectedData.Protect(plaintext, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);

        var path = ResolvePath(key);
        var tempPath = path + ".tmp";

        File.WriteAllBytes(tempPath, encrypted);
        File.Move(tempPath, path, overwrite: true);

        _logger.LogDebug("Secreto {Key} guardado en {Path}", key, path);
    }

    public void Delete(string key)
    {
        var path = ResolvePath(key);
        if (File.Exists(path))
        {
            File.Delete(path);
            _logger.LogDebug("Secreto {Key} eliminado", key);
        }
    }

    private string ResolvePath(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("La key del secreto no puede estar vacía.", nameof(key));
        }

        return Path.Combine(_directory, SanitizeFileName(key) + ".bin");
    }

    // Sanitiza una key canónica ("provider.apiKey") a un nombre de archivo válido en NTFS.
    // No nos preocupa la unicidad porque las keys del dominio son finitas y conocidas.
    private static string SanitizeFileName(string key)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(key.Length);
        foreach (var ch in key)
        {
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }
        return sb.ToString();
    }

    private static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Spikit",
        "secrets");
}
