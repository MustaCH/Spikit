namespace Spikit.Models;

// Plan de suscripción del usuario. V1 solo expone BYOK; Pro queda dormante hasta que
// exista backend (architecture.md ya lo documenta como decisión arquitectónica).
public enum Plan
{
    BYOK = 0,
    Pro = 1,
}
