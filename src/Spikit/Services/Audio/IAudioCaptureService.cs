namespace Spikit.Services.Audio;

// Contrato definido en docs/architecture.md § "Contrato de IAudioCaptureService".
// Decisión y trade-offs: ADR-0003.
public interface IAudioCaptureService : IDisposable
{
    // Abre el mic vía WasapiCapture en shared mode (Role.Console).
    // Puede tardar hasta ~1s p99 en warm-cold (subsystema de audio dormido).
    // El consumer DEBE pintar feedback al usuario durante ese gap, suscribiéndose
    // al evento StateChanged.
    Task StartAsync(CancellationToken ct);

    // Cierra el mic y libera el WasapiCapture. El indicador de mic del OS
    // tarda 1-2s en desaparecer después de esto (comportamiento de Windows,
    // no acelerable).
    Task StopAsync();

    // Emite transiciones de estado del servicio:
    //   Idle → Initializing → Recording → Stopping → Idle
    // El estado Initializing es DOMINANTE en warm-cold (~600ms p50).
    // La pill lo refleja como sub-estado "Iniciando…" de primera clase.
    event EventHandler<AudioCaptureState> StateChanged;

    // Stream de samples para visualización en tiempo real (waveform).
    // RMS calculado en ventanas de ~30ms.
    event EventHandler<float> RmsLevelChanged;

    // Stream de samples crudos para el buffer principal de la sesión.
    // Acumulado por el orchestrator hasta el Stop.
    event EventHandler<short[]> SamplesAvailable;
}

public enum AudioCaptureState
{
    Idle,           // mic cerrado
    Initializing,   // StartAsync invocado, esperando primer sample real
    Recording,      // primer sample no-cero recibido, capturando normalmente
    Stopping,       // StopAsync invocado, drenando últimos buffers
}
