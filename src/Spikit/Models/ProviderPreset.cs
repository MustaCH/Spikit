using System.Collections.Immutable;

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

// Defaults canónicos por preset: BaseUrl + Model sugerido + lista de modelos disponibles.
//
// AvailableModels alimenta el dropdown del campo "Modelo" cuando el preset NO es Custom
// (en Custom el campo se vuelve TextBox libre porque no podemos predecir qué modelo trae
// un endpoint custom).
public sealed record ProviderPresetDefaults(string BaseUrl, string Model, ImmutableArray<string> AvailableModels)
{
    public static ProviderPresetDefaults For(ProviderPreset preset) => preset switch
    {
        ProviderPreset.OpenAI => new ProviderPresetDefaults(
            // Default cambiado de `whisper-1` a `gpt-4o-transcribe` el 2026-05-20:
            // mismo precio ($0.006/min), WER significativamente mejor segun OpenAI
            // (oct-2024) y benchmark FLEURS. `whisper-1` queda disponible en el
            // dropdown como fallback seguro si aparece regresion. Aplica solo a
            // nuevos installs / "Restablecer defaults" del preset — usuarios BYOK
            // con `whisper-1` ya persistido en settings.json mantienen su eleccion.
            BaseUrl: "https://api.openai.com/v1",
            Model: "gpt-4o-transcribe",
            AvailableModels: ImmutableArray.Create("whisper-1", "gpt-4o-mini-transcribe", "gpt-4o-transcribe")),

        ProviderPreset.Groq => new ProviderPresetDefaults(
            BaseUrl: "https://api.groq.com/openai/v1",
            Model: "whisper-large-v3",
            AvailableModels: ImmutableArray.Create("whisper-large-v3", "whisper-large-v3-turbo")),

        ProviderPreset.Custom => new ProviderPresetDefaults(
            BaseUrl: string.Empty,
            Model: string.Empty,
            AvailableModels: ImmutableArray<string>.Empty),

        _ => throw new ArgumentOutOfRangeException(nameof(preset), preset, "Preset desconocido"),
    };

    public static string DisplayName(ProviderPreset preset) => preset switch
    {
        ProviderPreset.OpenAI => "OpenAI",
        ProviderPreset.Groq => "Groq",
        ProviderPreset.Custom => "Custom",
        _ => preset.ToString(),
    };

    // Identificador estable que persiste en provider.presetId del settings.json. Lowercase
    // para que el JSON sea legible y no acoplado al orden del enum.
    public static string ToPresetId(ProviderPreset preset) => preset switch
    {
        ProviderPreset.OpenAI => "openai",
        ProviderPreset.Groq => "groq",
        ProviderPreset.Custom => "custom",
        _ => preset.ToString().ToLowerInvariant(),
    };
}
