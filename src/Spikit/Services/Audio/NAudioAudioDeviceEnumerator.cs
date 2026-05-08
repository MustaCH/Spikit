using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;

namespace Spikit.Services.Audio;

// Implementación que enumera devices vía NAudio CoreAudioApi (WASAPI). DeviceState.Active
// excluye los desconectados/disabled — coherente con lo que el OS muestra en el "default
// dropdown" de Sound settings. Si el usuario quiere uno que no aparece, tiene que primero
// habilitarlo desde Windows.
public sealed class NAudioAudioDeviceEnumerator : IAudioDeviceEnumerator
{
    private readonly ILogger<NAudioAudioDeviceEnumerator> _logger;

    public NAudioAudioDeviceEnumerator(ILogger<NAudioAudioDeviceEnumerator> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<AudioInputDevice> List()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            var result = new List<AudioInputDevice>(devices.Count);
            foreach (var device in devices)
            {
                try
                {
                    result.Add(new AudioInputDevice(device.ID, device.FriendlyName));
                }
                finally
                {
                    device.Dispose();
                }
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Falló enumeración de devices de audio — devolviendo lista vacía");
            return Array.Empty<AudioInputDevice>();
        }
    }
}
