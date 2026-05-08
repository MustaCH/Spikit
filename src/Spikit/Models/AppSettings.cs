using Spikit.Native;

namespace Spikit.Models;

// Raíz del archivo %AppData%\Spikit\settings.json. Las secciones "provider" y "hotkey"
// cierran EP-3.4 y EP-3.6 respectivamente; el flag "onboardingCompleted" de EP-3.8
// gatea el bootstrap (App.xaml.cs decide entre OnboardingWindow y MainApp al startup).
// Las propiedades NO se mutan in-place desde fuera del settings service — el flujo
// canónico es Load → mutar → Save.
public sealed class AppSettings
{
    public ProviderSettings Provider { get; set; } = new();
    public HotkeySettings Hotkey { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public TranscriptionSettings Transcription { get; set; } = new();
    public PrivacySettings Privacy { get; set; } = new();

    // Flag persistido al apretar Finalizar o Saltar en el step Prueba (EP-3.7). Mientras
    // sea false, App.OnStartup vuelve a abrir el onboarding al levantar la app. RN-5:
    // sin onboarding completo no se entra al estado de dictado.
    public bool OnboardingCompleted { get; set; }
}

// Sección "general" — autostart con Windows + tema visual + anchor de la pill (EP-4.5).
// Los strings se serializan en lowercase al JSON para que sea robusto frente a renames
// del enum y legible al inspeccionar settings.json a mano. La conversión a enum runtime
// la hacen TryToTheme/TryToAnchor con fallback a defaults V1.
public sealed class GeneralSettings
{
    public string Theme { get; set; } = "system";
    public bool AutoStart { get; set; } = false;
    public string PillAnchor { get; set; } = "bottomCenter";

    public AppTheme TryToTheme() => Theme?.ToLowerInvariant() switch
    {
        "dark" => AppTheme.Dark,
        "light" => AppTheme.Light,
        _ => AppTheme.System,
    };

    public PillAnchor TryToAnchor() => PillAnchor?.ToLowerInvariant() switch
    {
        "topleft" => Models.PillAnchor.TopLeft,
        "topcenter" => Models.PillAnchor.TopCenter,
        "topright" => Models.PillAnchor.TopRight,
        "bottomleft" => Models.PillAnchor.BottomLeft,
        "bottomright" => Models.PillAnchor.BottomRight,
        _ => Models.PillAnchor.BottomCenter,
    };

    public static string ToThemeId(AppTheme theme) => theme switch
    {
        AppTheme.Dark => "dark",
        AppTheme.Light => "light",
        _ => "system",
    };

    public static string ToAnchorId(PillAnchor anchor) => anchor switch
    {
        Models.PillAnchor.TopLeft => "topleft",
        Models.PillAnchor.TopCenter => "topcenter",
        Models.PillAnchor.TopRight => "topright",
        Models.PillAnchor.BottomLeft => "bottomleft",
        Models.PillAnchor.BottomRight => "bottomright",
        _ => "bottomcenter",
    };
}

// Sección "privacy" (EP-4.7 / US-5.5 + EP-8.3 crash reports). Defaults OFF coherentes
// con RN-2 ("ningún dato persiste salvo opt-in explícito"). Cuando un toggle es false,
// la lógica respectiva no actúa (no se escribe history.json, no se inicializa Sentry).
//
// HistoryEnabled — toggle del historial local. EP-4.7 expone el switch en Settings.
// SendCrashReports — toggle de Sentry (EP-8.3). El bootstrap de Program.cs lee este
//   flag para decidir si inicializa el SDK. **Sin UI todavía** — el toggle visible en
//   Settings + Onboarding queda como TODO de Frontend (ver hand-off del ticket EP-8.3
//   y "Pendientes" de docs/infra.md). Persistir vía DPAPI no aplica: es un bool, no
//   un secreto.
public sealed class PrivacySettings
{
    public bool HistoryEnabled { get; set; } = false;
    public bool SendCrashReports { get; set; } = false;
}

// Sección "audio" (EP-4.6 / US-5.3). DeviceId vacío representa "default del sistema" —
// lo elegimos como sentinela explícita en lugar de null para que JsonSerializer no tenga
// que escribir `null` (algunos parsers se confunden) y para mantener el shape "siempre string".
public sealed class AudioSettings
{
    public string DeviceId { get; set; } = string.Empty;
}

// Sección "transcription" (EP-4.6 / US-5.4). Language es uno de "auto" (default) | "es" | "en".
// Persistimos como string lowercase coherente con el resto del JSON (provider, general).
// "auto" se mapea a no enviar el parámetro language a la API de Whisper (auto-detect del provider).
public sealed class TranscriptionSettings
{
    public string Language { get; set; } = "auto";

    // Devuelve null para "auto" (Whisper auto-detect) o "es"/"en" para los códigos ISO-639-1
    // que la API acepta. Cualquier valor inválido cae a null para no romper el request.
    public string? ResolveWhisperLanguage() => Language?.ToLowerInvariant() switch
    {
        "es" => "es",
        "en" => "en",
        _ => null,
    };
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
