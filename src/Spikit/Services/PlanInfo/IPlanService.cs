using Spikit.Models;

namespace Spikit.Services.PlanInfo;

// Servicio que reporta el plan actual del usuario. V1 retorna siempre BYOK desde un
// valor hardcoded; cuando exista backend Pro (post-V1), esta interfaz se cubre con
// una implementación que consulte el server / cache local. El componente UI no cambia
// estructura — solo la fuente de la verdad detrás del IPlanService.
public interface IPlanService
{
    Plan GetCurrent();
}
