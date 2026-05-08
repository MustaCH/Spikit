using Spikit.Models;

namespace Spikit.Services.PillPosition;

// Calcula (Left, Top) en DIPs (device-independent pixels, lo que WPF usa para Window.Left/Top)
// para colocar la pill según el anchor elegido por el usuario en Settings → General.
//
// V1: usa SystemParameters.WorkArea (monitor primario). WorkArea ya está en DIPs y respeta
// el DPI scaling de Windows automáticamente. Multi-monitor "según el monitor activo del
// foreground window" queda fuera de V1 — si aparece como request, se usa Screen.FromHandle
// del target hwnd.
public interface IPillPositionService
{
    // Margen del borde en DIPs. Documentado como constante porque la pill window también
    // necesita compensar el padding de su shadow (ver DictationPillWindow.WindowBottomPaddingForShadow).
    double EdgeMarginDips { get; }

    // pillWidth/pillHeight: dimensiones del Window contenedor en DIPs (no del visual interno).
    // El caller pasa Window.Width/Height. Si el Window tiene padding extra para shadow,
    // el caller debe compensar restándolo del Top devuelto.
    PillPlacement Calculate(PillAnchor anchor, double pillWidth, double pillHeight);
}

public readonly record struct PillPlacement(double Left, double Top);
