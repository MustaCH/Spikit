using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Spikit.Services.Audio;
using Spikit.Services.Settings;
using Spikit.Services.Transcription;

namespace Spikit.ViewModels.Settings.Sections;

// VM de la sección Audio de Settings (EP-4.6). Dos bloques onChange:
//   1. Dropdown Micrófono (US-5.3) — primera fila "Default del sistema" (DeviceId vacío) +
//      devices reales del IAudioDeviceEnumerator. Persiste en audio.deviceId.
//   2. Radio idioma transcripción (US-5.4) — Auto / Español / Inglés. Persiste en
//      transcription.language como "auto" | "es" | "en".
//
// Cableado runtime (que el AudioCaptureService realmente use el deviceId nuevo y que el
// WhisperApiOptions.Language tome el setting): cubierto en EP-4.10. Acá solo persistimos
// y refrescamos UI.
//
// Manejo de hot-plug: si el device persistido ya no aparece en List() (desconectado entre
// sesiones), el dropdown muestra "Default del sistema" como seleccionado, no muestra una
// entry stale. Si el usuario quiere preservar la elección original, tiene que reconectar
// el device antes de abrir Settings — coherente con cómo Windows trata los defaults.
public sealed class AudioSectionViewModel : ViewModelBase
{
    private readonly ILogger<AudioSectionViewModel> _logger;
    private readonly ISettingsService _settingsService;
    private readonly IAudioDeviceEnumerator _deviceEnumerator;
    private readonly AudioRuntimeOptions _audioRuntime;
    private readonly WhisperApiOptions _whisperRuntime;

    private AudioInputDevice? _selectedDevice;
    private TranscriptionLanguageOption _language;

    private bool _suppressEffects;

    public AudioSectionViewModel(
        ILogger<AudioSectionViewModel> logger,
        ISettingsService settingsService,
        IAudioDeviceEnumerator deviceEnumerator,
        AudioRuntimeOptions audioRuntime,
        WhisperApiOptions whisperRuntime)
    {
        _logger = logger;
        _settingsService = settingsService;
        _deviceEnumerator = deviceEnumerator;
        _audioRuntime = audioRuntime;
        _whisperRuntime = whisperRuntime;

        Devices = new ObservableCollection<AudioInputDevice>();
        LoadFromPersistence();
    }

    // ============ Bloque 1 — Micrófono ============

    public ObservableCollection<AudioInputDevice> Devices { get; }

    // SelectedDevice TwoWay con el ComboBox. Cuando cambia, persistimos.
    public AudioInputDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!SetProperty(ref _selectedDevice, value)) return;
            if (_suppressEffects) return;

            PersistDeviceId(value?.Id ?? string.Empty);
            _logger.LogDebug("Audio device → {Device}", value?.Name ?? "(default)");
        }
    }

    // ============ Bloque 2 — Idioma ============

    public TranscriptionLanguageOption Language
    {
        get => _language;
        set
        {
            if (!SetProperty(ref _language, value)) return;
            OnPropertyChanged(nameof(IsLanguageAuto));
            OnPropertyChanged(nameof(IsLanguageSpanish));
            OnPropertyChanged(nameof(IsLanguageEnglish));

            if (_suppressEffects) return;

            PersistLanguage(value);
            _logger.LogDebug("Transcription language → {Language}", value);
        }
    }

    // Bindings para los RadioButtons. Mismo patrón que IsPushToTalk/IsToggle del HotkeyStepVM:
    // setter ignorado cuando value=false para no romper la mutua exclusión.
    public bool IsLanguageAuto
    {
        get => _language == TranscriptionLanguageOption.Auto;
        set { if (value) Language = TranscriptionLanguageOption.Auto; }
    }

    public bool IsLanguageSpanish
    {
        get => _language == TranscriptionLanguageOption.Spanish;
        set { if (value) Language = TranscriptionLanguageOption.Spanish; }
    }

    public bool IsLanguageEnglish
    {
        get => _language == TranscriptionLanguageOption.English;
        set { if (value) Language = TranscriptionLanguageOption.English; }
    }

    // ============ Persistencia ============

    private void LoadFromPersistence()
    {
        _suppressEffects = true;
        try
        {
            var settings = _settingsService.Load();

            // Recargamos la lista de devices al abrir la sección. Si el usuario conectó/
            // desconectó devices entre aperturas, esto refleja el estado actual.
            Devices.Clear();
            Devices.Add(DefaultDevice);
            foreach (var device in _deviceEnumerator.List())
            {
                Devices.Add(device);
            }

            // Match del deviceId persistido contra las entries actuales. Si no aparece
            // (desconectado), caemos al default — el setter no dispara persist gracias al
            // _suppressEffects.
            var persistedId = settings.Audio.DeviceId ?? string.Empty;
            _selectedDevice = string.IsNullOrEmpty(persistedId)
                ? DefaultDevice
                : Devices.FirstOrDefault(d => d.Id == persistedId) ?? DefaultDevice;
            OnPropertyChanged(nameof(SelectedDevice));

            _language = ParseLanguage(settings.Transcription.Language);
            OnPropertyChanged(nameof(Language));
            OnPropertyChanged(nameof(IsLanguageAuto));
            OnPropertyChanged(nameof(IsLanguageSpanish));
            OnPropertyChanged(nameof(IsLanguageEnglish));
        }
        finally
        {
            _suppressEffects = false;
        }
    }

    private void PersistDeviceId(string deviceId)
    {
        var settings = _settingsService.Load();
        settings.Audio.DeviceId = deviceId;
        _settingsService.Save(settings);

        // EP-4.10: cableado runtime — la próxima sesión de dictado va a ver este deviceId
        // sin reiniciar la app. AudioCaptureService consulta AudioRuntimeOptions.DeviceId
        // en cada StartAsync (no se cachea).
        _audioRuntime.DeviceId = deviceId;
    }

    private void PersistLanguage(TranscriptionLanguageOption option)
    {
        var settings = _settingsService.Load();
        var languageId = ToLanguageId(option);
        settings.Transcription.Language = languageId;
        _settingsService.Save(settings);

        // EP-4.10: mutamos el WhisperApiOptions singleton para que la próxima request a
        // /audio/transcriptions use el language nuevo. WhisperApiTranscriptionService omite
        // el parámetro language cuando es null/vacío — convertimos "auto" a null acá para
        // mantener esa semántica (whisper auto-detecta cuando el campo no se manda).
        _whisperRuntime.Language = languageId == "auto" ? null : languageId;
    }

    // ============ Helpers ============

    // Entry estable de "Default del sistema" — usamos string.Empty como Id sentinela y un
    // nombre amigable. La fila es siempre la primera del dropdown.
    public static AudioInputDevice DefaultDevice { get; } = new(string.Empty, "Default del sistema");

    private static TranscriptionLanguageOption ParseLanguage(string? id) => id?.ToLowerInvariant() switch
    {
        "es" => TranscriptionLanguageOption.Spanish,
        "en" => TranscriptionLanguageOption.English,
        _ => TranscriptionLanguageOption.Auto,
    };

    private static string ToLanguageId(TranscriptionLanguageOption option) => option switch
    {
        TranscriptionLanguageOption.Spanish => "es",
        TranscriptionLanguageOption.English => "en",
        _ => "auto",
    };
}

// Enum de UI puro para los radio buttons. No vive en Models porque el contrato persistido
// es string ("auto" | "es" | "en") — el enum es solo para tipar el binding del VM.
public enum TranscriptionLanguageOption
{
    Auto = 0,
    Spanish = 1,
    English = 2,
}
