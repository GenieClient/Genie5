using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;

namespace Genie.App.Settings;

/// <summary>
/// "Always show scrollbars" (public #365). Avalonia's scrollbars auto-hide to a
/// thin line when idle and expand only on hover — a small target that has to be
/// acquired twice, which is unreliable with a trackball, a touchpad or a tremor.
///
/// <para><b>Why the style targets every templated control, not just ScrollViewer.</b>
/// A <c>ScrollViewer</c> selector reaches only bare ScrollViewers: the Fluent
/// ListBox, TextBox (and other scrolling controls') templates TemplateBind their
/// inner ScrollViewer's <c>AllowAutoHide</c> to the OWNING control's attached
/// <c>ScrollViewer.AllowAutoHide</c>, and a template binding outranks a style
/// setter. Setting the attached property on every templated control feeds both
/// paths — the bare ScrollViewer reads it as its own property, and every
/// templated owner passes it down. No control in the app sets the property
/// locally, so nothing outranks the setter.</para>
///
/// <para>Added to <see cref="Application.Styles"/> rather than a window, so it
/// reaches floating panels and dialogs, which are separate top-level windows.
/// Removing it restores the stock auto-hide.</para>
/// </summary>
public static class ScrollBarAutoHide
{
    private static readonly Style AlwaysVisibleStyle = Build();

    private static Style Build()
    {
        var style = new Style(x => x.Is<TemplatedControl>());
        style.Setters.Add(new Setter(ScrollViewer.AllowAutoHideProperty, false));
        return style;
    }

    /// <summary>Apply or remove the always-visible rule app-wide. Called from
    /// <c>DisplaySettings.Apply</c> whenever <c>AlwaysShowScrollbars</c> changes.</summary>
    public static void SetAlwaysVisible(bool alwaysVisible)
    {
        var styles = Application.Current?.Styles;
        if (styles is null) return;
        bool present = styles.Contains(AlwaysVisibleStyle);
        if (alwaysVisible && !present) styles.Add(AlwaysVisibleStyle);
        else if (!alwaysVisible && present) styles.Remove(AlwaysVisibleStyle);
    }
}
