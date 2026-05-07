namespace Spikit.Services.Orchestration;

// Modo demo del DictationOrchestrator (EP-4.4 — botón "Probar" de Settings → Hotkey).
//
// Mientras IsDemoMode=true, el orchestrator cortocircuita su OnHotkeyPressed: NO inicia
// AudioCaptureService ni Whisper, solo dispara DemoHotkeyDetected y simula un flash visual
// de la pill (StateChanged Recording → 600 ms → Idle, que la pill ya consume vía su VM
// existente). El AC explícito del ticket es "no se llama a AudioCaptureService ni a Whisper".
//
// El flag se desactiva automáticamente cuando se completa el flash, o vía EndDemoMode si el
// usuario cancela (Esc) antes de apretar el hotkey.
//
// Inyectado al HotkeySectionViewModel como abstracción para que los tests puedan verificar
// que un Probar no toca al orchestrator real (FakeDemoMode counts BeginDemoMode calls sin
// arrancar audio).
public interface IDictationDemoMode
{
    bool IsDemoMode { get; }

    // Disparado cuando el HotkeyService detecta un press estando IsDemoMode=true. El VM
    // de la sección Hotkey se suscribe para mutar el toast a "Hotkey detectado ✓".
    event EventHandler? DemoHotkeyDetected;

    void BeginDemoMode();

    void EndDemoMode();
}
