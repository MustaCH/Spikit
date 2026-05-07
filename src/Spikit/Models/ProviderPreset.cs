namespace Spikit.Models;

// Presets de provider compatibles OpenAI Whisper (US-1.1 + requirements.md decisión #6).
// V1: 3 opciones. V2 puede sumar adapters nativos (Deepgram, AssemblyAI) — esos no son
// preset acá, son otra capa.
public enum ProviderPreset
{
    OpenAI = 0,
    Groq = 1,
    Custom = 2,
}

public sealed record ProviderPresetDefaults(string BaseUrl, string Model)
{
    public static ProviderPresetDefaults For(ProviderPreset preset) => preset switch
    {
        ProviderPreset.OpenAI => new ProviderPresetDefaults("https://api.openai.com/v1", "whisper-1"),
        ProviderPreset.Groq => new ProviderPresetDefaults("https://api.groq.com/openai/v1", "whisper-large-v3"),
        ProviderPreset.Custom => new ProviderPresetDefaults(string.Empty, string.Empty),
        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Preset desconocido"),
    };

    public static string DisplayName(ProviderPreset preset) => preset switch
    {
        ProviderPreset.OpenAI => "OpenAI",
        ProviderPreset.Groq => "Groq",
        ProviderPreset.Custom => "Custom",
        _ => preset.ToString(),
    };
}
