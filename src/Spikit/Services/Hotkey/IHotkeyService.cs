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

    // Hotkey global secundario para cancelar una sesión de dictado activa (Q-7 / Esc cancela
    // en initializing/recording/transcribing). Se registra solo durante esos estados y se
    // libera en Idle/Inserting para no robar Esc al resto de las apps. Usa un id de Win32
    // distinto del hotkey principal — sin modificadores, fsModifiers=0, VK_ESCAPE.
    //
    // Idempotente: registrar de nuevo si ya está registrado es no-op; unregister sin haber
    // registrado tampoco rompe nada. Si Win32 rechaza el registro (otra app reservó Esc
    // sin modificadores), se loguea warning pero no se propaga — la sesión sigue funcionando
    // sin cancel global, y el usuario tiene la alternativa del re-press del hotkey (CB-2).
    void RegisterCancelHotkey();
    void UnregisterCancelHotkey();
    event EventHandler? CancelHotkeyPressed;

    // Suspende temporalmente la registración Win32 del hotkey principal mientras un campo
    // de captura está abierto en la UI. Sin esto, si el usuario quiere recapturar la misma
    // combinación que ya está activa (ej. cambiar Ctrl+M → Ctrl+Alt+M cuando Ctrl+Alt+M era
    // la previa), Win32 se traga el press antes de que WPF lo entregue como KeyDown al
    // HotkeyCaptureField — y la app empieza a grabar en vez de capturar la nueva combo.
    //
    // Suspend libera la combinación al OS y guarda la referencia. Resume la re-registra.
    // Si la app se cierra entre uno y otro (raro), el cleanup del Dispose normal alcanza.
    // Idempotente: llamar Suspend dos veces seguidas o Resume sin Suspend previo es no-op.
    void SuspendForCapture();
    void ResumeFromCapture();
}

public sealed class HotkeyRegistrationException : Exception
{
    public HotkeyRegistrationException(string message) : base(message) { }
    public HotkeyRegistrationException(string message, Exception inner) : base(message, inner) { }
}
