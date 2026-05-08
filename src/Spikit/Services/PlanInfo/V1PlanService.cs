using Spikit.Models;

namespace Spikit.Services.PlanInfo;

// Implementación V1: siempre BYOK. El upsell al Pro vive dormante (EP-7) hasta que
// haya un backend que pueda venderlo. Cuando esto pase, agregamos una HttpPlanService
// que reemplaza este registro en DI y el resto del código no se entera.
public sealed class V1PlanService : IPlanService
{
    public Plan GetCurrent() => Plan.BYOK;
}
