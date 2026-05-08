namespace Spikit.Services.Audio;

// Singleton mutable para las preferencias de audio que el AudioCaptureService consulta
// en cada StartAsync. Mismo patrón que WhisperApiKey + WhisperApiOptions:
//   - El bootstrap (Program.cs) lo hidrata desde settings.json al startup.
//   - Cuando el usuario cambia el setting en Settings → Audio, la VM muta este singleton
//     ADEMÁS de persistir, para que la próxima sesión use el device nuevo sin reiniciar.
//
// DeviceId vacío = "default del sistema" — el AudioCaptureService cae a
// MMDeviceEnumerator.GetDefaultAudioEndpoint coherente con cómo Windows trata el default.
public sealed class AudioRuntimeOptions
{
    private string _deviceId = string.Empty;

    public string DeviceId
    {
        get => _deviceId;
        set => _deviceId = value ?? string.Empty;
    }
}
