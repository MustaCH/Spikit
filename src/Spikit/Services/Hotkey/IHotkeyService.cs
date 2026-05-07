using Spikit.Models;

namespace Spikit.Services.Hotkey;

public interface IHotkeyService : IDisposable
{
    // Registra el hotkey global. Lanza HotkeyRegistrationException si la combinación
    // ya está tomada por otra app (CB-7: el caller decide cómo mostrar el error al usuario).
    void Register(HotkeyDefinition definition);

    // Idempotente: llamar sin haber registrado no hace nada.
    void Unregister();

    // La definición actualmente registrada, o null si nada registrado todavía. La usa
    // HotkeyConfigWriter para hacer rollback a la combinación previa cuando un Register
    // nuevo falla por CB-7 o cuando JsonSettings rechaza la persistencia post-Register.
    HotkeyDefinition? CurrentRegistration { get; }

    // Disparado en el press completo de la combinación. Para push-to-talk, marca el
    // inicio de la sesión de captura.
    event EventHandler? HotkeyPressed;

    // Disparado cuando alguna de las teclas de la combinación se libera. Para push-to-talk,
    // marca el final de la sesión de captura. Solo se dispara una vez por press.
    event EventHandler? HotkeyReleased;

    // Pausa del hotkey: cuando IsPaused=true los eventos HotkeyPressed/Released NO se disparan,
    // pero la combinación sigue registrada en Win32 (no se desregistra). Esto es lo que
    // implementa "Pausar app" del tray menu (EP-4.2 / D-6) — el usuario puede silenciar
    // el hotkey sin perder la registración ante el OS. Si una sesión está activa y se
    // pausa, NO se interrumpe en el medio: el cortocircuito aplica al próximo press.
    bool IsPaused { get; }
    void SetPaused(bool paused);
    event EventHandler? PausedChanged;

    // Simula un press del hotkey desde código (sin que el usuario apriete teclas físicas).
    // Uso del tray menu "▶ Iniciar dictado" (EP-4.2). NO inicia release polling porque las
    // teclas físicas no están apretadas — el callsite tiene que estar en modo Toggle (donde
    // el orchestrator no espera el release para cerrar la sesión). En modo PushToTalk
    // disparar este método cerraría la sesión inmediatamente al primer poll. Si está pausado
    // o ya hay una sesión abierta, es un no-op.
    void TriggerManualPress();
}

public sealed class HotkeyRegistrationException : Exception
{
    public HotkeyRegistrationException(string message) : base(message) { }
    public HotkeyRegistrationException(string message, Exception inner) : base(message, inner) { }
}
