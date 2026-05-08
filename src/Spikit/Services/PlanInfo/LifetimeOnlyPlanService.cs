using Spikit.Models;

namespace Spikit.Services.PlanInfo;

// Implementación V1: todos los usuarios caen en Lifetime (la app es BYOK-only y se
// distribuye por invitación, así que cada instalación es de facto Lifetime). El upsell
// a Pro vive dormante hasta que exista backend que lo venda. Cuando llegue, agregamos
// una HttpPlanService que reemplaza este registro en DI y el resto del código no se
// entera.
public sealed class LifetimeOnlyPlanService : IPlanService
{
    public Plan GetCurrent() => Plan.Lifetime;
}
