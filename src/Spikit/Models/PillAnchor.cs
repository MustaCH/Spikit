namespace Spikit.Models;

// Anchor de la DictationPillWindow respecto al monitor activo (D-1 de docs/flows.md).
// El selector visual 3×2 mapea:
//   TopLeft    | TopCenter    | TopRight
//   BottomLeft | BottomCenter | BottomRight
// BottomCenter es default V1 (decisión cerrada en flows.md D-1). Margen del borde: 32 px
// — ver IPillPositionService para el cálculo (DPI-aware vía SystemParameters.WorkArea).
public enum PillAnchor
{
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}
