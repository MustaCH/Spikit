namespace Spikit.Services.Transcription;

public sealed class WhisperApiOptions
{
    // Endpoint base. /audio/transcriptions se appendea en runtime.
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";

    // Modelo Whisper-compatible. V1 usa whisper-1 (re-evaluar gpt-4o-transcribe cuando esté GA).
    public string Model { get; set; } = "whisper-1";

    // null = auto-detect (Whisper soporta ES + EN). En V1 lo dejamos null.
    public string? Language { get; set; }

    // Timeout total del request HTTP. 30s cubre warm + transcripción de ~30s de audio.
    public int TimeoutSeconds { get; set; } = 30;
}
