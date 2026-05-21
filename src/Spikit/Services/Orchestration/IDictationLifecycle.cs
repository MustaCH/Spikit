namespace Spikit.Services.Orchestration;

// Subset del DictationOrchestrator que ISessionLifecycleService consume (EP-11.7).
// Implementado por el mismo singleton del orchestrator, expuesto por separado para
// que el SessionLifecycleService no dependa de la clase concreta sellada. Sigue el
// patrón de IDictationDemoMode (EP-4.4) — misma instancia detrás, surface más chico.
public interface IDictationLifecycle
{
    // Cancela cualquier dictado activo (Recording / Transcribing / Inserting),
    // descarta el audio capturado y vuelve a Idle. No-op si ya está en Idle.
    Task CancelActiveSessionAsync();

    // Desuscribe el orchestrator de los eventos de hotkey/audio. La instancia sigue
    // viva y puede reactivarse con Start() (mismo método del orchestrator) tras un
    // re-login. NO dispone el singleton — eso es trabajo de App.OnExit.
    void Stop();
}
