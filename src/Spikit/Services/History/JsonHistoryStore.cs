using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Spikit.Models;

namespace Spikit.Services.History;

// Implementación JSON del IHistoryStore. Persiste en %AppData%\Spikit\history.json
// como un array directo de HistoryEntry. Misma estrategia atómica que JsonSettingsService:
// write-to-tmp + rename, lock para serializar escrituras concurrentes (no esperado pero
// el costo es trivial).
//
// Estructura del archivo:
//   [
//     { "id": "...", "timestamp": "...", "durationMs": 18432, "text": "...", "targetProcessName": "cursor.exe" },
//     ...
//   ]
//
// Estrategia de carga: lazy + cached. La primera query lee el archivo a memoria; las
// siguientes operan sobre el cache hasta que un mutator (Append/Delete) lo invalide.
// Para historial chico (típicamente <1000 entries) este approach es suficiente. Si V2
// necesita streaming desde disco lo cambiamos sin tocar callers.
public sealed class JsonHistoryStore : IHistoryStore
{
    private const string FileName = "history.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;
    private readonly ILogger<JsonHistoryStore> _logger;
    private readonly object _lock = new();

    private List<HistoryEntry>? _cache;

    public JsonHistoryStore(ILogger<JsonHistoryStore> logger)
        : this(DefaultFilePath(), logger)
    {
    }

    // Constructor para tests: redirigir a tmpdir.
    public JsonHistoryStore(string filePath, ILogger<JsonHistoryStore> logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public string FilePath => _filePath;

    public void Append(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Id == Guid.Empty)
        {
            entry.Id = Guid.NewGuid();
        }

        lock (_lock)
        {
            var entries = LoadInternal();
            entries.Add(entry);
            WriteInternal(entries);
            _cache = entries;
        }
    }

    public IReadOnlyList<HistoryEntry> LoadPage(int skip, int take)
    {
        if (take <= 0) return Array.Empty<HistoryEntry>();
        if (skip < 0) skip = 0;

        lock (_lock)
        {
            var entries = LoadInternal();
            // Orden DESC por Timestamp. ToList() dentro del lock para no devolver el
            // ordering live (si después de leer el caller llama Append, su slice es
            // un snapshot del momento del llamado).
            return entries
                .OrderByDescending(e => e.Timestamp)
                .Skip(skip)
                .Take(take)
                .ToList();
        }
    }

    public int Count()
    {
        lock (_lock)
        {
            return LoadInternal().Count;
        }
    }

    public IReadOnlyList<HistoryEntry> Search(string query)
    {
        // Empty query = devolver todo (DESC). UI llama Search("") cuando el usuario
        // limpia el textbox; esto evita que el VM tenga dos paths distintos.
        if (string.IsNullOrEmpty(query))
        {
            return LoadPage(skip: 0, take: int.MaxValue);
        }

        lock (_lock)
        {
            var entries = LoadInternal();
            return entries
                .Where(e => e.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.Timestamp)
                .ToList();
        }
    }

    public void DeleteOne(Guid id)
    {
        lock (_lock)
        {
            var entries = LoadInternal();
            var index = entries.FindIndex(e => e.Id == id);
            if (index < 0) return;

            entries.RemoveAt(index);
            WriteInternal(entries);
            _cache = entries;
        }
    }

    public void DeleteAll()
    {
        lock (_lock)
        {
            // Borrar el archivo en lugar de escribir un array vacío. Coherente con
            // RN-2 ("ningún dato persiste") — si el toggle pasa a OFF después, no
            // queda un history.json residual en %AppData%.
            try
            {
                if (File.Exists(_filePath))
                {
                    File.Delete(_filePath);
                    _logger.LogInformation("history.json eliminado por DeleteAll");
                }
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "DeleteAll: no se pudo borrar history.json");
            }
            _cache = new List<HistoryEntry>();
        }
    }

    // ============ Internos ============

    private List<HistoryEntry> LoadInternal()
    {
        if (_cache is not null) return _cache;

        if (!File.Exists(_filePath))
        {
            _cache = new List<HistoryEntry>();
            return _cache;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<List<HistoryEntry>>(json, JsonOptions);
            _cache = loaded ?? new List<HistoryEntry>();
        }
        catch (JsonException ex)
        {
            // Archivo corrupto: log + tratar como vacío. La UI muestra empty state. NO
            // borramos el archivo automáticamente: si el usuario quiere recuperar algo
            // a mano, los bytes siguen ahí. La próxima Append los va a sobrescribir.
            _logger.LogWarning(ex, "history.json corrupto en {Path}, usando lista vacía", _filePath);
            _cache = new List<HistoryEntry>();
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "No se pudo leer history.json en {Path}", _filePath);
            _cache = new List<HistoryEntry>();
        }
        return _cache;
    }

    private void WriteInternal(List<HistoryEntry> entries)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(entries, JsonOptions);
        var tempPath = _filePath + ".tmp";

        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);

        _logger.LogDebug("history.json escrito ({Count} entries) en {Path}", entries.Count, _filePath);
    }

    private static string DefaultFilePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Spikit",
        FileName);
}
