using Spikit.Native;

namespace Spikit.Models;

// Raíz del archivo %AppData%\Spikit\settings.json. Crece con el resto del onboarding
// (flag onboardingCompleted en EP-3.8). Las secciones "provider" y "hotkey" cierran
// EP-3.4 y EP-3.6 respectivamente. Las propiedades NO se mutan in-place desde fuera del
// settings service — el flujo canónico es Load → mutar → Save.
public sealed class AppSettings
{
    public ProviderSettings Provider { get; set; } = new();
    public HotkeySettings Hotkey { get; set; } = new();
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

// Sección "hotkey" — ver acceptance criteria de EP-3.6. El JSON guarda los modifiers como
// string concatenado por comma+space (formato natural del Enum.ToString() para un [Flags]
// enum) + el virtual-key code numérico + el modo serializado a string.
//
// Defaults bootstrap (cuando no hay settings.json todavía): Ctrl+Alt+M / PushToTalk.
public sealed class HotkeySettings
{
    public string Modifiers { get; set; } = (HotkeyModifiers.Control | HotkeyModifiers.Alt).ToString();
    public uint VirtualKey { get; set; } = VirtualKeys.M;
    public string Mode { get; set; } = nameof(HotkeyMode.PushToTalk);

    // Conversión runtime → settings. El HotkeyDefinition guarda solo modificadores reales
    // (no NoRepeat, que vive del lado de la API de Win32 al registrar).
    public static HotkeySettings From(HotkeyDefinition definition, HotkeyMode mode) => new()
    {
        Modifiers = definition.Modifiers.ToString(),
        VirtualKey = definition.VirtualKey,
        Mode = mode.ToString(),
    };

    // Settings → runtime. Si el JSON está corrupto o trae enum-values inválidos, devolvemos
    // los defaults V1 — coherente con la política de JsonSettingsService.Load() (recover
    // gracefully). El caller (bootstrap) decide si pegarle un warning al usuario.
    public bool TryToRuntime(out HotkeyDefinition definition, out HotkeyMode mode)
    {
        if (!Enum.TryParse<HotkeyModifiers>(Modifiers, out var parsedModifiers)
            || !Enum.TryParse<HotkeyMode>(Mode, out var parsedMode)
            || VirtualKey == 0)
        {
            definition = HotkeyDefinition.Default;
            mode = HotkeyMode.PushToTalk;
            return false;
        }

        // NoRepeat no debería estar en el JSON (no lo emitimos al guardar) pero por las dudas
        // lo limpiamos antes de armar la HotkeyDefinition runtime.
        var clean = parsedModifiers & ~HotkeyModifiers.NoRepeat;
        definition = new HotkeyDefinition(clean, VirtualKey);
        mode = parsedMode;
        return true;
    }
}
