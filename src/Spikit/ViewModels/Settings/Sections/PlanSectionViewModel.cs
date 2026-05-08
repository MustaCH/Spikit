using Spikit.Models;
using Spikit.Services.PlanInfo;

namespace Spikit.ViewModels.Settings.Sections;

// VM read-only de la sección Plan (EP-4.9 / US-6.1). Toda la información viene de
// IPlanService.GetCurrent(). En V1 siempre devuelve BYOK; el día que exista backend
// Pro, esta VM no cambia — solo refleja el plan real.
//
// El botón "Pasar a Pro" queda disabled (CanUpgradeToPro=false) hasta que exista el
// flujo de pago. EP-7 es el ticket dormante que va a habilitar este upsell cuando
// llegue el momento.
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
        Plan.BYOK => "BYOK",
        Plan.Pro => "Pro",
        _ => CurrentPlan.ToString(),
    };

    public string PlanTitle => $"Plan actual: {PlanLabel}";

    public string PlanDescription => CurrentPlan switch
    {
        Plan.BYOK => "Estás usando tu propia API key. Cuando lancemos el plan Pro vas a poder pasarte sin reconfigurar nada.",
        Plan.Pro => "Estás en el plan Pro. Tus dictados se procesan con la API key gestionada de Spikit.",
        _ => string.Empty,
    };

    // V1 siempre devuelve false — el flujo de upgrade vive dormante hasta EP-7.
    public bool CanUpgradeToPro => false;
}
