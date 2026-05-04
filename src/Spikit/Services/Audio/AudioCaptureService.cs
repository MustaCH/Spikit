namespace Spikit.Services.Audio;

public class AudioCaptureService : IAudioCaptureService
{
    public void Dispose() => GC.SuppressFinalize(this);
}
