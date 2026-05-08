namespace Spikit.Services.Audio;

// El device persistido en settings (audio.deviceId) no está activo en este momento.
// Casos típicos: el usuario desconectó los auriculares entre sesiones, o cambió de
// perfil con devices distintos. EP-5.3 (toast) cablea esta excepción a un mensaje
// user-friendly con CTA a Settings → Audio.
//
// El orchestrator captura esta excepción separada de Exception general para no
// confundirla con un fallo "de verdad" del WasapiCapture.
public sealed class AudioDeviceUnavailableException : Exception
{
    public string DeviceId { get; }

    public AudioDeviceUnavailableException(string deviceId, string message)
        : base(message)
    {
        DeviceId = deviceId;
    }

    public AudioDeviceUnavailableException(string deviceId, string message, Exception innerException)
        : base(message, innerException)
    {
        DeviceId = deviceId;
    }
}
