using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;

namespace Genie.App.Docking;

/// <summary>
/// Docked panel banner visibility (#302 / #320) — the accent bar above a dock
/// group's content carrying the active panel's name.
///
/// <para><b>Why the collapse is an assignment, not a Setter.</b> The header part
/// is already owned by a local value on the float side: #181's Hide Title Bar
/// assigns <c>IsVisible</c> on these same parts directly. A Style setter loses to
/// a local value, so styling the part here would work on docked chromes and then
/// be silently overridden on any panel the user had floated and re-docked. Making
/// both paths write the same way — and having this one skip floats outright —
/// keeps a single owner per chrome instead of two mechanisms racing.</para>
///
/// <para><b>Why a style still starts it.</b> <see cref="ManagedProperty"/> is set
/// by a single App.axaml rule matching every <c>ToolChromeControl</c>. Chromes are
/// created and destroyed as panels open, float, re-dock and layouts load, so a
/// one-shot walk of the visual tree would go stale the moment the user opened a
/// panel. Letting the styling system hand us each chrome as it appears keeps the
/// coverage automatic.</para>
///
/// <para>Floating chromes are skipped: on a float the same bar IS the window title
/// bar, and #181 already owns it per-window.</para>
/// </summary>
public static class BannerChrome
{
    /// <summary>Attached marker; App.axaml sets it on every ToolChromeControl.
    /// Setting it registers the chrome for banner management.</summary>
    public static readonly AttachedProperty<bool> ManagedProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Managed", typeof(BannerChrome));

    public static void SetManaged(Control c, bool value) => c.SetValue(ManagedProperty, value);
    public static bool GetManaged(Control c) => c.GetValue(ManagedProperty);

    /// <summary>Live banner visibility, mirrored from
    /// <c>DisplaySettings.ShowWindowBanners</c>.</summary>
    private static bool _visible = true;

    /// <summary>Chromes currently under management. Weak so a closed panel's chrome
    /// is collectable — the dock creates and drops these freely.</summary>
    private static readonly List<WeakReference<ToolChromeControl>> Tracked = new();

    static BannerChrome()
    {
        ManagedProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            if (e.NewValue is not true || c is not ToolChromeControl chrome) return;
            Track(chrome);
        });
    }

    private static void Track(ToolChromeControl chrome)
    {
        lock (Tracked)
        {
            Tracked.RemoveAll(w => !w.TryGetTarget(out var t) || ReferenceEquals(t, chrome));
            Tracked.Add(new WeakReference<ToolChromeControl>(chrome));
        }

        // The header parts only exist once the template has been applied, and the
        // chrome may be re-templated later (theme switch, re-dock).
        chrome.TemplateApplied        -= OnTemplateApplied;
        chrome.TemplateApplied        += OnTemplateApplied;
        chrome.AttachedToVisualTree   -= OnAttached;
        chrome.AttachedToVisualTree   += OnAttached;
        Apply(chrome);
    }

    private static void OnTemplateApplied(object? sender, EventArgs e)
    {
        if (sender is ToolChromeControl c) Apply(c);
    }

    private static void OnAttached(object? sender, EventArgs e)
    {
        if (sender is ToolChromeControl c) Apply(c);
    }

    /// <summary>Set the global banner visibility and push it to every live chrome.
    /// Called from the host when <c>DisplaySettings.ShowWindowBanners</c> changes.</summary>
    public static void SetVisible(bool visible)
    {
        _visible = visible;
        ApplyAll();
    }

    private static void ApplyAll()
    {
        ToolChromeControl[] live;
        lock (Tracked)
        {
            Tracked.RemoveAll(w => !w.TryGetTarget(out _));
            var list = new List<ToolChromeControl>(Tracked.Count);
            foreach (var w in Tracked)
                if (w.TryGetTarget(out var c)) list.Add(c);
            live = list.ToArray();
        }
        foreach (var c in live) Apply(c);
    }

    /// <summary>Collapse or restore one chrome's header band.
    ///
    /// <para>Targets only DIRECT children of the chrome's template-root Grid. The
    /// app's own ToolControl skin also names a Border <c>PART_Border</c>, but that
    /// one lives inside the content presenter — a blanket descendant search would
    /// blank the panel's contents instead of its banner (the mistake #181's first
    /// cut made).</para>
    ///
    /// <para><c>PART_Panel</c>, the divider beside the band, is deliberately left
    /// alone: it ships hidden when docked and keeps its own state logic, so driving
    /// it from this flag would invent a separator whenever banners were on.</para></summary>
    private static void Apply(ToolChromeControl chrome)
    {
        // A float's header is its title bar; #181 owns that per-window.
        if (chrome.GetValue(ToolChromeControl.IsFloatingProperty)) return;

        Grid? root = null;
        foreach (var child in chrome.GetVisualChildren())
            if (child is Grid g) { root = g; break; }
        if (root is null) return;

        foreach (var child in root.GetVisualChildren())
            if (child is Control { Name: "PART_Border" } band)
                band.IsVisible = _visible;
    }
}
