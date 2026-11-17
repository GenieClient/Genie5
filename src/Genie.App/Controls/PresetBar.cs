using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Genie.App.Highlighting;

namespace Genie.App.Controls;

/// <summary>
/// Colours a <see cref="ProgressBar"/> from a Genie 4 preset (public #406), the
/// way Genie 4 coloured its vitals bars (<c>FormMain.cs</c>:
/// <c>ComponentBarsHealth.ForegroundColor = PresetList["health"].FgColor</c>,
/// <c>BackgroundColor</c> and <c>BorderColor</c> = <c>BgColor</c>):
/// the preset's foreground is the fill, its background the track and border.
/// <code>&lt;ProgressBar ctl:PresetBar.Preset="health" … /&gt;</code>
///
/// <para>A colour the preset doesn't set (<c>Default</c>, or no background) is
/// left to the control's own style, so a bar with no background shows the normal
/// track. Bars repaint live whenever presets change
/// (<see cref="UserHighlights.RulesChanged"/>).</para>
/// </summary>
public static class PresetBar
{
    public static readonly AttachedProperty<string?> PresetProperty =
        AvaloniaProperty.RegisterAttached<ProgressBar, string?>("Preset", typeof(PresetBar));

    public static string? GetPreset(ProgressBar bar) => bar.GetValue(PresetProperty);
    public static void SetPreset(ProgressBar bar, string? value) => bar.SetValue(PresetProperty, value);

    // Bars currently in a visual tree. Only those are held, so a closed panel's
    // bars can be collected.
    private static readonly HashSet<ProgressBar> Live = new();

    static PresetBar()
    {
        PresetProperty.Changed.AddClassHandler<ProgressBar>(OnPresetChanged);
        UserHighlights.RulesChanged += OnRulesChanged;
    }

    private static void OnPresetChanged(ProgressBar bar, AvaloniaPropertyChangedEventArgs e)
    {
        bar.AttachedToVisualTree   -= OnAttached;
        bar.DetachedFromVisualTree -= OnDetached;
        if (e.NewValue is string { Length: > 0 })
        {
            bar.AttachedToVisualTree   += OnAttached;
            bar.DetachedFromVisualTree += OnDetached;
            if (bar.IsAttachedToVisualTree()) Live.Add(bar);
        }
        else Live.Remove(bar);
        Apply(bar);
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not ProgressBar bar) return;
        Live.Add(bar);
        Apply(bar);   // presets may have changed while it was detached
    }

    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is ProgressBar bar) Live.Remove(bar);
    }

    private static void OnRulesChanged()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(OnRulesChanged); return; }
        foreach (var bar in Live.ToArray()) Apply(bar);
    }

    /// <summary>Paint <paramref name="bar"/> from its preset now.</summary>
    public static void Apply(ProgressBar bar)
    {
        var id = GetPreset(bar);
        var fill  = string.IsNullOrEmpty(id) ? null : DefaultHighlights.PresetForeground(id);
        var track = string.IsNullOrEmpty(id) ? null : DefaultHighlights.PresetBackground(id);

        if (fill is null) bar.ClearValue(TemplatedControl.ForegroundProperty);
        else              bar.Foreground = fill;

        if (track is null)
        {
            bar.ClearValue(TemplatedControl.BackgroundProperty);
            bar.ClearValue(TemplatedControl.BorderBrushProperty);
        }
        else
        {
            bar.Background  = track;
            bar.BorderBrush = track;
        }
    }

    private static bool IsAttachedToVisualTree(this Visual v) =>
        Avalonia.VisualTree.VisualExtensions.GetVisualRoot(v) is not null;
}
