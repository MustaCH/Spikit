using Spikit.Models;

namespace Spikit.Services.PlanInfo;

// Servicio que reporta el plan actual del usuario. V1 retorna siempre Lifetime desde
// un valor hardcoded (todos los users de V1 son invitados con acceso de por vida);
// cuando exista backend con planes Free / Pro (V2), esta interfaz se cubre con una
// implementación que consulte el server / cache local. El componente UI no cambia
// estructura — solo la fuente de la verdad detrás del IPlanService.
public interface IPlanService
{
    Plan GetCurrent();
}
