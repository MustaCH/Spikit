namespace Spikit.ViewModels.Onboarding;

// Pasos del wizard de onboarding (F1).
//
// Welcome (1.0) es preludio: no entra al stepper visible (●━○━○).
// Provider/Hotkey/Prueba son los 3 pasos numerados que ve el usuario en el indicador.
//
// Spec: docs/flows.md FLOW 1, docs/design-system.md §11.1.
public enum OnboardingStep
{
    Welcome = 0,
    Provider = 1,
    Hotkey = 2,
    Prueba = 3,
}
