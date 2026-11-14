using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Genie.App.Controls;
using Genie.App.Highlighting;
using Genie.App.ViewModels;
using Genie.App.Views;
using Genie.Core.Extensions.Builtin;
using Genie.Core.Presets;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Hands-strip bars (public #405): Fluent's ProgressBar has MinWidth=200 on
/// :horizontal, so a bar inside a ~100 px frame was measured at 200 px and drawn
/// past it. The bars now override the minimum and stretch to the strip's height
/// like the S / L / R boxes. The cast bar is coloured by the castbar preset (#406).
/// </summary>
public class HandsStripBarsHeadlessTests : IDisposable
{
    private readonly string _dir;

    public HandsStripBarsHeadlessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie_hsbars_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [AvaloniaFact]
    public void Rested_exp_gauge_stays_inside_its_frame_and_fills_the_row_height()
    {
        var vm = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);
        vm.Display.ShowRestedExpBar = true;
        vm.Vitals.ApplyRestedExp(new RestedExpReading(
            TimeSpan.FromHours(2), TimeSpan.FromHours(1), TimeSpan.FromHours(5)));

        // A 30 px row, like a hands strip with the Enhanced icons (28 x 32) on.
        var view = new RestedExpBarView { DataContext = vm, Height = 30 };
        using var host = new Hosted<StackPanel>(new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Children    = { view },
        });

        var frame = view.GetLogicalDescendants().OfType<Border>().First();
        var bar   = view.GetLogicalDescendants().OfType<ProgressBar>().Single();

        Assert.True(frame.IsVisible);
        Assert.True(bar.Bounds.Width <= frame.Bounds.Width,
            $"bar {bar.Bounds.Width} px is wider than its {frame.Bounds.Width} px frame");
        Assert.True(bar.Bounds.Height >= 26,
            $"bar is {bar.Bounds.Height} px tall in a 30 px row; it should stretch");
    }

    [AvaloniaFact]
    public void Cast_bar_uses_the_castbar_preset_stock_magenta()
    {
        var prev = DefaultHighlights.PresetEngine;
        DefaultHighlights.PresetEngine = new PresetEngine();
        try
        {
            var bar = new ProgressBar();
            PresetBar.SetPreset(bar, "castbar");
            PresetBar.Apply(bar);

            Assert.Equal(Colors.Magenta, Assert.IsAssignableFrom<ISolidColorBrush>(bar.Foreground).Color);
        }
        finally { DefaultHighlights.PresetEngine = prev; }
    }
}
