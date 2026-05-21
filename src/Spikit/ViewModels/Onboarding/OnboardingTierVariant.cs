namespace Spikit.ViewModels.Onboarding;

// Variante del wizard de onboarding según el tier del entitlement del usuario.
// EP-11.5 / ADR-0008 — el wizard se bifurca en dos shapes:
//
//   - Byok          → 3 pasos (Welcome BYOK → Provider → Hotkey → Prueba → Completed)
//   - Trial / Pro   → 2 pasos (Welcome Trial-or-Pro → Hotkey → Prueba → Completed)
//                     (salta Provider — Spikit gestiona la API key)
//
// La decisión se toma una vez al construir el OnboardingViewModel (snapshot del tier).
// Si el tier cambia mid-wizard (ej. webhook de Stripe llega y promueve trial → pro
// durante el setup), el wizard continúa con la variante original; el upgrade se refleja
// recién al terminar y entrar al shell. Cambiar el copy del Welcome a media pantalla
// sería disonante; no vale la complejidad (documentado en design-system §10.13).
public enum OnboardingTierVariant
{
    // Variante "A" — 3 pasos. Default seguro si no hay entitlement cacheado al construir
    // el VM (incluye el Provider step donde el user puede ingresar su API key).
    Byok,

    // Variante "B" — 2 pasos. Copy del Welcome variable ("Tenés 14 días...").
    Trial,

    // Variante "B" — 2 pasos. Copy del Welcome celebratorio ("Gracias por pasarte a Pro 🚀").
    Pro,
}
