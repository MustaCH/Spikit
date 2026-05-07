namespace Spikit.Models;

// Modo de operación del hotkey configurado por el usuario en US-1.2.
// Las dos semánticas conviven en runtime: HotkeyService dispara el press, el orchestrator
// decide si grabar mientras se mantenga (PTT) o alternar entre Recording / Idle (Toggle).
public enum HotkeyMode
{
    // Mantener apretado para grabar; soltar termina la grabación. Default V1 — más
    // predecible para el usuario nuevo (no hay que recordar si está grabando).
    PushToTalk = 0,

    // Una pulsación arranca, otra termina. Mejor para sesiones largas / dictado libre.
    Toggle = 1,
}
