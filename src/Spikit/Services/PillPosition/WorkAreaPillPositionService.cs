using System.Windows;
using Spikit.Models;

namespace Spikit.Services.PillPosition;

// Implementación V1: usa SystemParameters.WorkArea (monitor primario) y aplica el margen
// de 32 DIPs en cada lado interior. Para multi-monitor "según target window" extender con
// un parámetro hwnd y usar System.Windows.Forms.Screen.FromHandle.
public sealed class WorkAreaPillPositionService : IPillPositionService
{
    public const double DefaultEdgeMarginDips = 32.0;

    public double EdgeMarginDips => DefaultEdgeMarginDips;

    public PillPlacement Calculate(PillAnchor anchor, double pillWidth, double pillHeight)
    {
        var workArea = SystemParameters.WorkArea;
        var left = anchor switch
        {
            PillAnchor.TopLeft or PillAnchor.BottomLeft => workArea.Left + EdgeMarginDips,
            PillAnchor.TopRight or PillAnchor.BottomRight => workArea.Right - pillWidth - EdgeMarginDips,
            _ => workArea.Left + (workArea.Width - pillWidth) / 2,
        };
        var top = anchor switch
        {
            PillAnchor.TopLeft or PillAnchor.TopCenter or PillAnchor.TopRight =>
                workArea.Top + EdgeMarginDips,
            _ => workArea.Bottom - pillHeight - EdgeMarginDips,
        };
        return new PillPlacement(left, top);
    }
}
