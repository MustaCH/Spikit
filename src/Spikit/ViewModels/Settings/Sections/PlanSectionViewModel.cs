using Spikit.Models;
using Spikit.Services.PlanInfo;

namespace Spikit.ViewModels.Settings.Sections;

// VM read-only de la sección Plan (US-6.1). Toda la información viene de
// IPlanService.GetCurrent(). En V1 siempre devuelve Lifetime; el día que exista
// backend con Free / Pro (V2), esta VM se extiende sumando ramas al switch sin
// cambiar la forma del componente.
//
// El botón "Pasar a Pro" queda disabled (CanUpgradeToPro=false) en V1 — funciona
// como teaser del plan futuro mientras no exista el flujo de pago.
public sealed class PlanSectionViewModel : ViewModelBase
{
    public PlanSectionViewModel(IPlanService planService)
    {
        ArgumentNullException.ThrowIfNull(planService);
        CurrentPlan = planService.GetCurrent();
    }

    public Plan CurrentPlan { get; }

    public string PlanLabel => CurrentPlan switch
    {
        Plan.Lifetime => "Lifetime access",
        _ => CurrentPlan.ToString(),
    };

    public string PlanTitle => $"Plan actual: {PlanLabel}";

    public string PlanDescription => CurrentPlan switch
    {
        Plan.Lifetime => "Tenés acceso de por vida a Spikit con tu propia API key. Cuando lancemos el plan Pro vas a poder pasarte sin reconfigurar nada.",
        _ => string.Empty,
    };

    public bool CanUpgradeToPro => false;
}
