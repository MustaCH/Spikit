namespace Spikit.Models;

// Raíz del archivo %AppData%\Spikit\settings.json. Crece con el resto del onboarding (Hotkey
// en EP-3.6, flag onboardingCompleted en EP-3.8). Por ahora solo la sección "provider" del
// EP-3.4. Las propiedades NO se mutan in-place desde fuera del settings service — el flujo
// canónico es Load → mutar → Save.
public sealed class AppSettings
{
    public ProviderSettings Provider { get; set; } = new();
}

// Sección "provider" — ver acceptance criteria de EP-3.4. presetId es el enum ProviderPreset
// serializado a string en lowercase ("openai" | "groq" | "custom") para que el JSON quede
// legible y estable frente a cambios de orden del enum.
public sealed class ProviderSettings
{
    public string PresetId { get; set; } = "openai";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "whisper-1";
}
