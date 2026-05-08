namespace Spikit.Models;

// Severidad del toast bottom-right (FLOW 5 / §9.18 design-system). Define el color
// del dot a la izquierda y el tono general del feedback. Success queda reservado para
// V2 (no se usa en V1, ver flows.md FLOW 5).
public enum ToastSeverity
{
    Info,
    Warning,
    Error,
    Success,
}
