using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spikit.Models;

namespace Spikit.Services.Settings;

// Persistencia JSON en %AppData%\Spikit\settings.json (path inyectable para tests).
//
// Estrategia de escritura:
//   1. Crear directorio si no existe (idempotente).
//   2. Escribir a un archivo temporal hermano (`settings.json.tmp`).
//   3. Move atómico (`File.Move(..., overwrite: true)`).
// Esto evita dejar un settings.json corrupto si el proceso muere a mitad de escritura
// (ej. apagón). En lectura, si el archivo no existe o está corrupto, devolvemos defaults
// y logueamos warning — la app puede arrancar limpia y EP-3.8 mostrará el onboarding.
public sealed class JsonSettingsService : ISettingsService
{
    private const string FileName = "settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;
    private readonly ILogger<JsonSettingsService> _logger;
    private readonly object _writeLock = new();

    public event EventHandler? SettingsChanged;

    public JsonSettingsService(ILogger<JsonSettingsService> logger)
        : this(DefaultFilePath(), logger)
    {
    }

    // Constructor para tests: permite redirigir a un tmpdir.
    public JsonSettingsService(string filePath, ILogger<JsonSettingsService> logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public string FilePath => _filePath;

    public AppSettings Load()
    {
        if (!File.Exists(_filePath))
        {
            _logger.LogDebug("settings.json no existe en {Path}, devolviendo defaults", _filePath);
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (loaded is null)
            {
                _logger.LogWarning("settings.json deserializó null en {Path}, usando defaults", _filePath);
                return new AppSettings();
            }
            // Defensa contra archivos viejos sin las secciones nuevas. Cada feature (Hotkey,
            // General, Audio, Transcription) extiende sin romper a archivos viejos.
            loaded.Provider ??= new ProviderSettings();
            loaded.General ??= new GeneralSettings();
            loaded.Audio ??= new AudioSettings();
            loaded.Transcription ??= new TranscriptionSettings();
            return loaded;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "settings.json corrupto en {Path}, usando defaults", _filePath);
            return new AppSettings();
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "No se pudo leer settings.json en {Path}, usando defaults", _filePath);
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var tempPath = _filePath + ".tmp";

        // Lock para evitar interleaving si dos llamadas a Save() ocurren en paralelo
        // (no es un escenario esperado pero un singleton podría recibir saves concurrentes).
        lock (_writeLock)
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }

        _logger.LogDebug("settings.json escrito en {Path}", _filePath);

        // Disparado fuera del lock para no mantener el writeLock durante callbacks largos
        // de suscriptores (refresh de UI, etc.). El evento garantiza solo "algo cambió",
        // no qué cambió — los listeners hacen Load() para el snapshot fresco.
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string DefaultFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Spikit",
        FileName);
}
