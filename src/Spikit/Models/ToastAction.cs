namespace Spikit.Models;

// Acción opcional del toast (link/button a la derecha del mensaje). Una sola acción por toast
// — heurística #8 (minimalismo) en flows.md FLOW 5. El callback se ejecuta y el toast se
// cierra inmediatamente. Si la acción todavía no está implementada (típico durante desarrollo
// si EP-5.3 corre antes de EP-4), el callback puede tirar NotImplementedException — el toast
// service lo loguea pero no propaga.
public sealed record ToastAction(string Label, Action OnInvoke);
