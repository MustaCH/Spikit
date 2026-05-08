namespace Spikit.Models;

// Plan de suscripción del usuario. V1 solo expone Lifetime (acceso de por vida con
// API key propia, distribuido por invitación). Free y Pro se agregan en V2 con backend
// — RN-7 cumplido porque sumar valores al enum + nueva implementación de IPlanService
// no toca la abstracción.
public enum Plan
{
    Lifetime = 0,
}
