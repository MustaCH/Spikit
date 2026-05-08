using System.Windows;
using Spikit.Models;
using Spikit.Services.PillPosition;

namespace Spikit.Tests.Services.PillPosition;

public class WorkAreaPillPositionServiceTests
{
    private const double PillWidth = 200;
    private const double PillHeight = 60;
    private const double Margin = WorkAreaPillPositionService.DefaultEdgeMarginDips;

    [Fact]
    public void TopLeft_anchors_to_workArea_top_left_with_margin()
    {
        var sut = new WorkAreaPillPositionService();
        var wa = SystemParameters.WorkArea;

        var placement = sut.Calculate(PillAnchor.TopLeft, PillWidth, PillHeight);

        Assert.Equal(wa.Left + Margin, placement.Left, precision: 1);
        Assert.Equal(wa.Top + Margin, placement.Top, precision: 1);
    }

    [Fact]
    public void TopRight_anchors_to_workArea_top_right_with_margin()
    {
        var sut = new WorkAreaPillPositionService();
        var wa = SystemParameters.WorkArea;

        var placement = sut.Calculate(PillAnchor.TopRight, PillWidth, PillHeight);

        Assert.Equal(wa.Right - PillWidth - Margin, placement.Left, precision: 1);
        Assert.Equal(wa.Top + Margin, placement.Top, precision: 1);
    }

    [Fact]
    public void BottomCenter_horizontally_centers_within_workArea()
    {
        var sut = new WorkAreaPillPositionService();
        var wa = SystemParameters.WorkArea;

        var placement = sut.Calculate(PillAnchor.BottomCenter, PillWidth, PillHeight);

        Assert.Equal(wa.Left + (wa.Width - PillWidth) / 2, placement.Left, precision: 1);
        Assert.Equal(wa.Bottom - PillHeight - Margin, placement.Top, precision: 1);
    }

    [Fact]
    public void BottomLeft_anchors_to_workArea_bottom_left()
    {
        var sut = new WorkAreaPillPositionService();
        var wa = SystemParameters.WorkArea;

        var placement = sut.Calculate(PillAnchor.BottomLeft, PillWidth, PillHeight);

        Assert.Equal(wa.Left + Margin, placement.Left, precision: 1);
        Assert.Equal(wa.Bottom - PillHeight - Margin, placement.Top, precision: 1);
    }

    [Fact]
    public void BottomRight_anchors_to_workArea_bottom_right()
    {
        var sut = new WorkAreaPillPositionService();
        var wa = SystemParameters.WorkArea;

        var placement = sut.Calculate(PillAnchor.BottomRight, PillWidth, PillHeight);

        Assert.Equal(wa.Right - PillWidth - Margin, placement.Left, precision: 1);
        Assert.Equal(wa.Bottom - PillHeight - Margin, placement.Top, precision: 1);
    }

    [Fact]
    public void TopCenter_horizontally_centers_at_top()
    {
        var sut = new WorkAreaPillPositionService();
        var wa = SystemParameters.WorkArea;

        var placement = sut.Calculate(PillAnchor.TopCenter, PillWidth, PillHeight);

        Assert.Equal(wa.Left + (wa.Width - PillWidth) / 2, placement.Left, precision: 1);
        Assert.Equal(wa.Top + Margin, placement.Top, precision: 1);
    }
}
