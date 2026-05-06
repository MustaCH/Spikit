using Spikit.Models;

namespace Spikit.Services.Hotkey;

public interface IHotkeyService : IDisposable
{
    // Registra el hotkey global. Lanza HotkeyRegistrationException si la combinación
    // ya está tomada por otra app (CB-7: el caller decide cómo mostrar el error al usuario).
    void Register(HotkeyDefinition definition);

    // Idempotente: llamar sin haber registrado no hace nada.
    void Unregister();

    // Disparado en el press completo de la combinación. Para push-to-talk, marca el
    // inicio de la sesión de captura.
    event EventHandler? HotkeyPressed;

    // Disparado cuando alguna de las teclas de la combinación se libera. Para push-to-talk,
    // marca el final de la sesión de captura. Solo se dispara una vez por press.
    event EventHandler? HotkeyReleased;
}

public sealed class HotkeyRegistrationException : Exception
{
    public HotkeyRegistrationException(string message) : base(message) { }
    public HotkeyRegistrationException(string message, Exception inner) : base(message, inner) { }
}
