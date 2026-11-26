using System;
using Genie.App.Controls;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Map wheel zoom follows how far the wheel moved (#449). It used to apply one
/// full step per wheel event and read only the sign of the delta, so a
/// high-resolution wheel or a touchpad, which send many small-delta events for
/// one notch, zoomed several levels at a touch.
/// </summary>
public class MapCanvasWheelZoomTests
{
    private const double Step = 1.20;

    [Fact]
    public void One_whole_notch_is_one_step_either_way()
    {
        Assert.Equal(Step, MapCanvas.WheelZoomFactor(1), 10);
        Assert.Equal(1 / Step, MapCanvas.WheelZoomFactor(-1), 10);
    }

    [Theory]
    [InlineData(8)]     // a high-resolution wheel: 8 events of 1/8 notch
    [InlineData(15)]    // a touchpad's stream of small deltas
    [InlineData(120)]
    public void A_notch_delivered_as_many_small_events_still_adds_up_to_one_step(int events)
    {
        double zoomIn = 1.0, zoomOut = 1.0;
        for (int i = 0; i < events; i++)
        {
            zoomIn  *= MapCanvas.WheelZoomFactor(1.0 / events);
            zoomOut *= MapCanvas.WheelZoomFactor(-1.0 / events);
        }
        Assert.Equal(Step, zoomIn, 6);
        Assert.Equal(1 / Step, zoomOut, 6);
    }

    [Fact]
    public void A_small_scroll_is_a_small_zoom()
    {
        var f = MapCanvas.WheelZoomFactor(0.1);
        Assert.True(f > 1.0 && f < 1.03, $"a tenth of a notch zoomed by {f}");
    }

    [Fact]
    public void No_vertical_movement_does_not_zoom()
    {
        // A sideways scroll arrives with Delta.Y = 0; it used to zoom out.
        Assert.Equal(1.0, MapCanvas.WheelZoomFactor(0));
        Assert.Equal(1.0, MapCanvas.WheelZoomFactor(double.NaN));
    }

    [Fact]
    public void One_event_never_zooms_more_than_three_notches()
    {
        Assert.Equal(Math.Pow(Step, 3), MapCanvas.WheelZoomFactor(50), 10);
        Assert.Equal(Math.Pow(Step, -3), MapCanvas.WheelZoomFactor(-50), 10);
    }
}
