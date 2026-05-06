namespace Spikit.Services.Orchestration;

// Estados del flow de dictado. Definido en docs/architecture.md § "Estados del DictationOrchestrator".
public enum DictationState
{
    Idle,
    Recording,
    Transcribing,
    Inserting,
    ShowingFloatingResult,
}
