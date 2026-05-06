namespace Spikit.Services.Transcription;

// Wrapper inyectable para la API key. Mantiene la key fuera de WhisperApiOptions
// (que es serializable desde appsettings.json) y permite mockear en tests sin
// tocar variables de entorno.
public sealed record WhisperApiKey(string Value)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Value);
}
