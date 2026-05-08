namespace Spikit.Services.Audio;

// Lista los dispositivos de input de audio activos del sistema (US-5.3). Implementación
// V1 vía NAudio CoreAudioApi.MMDeviceEnumerator. La UI consume el resultado para poblar
// el dropdown de Settings → Audio.
//
// La opción "Default del sistema" NO sale acá — la agrega el VM de la sección como primera
// fila de la lista (DeviceId vacío). Mantener la separación deja al enumerator alineado
// con lo que reporta Win32 sin entry sintética.
public interface IAudioDeviceEnumerator
{
    IReadOnlyList<AudioInputDevice> List();
}

// Representa un dispositivo de input. Id es el ID estable del endpoint MMDevice (lo que
// persistimos en settings.json). Name es el friendly name que muestra Windows.
public sealed record AudioInputDevice(string Id, string Name);
