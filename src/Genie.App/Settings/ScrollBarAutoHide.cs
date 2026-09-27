using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml.MarkupExtensions;
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
/// <para><b>Why the style is installed once and a resource is toggled, rather
/// than adding and removing the style.</b> The first cut added the style to turn
/// the setting on and removed it to turn it off. Adding reached every control,
/// but removing did NOT reach controls hosted inside the dock: the Game window's
/// ScrollViewer kept <c>AllowAutoHide = false</c> after the style was gone, so its
/// scrollbar stayed wide until a restart (live walk 2026-09-26; reproduced headless
/// against the production dock). Application style removal propagates by walking
/// the tree, and the dock's hosted content is not reached by that walk; resource
/// changes are delivered to every control that holds a <c>DynamicResource</c>,
/// the same channel the echo colour and theme palette already use live in the
/// dock. So the setter's value is a <c>DynamicResource</c> on
/// <see cref="ResourceKey"/>, and turning the setting on or off only writes that
/// resource.</para>
///
/// <para>Installed on first use and then left in place: someone who never turns
/// the setting on never gets the style at all. Once installed with the setting
/// off, the value it applies is <c>true</c>, which is Avalonia's own default.</para>
///
/// <para>Added to <see cref="Application.Styles"/> rather than a window, so it
/// reaches floating panels and dialogs, which are separate top-level windows.</para>
/// </summary>
public static class ScrollBarAutoHide
{
    /// <summary>The <see cref="Application.Resources"/> key the style's setter reads:
    /// <c>true</c> = Avalonia's auto-hide, <c>false</c> = always full width.</summary>
    public const string ResourceKey = "Genie.ScrollBars.AllowAutoHide";

    // One style per Application: a Style can have only one owner, and the headless
    // test runner hands each test a fresh Application.
    private static readonly ConditionalWeakTable<Application, Style> Installed = new();

    private static Style Build()
    {
        var style = new Style(x => x.Is<TemplatedControl>());
        style.Setters.Add(new Setter(ScrollViewer.AllowAutoHideProperty, new DynamicResourceExtension(ResourceKey)));
        return style;
    }

    /// <summary>Whether the always-visible rule is installed on the current
    /// application (it is never removed once installed — see the remarks).</summary>
    public static bool IsInstalled =>
        Application.Current is { } app && Installed.TryGetValue(app, out _);

    /// <summary>Apply the setting app-wide. Called from <c>DisplaySettings.Apply</c>
    /// whenever <c>AlwaysShowScrollbars</c> changes.</summary>
    public static void SetAlwaysVisible(bool alwaysVisible)
    {
        var app = Application.Current;
        if (app is null) return;

        // Resource first, so the style's first evaluation already sees the value.
        if (!app.Resources.TryGetValue(ResourceKey, out var current) || current is not bool b || b == alwaysVisible)
            app.Resources[ResourceKey] = !alwaysVisible;

        if (alwaysVisible && !Installed.TryGetValue(app, out _))
        {
            var style = Build();
            app.Styles.Add(style);
            Installed.Add(app, style);
        }
    }
}
