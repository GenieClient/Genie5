using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Genie.App.Highlighting;
using Genie.App.Settings;
using Genie.Core;

namespace Genie.App.Controls;

/// <summary>
/// Paints window chrome from Genie 4's <c>ui.*</c> presets (public #406), on the
/// surfaces Genie 4 used them for (<c>FormMain.cs</c> 141–153,
/// <c>MenuRenderer.cs</c>, <c>FormMapperSettings.Recolor</c>):
/// <code>&lt;Menu ctl:PresetChrome.Surface="menu" … /&gt;</code>
/// <list type="table">
///   <item><c>menu</c>: <c>ui.menu</c> (menu bar and its menus),
///         <c>ui.menu.highlight</c> (item under the mouse),
///         <c>ui.menu.checked</c> (behind a tick)</item>
///   <item><c>textbox</c>: <c>ui.textbox</c> (the command input)</item>
///   <item><c>status</c>: <c>ui.status</c> (the <c>#statusbar</c> strip)</item>
///   <item><c>window</c>: <c>ui.window</c> and <c>ui.button</c> (the AutoMapper
///         Settings window and its buttons)</item>
/// </list>
///
/// <para>The theme colours these surfaces. A preset set to a colour paints its
/// surface over the theme by overriding, on that control only, the resource keys
/// the theme and Fluent use for it (every state: normal, hover, pressed,
/// focused), so nothing else in the app changes. "Default" (the stock value)
/// removes the override and the theme's look comes back. Surfaces repaint live
/// whenever presets change (<see cref="UserHighlights.RulesChanged"/>).</para>
/// </summary>
public static class PresetChrome
{
    public static readonly AttachedProperty<string?> SurfaceProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Surface", typeof(PresetChrome));

    public static string? GetSurface(Control c) => c.GetValue(SurfaceProperty);
    public static void SetSurface(Control c, string? value) => c.SetValue(SurfaceProperty, value);

    /// <summary>Where the presets are read. Set by <c>MainWindowViewModel</c> (the
    /// shared presets at startup, the session's store once Genie's engine is
    /// built); null reads the session engine, if any.</summary>
    public static PresetColorStore? Store { get; set; }

    /// <summary>Key the menu style uses for the box behind a ticked item.</summary>
    public const string MenuCheckedKey = "Genie.UiMenuChecked";

    private enum Part { Foreground, Background }

    /// <summary>What a preset colour does on a surface: set the control's own
    /// property, and/or override resource keys within the control.</summary>
    private sealed record Rule(string Preset, Part Part, AvaloniaProperty? Property, params string[] Keys);

    private static readonly Dictionary<string, Rule[]> Surfaces = new(StringComparer.OrdinalIgnoreCase)
    {
        ["menu"] =
        [
            new(UiPresets.Menu, Part.Foreground, null,
                "MenuFlyoutItemForeground", "MenuFlyoutItemForegroundPointerOver", "MenuFlyoutItemForegroundPressed",
                "MenuFlyoutItemKeyboardAcceleratorTextForeground",
                "MenuFlyoutItemKeyboardAcceleratorTextForegroundPointerOver",
                "MenuFlyoutItemKeyboardAcceleratorTextForegroundPressed",
                "MenuFlyoutSubItemChevron", "MenuFlyoutSubItemChevronPointerOver",
                "MenuFlyoutSubItemChevronPressed", "MenuFlyoutSubItemChevronSubMenuOpened"),
            new(UiPresets.Menu, Part.Background, TemplatedControl.BackgroundProperty,
                "MenuFlyoutPresenterBackground", "MenuFlyoutItemBackground"),
            new(UiPresets.MenuHighlight, Part.Foreground, null,
                "MenuFlyoutItemBackgroundPointerOver", "MenuFlyoutItemBackgroundPressed"),
            new(UiPresets.MenuChecked, Part.Foreground, null, MenuCheckedKey),
        ],
        ["textbox"] =
        [
            new(UiPresets.TextBox, Part.Foreground, null,
                "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused"),
            new(UiPresets.TextBox, Part.Background, null,
                "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused"),
        ],
        ["status"] =
        [
            new(UiPresets.Status, Part.Foreground, null, "Theme.TextPrimary"),
            new(UiPresets.Status, Part.Background, null, "Theme.StripBg"),
        ],
        ["window"] =
        [
            new(UiPresets.Window, Part.Foreground, TemplatedControl.ForegroundProperty),
            new(UiPresets.Window, Part.Background, TemplatedControl.BackgroundProperty),
            new(UiPresets.Button, Part.Foreground, null,
                "ButtonForeground", "ButtonForegroundPointerOver", "ButtonForegroundPressed"),
            new(UiPresets.Button, Part.Background, null,
                "ButtonBackground", "ButtonBackgroundPointerOver", "ButtonBackgroundPressed"),
        ],
    };

    // Surfaces currently in a visual tree; only those are held.
    private static readonly HashSet<Control> Live = new();

    static PresetChrome()
    {
        SurfaceProperty.Changed.AddClassHandler<Control>(OnSurfaceChanged);
        UserHighlights.RulesChanged += OnRulesChanged;
    }

    private static void OnSurfaceChanged(Control c, AvaloniaPropertyChangedEventArgs e)
    {
        c.AttachedToVisualTree   -= OnAttached;
        c.DetachedFromVisualTree -= OnDetached;
        if (e.NewValue is string { Length: > 0 })
        {
            c.AttachedToVisualTree   += OnAttached;
            c.DetachedFromVisualTree += OnDetached;
            if (Avalonia.VisualTree.VisualExtensions.GetVisualRoot(c) is not null) Live.Add(c);
        }
        else Live.Remove(c);
        Apply(c);
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Control c) return;
        Live.Add(c);
        Apply(c);   // presets may have changed while it was detached
    }

    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control c) Live.Remove(c);
    }

    // Always queued, never inline: a preset edit announces the change before it
    // has written presets.json, and offline the colours are read from that file.
    private static void OnRulesChanged() => Dispatcher.UIThread.Post(ApplyLive);

    private static void ApplyLive()
    {
        foreach (var c in Live.ToArray()) Apply(c);
    }

    /// <summary>Paint <paramref name="c"/> from its surface's presets now.</summary>
    public static void Apply(Control c)
    {
        // A control with no (or an unknown) surface drops every override.
        var mine  = GetSurface(c) is { } surface && Surfaces.TryGetValue(surface, out var r) ? r : null;
        var (fg, bg) = Reader();

        foreach (var rule in mine ?? AllRules)
        {
            var brush = mine is null
                ? null
                : Brush(rule.Part == Part.Foreground ? fg(rule.Preset) : bg(rule.Preset));

            if (rule.Property is { } p && AvaloniaPropertyRegistry.Instance.IsRegistered(c, p))
            {
                if (brush is null) c.ClearValue(p);
                else               c.SetValue(p, brush);
            }
            foreach (var key in rule.Keys)
            {
                if (brush is null) c.Resources.Remove(key);
                else               c.Resources[key] = brush;
            }
        }
    }

    // Every rule, so a control whose surface is cleared drops all overrides.
    private static readonly Rule[] AllRules = Surfaces.Values.SelectMany(x => x).ToArray();

    private static (Func<string, string> Fg, Func<string, string> Bg) Reader()
    {
        if (Store is { } store) return store.Reader();
        if (DefaultHighlights.PresetEngine is { } engine) return (engine.GetForeground, engine.GetBackground);
        return (_ => "", _ => "");
    }

    /// <summary>A preset colour as a brush; null for "Default", empty or unreadable.</summary>
    private static IBrush? Brush(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("Default", StringComparison.OrdinalIgnoreCase)) return null;
        return Color.TryParse(value.Trim(), out var col) ? new SolidColorBrush(col) : null;
    }
}
