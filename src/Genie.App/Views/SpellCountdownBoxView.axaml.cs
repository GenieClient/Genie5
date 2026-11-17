using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace Genie.App.Views;

/// <summary>Code-behind for <c>SpellCountdownBoxView.axaml</c>: the combined
/// spell box on the hands strip (#405). Splits the label at the fill's edge so
/// the part over the fill is white and the part over the track keeps the spell
/// label's colour. All state lives on <c>VitalsViewModel</c>.</summary>
public partial class SpellCountdownBoxView : UserControl
{
    private readonly ProgressBar _bar;
    private readonly TextBlock   _fillText;
    private readonly TextBlock   _trackText;

    public SpellCountdownBoxView()
    {
        AvaloniaXamlLoader.Load(this);
        _bar       = this.FindControl<ProgressBar>("Bar")!;
        _fillText  = this.FindControl<TextBlock>("FillText")!;
        _trackText = this.FindControl<TextBlock>("TrackText")!;

        _bar.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty || e.Property == RangeBase.MaximumProperty ||
                e.Property == RangeBase.MinimumProperty || e.Property == BoundsProperty ||
                e.Property == IsVisibleProperty)
                UpdateSplit();
        };
        _trackText.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) UpdateSplit();
        };
        UpdateSplit();
    }

    /// <summary>Where the fill ends, in the labels' own coordinates.</summary>
    internal double SplitX { get; private set; }

    private void UpdateSplit()
    {
        var range    = _bar.Maximum - _bar.Minimum;
        var fraction = _bar.IsVisible && range > 0
            ? Math.Clamp((_bar.Value - _bar.Minimum) / range, 0, 1)
            : 0;
        var text  = _trackText.Bounds;
        var split = Math.Max(0, _bar.Bounds.Width * fraction - text.X);
        SplitX = split;

        // Generous vertical extent so descenders are never clipped.
        const double pad = 50;
        _fillText.Clip  = new RectangleGeometry(new Rect(-pad, -pad, split + pad, text.Height + 2 * pad));
        _trackText.Clip = new RectangleGeometry(new Rect(split, -pad, Math.Max(0, text.Width - split) + pad, text.Height + 2 * pad));
    }
}
