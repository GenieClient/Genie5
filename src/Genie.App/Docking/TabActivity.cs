using System.Collections.Specialized;
using System.ComponentModel;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Genie.Core.Layout;

namespace Genie.App.Docking;

/// <summary>
/// Unread-activity flash for tabbed dock windows (Genie 4 tab-flash parity).
///
/// <para>When a line lands in a window whose tab is NOT the front tab of its
/// dock, the user has no way to know — the text sits invisibly behind the
/// active tab. Tools with a live text feed call
/// <see cref="NotifyContentAdded"/> on every appended line; if the tab is not
/// currently in front, the dockable's <see cref="IDockable.IsModified"/> flag
/// is raised, which the tab header renders as a blinking title (the
/// <c>unread</c> class + animation in App.axaml). The flag clears in the
/// tool's <c>OnSelected()</c> override — Dock calls that on every activation
/// path, including the tab strip's TwoWay <c>SelectedItem</c> binding (plain
/// <c>ActiveDockable</c> property sets route through
/// <c>Factory.InitActiveDockable</c>; verified against Dock 11.3.11).</para>
///
/// <para><see cref="IDockable.IsModified"/> is Dock's document-dirty flag —
/// nothing in Genie uses that semantic, so tool tabs repurpose it: it is
/// already INPC-backed on every dockable, visible to compiled bindings typed
/// <c>core:IDockable</c> (no reflection-binding noise for tools that never
/// set it), and transient (not persisted by layout snapshots).</para>
///
/// <para>A tool that is alone in its dock is always its dock's
/// <c>ActiveDockable</c>, so it never flags — correct, its content is
/// visible. A tool with no owner yet (panel closed, dock not built) DOES
/// flag; if reopening activates it the flag clears immediately via
/// <c>OnSelected</c> (it is now visible), and if it lands behind another tab
/// it blinks until viewed.</para>
/// </summary>
internal static class TabActivity
{
    public static void NotifyContentAdded(IDockable tool)
    {
        if (tool.Owner is IDock d && ReferenceEquals(d.ActiveDockable, tool))
            return; // tab is in front — the new line is already on screen
        tool.IsModified = true;
    }
}

/// <summary>
/// Base for every dock tool whose tab should flash on unread activity. Each
/// tool wires its own data signal in its constructor via
/// <see cref="WireActivity(INotifyCollectionChanged)"/> (line/row collections;
/// Add events only, so trims, Clears, and the highlight-repaint Replace
/// pattern don't count as activity) or
/// <see cref="WireActivity(INotifyPropertyChanged, string[])"/> (Reactive/INPC
/// view models whose properties only raise when the value actually changes).
/// The <see cref="OnSelected"/> override clears the flag on every activation
/// path — see <see cref="TabActivity"/> for the full design.
///
/// <para>Deliberately NOT wired: TimeTrackerTool (its content is a clock —
/// re-rendered every second, it would blink forever) and AnalyticsTool (loads
/// data only on user interaction; nothing arrives while it sits in the
/// background).</para>
/// </summary>
public abstract class ActivityTool : Tool
{
    private WindowSettings? _activitySettings;

    /// <summary>
    /// The window's live settings, consulted for the per-window
    /// <see cref="WindowSettings.FlashOnActivity"/> toggle. Assigned in each
    /// tool's constructor (the same instance the Layout tab and the window
    /// menu mutate, so the toggle applies live with no re-subscribe). Null —
    /// e.g. a tool built before its settings registration — reads as
    /// "flash on", matching the property's default.
    /// </summary>
    public WindowSettings? ActivitySettings
    {
        get => _activitySettings;
        protected set
        {
            _activitySettings = value;
            if (value is not null)
                // Turning the toggle OFF also stops an already-running flash;
                // other settings changes leave an active flash alone.
                value.Changed += () => { if (!value.FlashOnActivity) IsModified = false; };
        }
    }

    private bool FlashEnabled => _activitySettings?.FlashOnActivity != false;

    protected void WireActivity(INotifyCollectionChanged source) =>
        source.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && FlashEnabled)
                TabActivity.NotifyContentAdded(this);
        };

    // A required first property keeps this overload unambiguous against the
    // collection one — ObservableCollection implements BOTH interfaces.
    protected void WireActivity(INotifyPropertyChanged source, string property, params string[] moreProperties)
    {
        var names = new HashSet<string>(moreProperties, StringComparer.Ordinal) { property };
        source.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null && names.Contains(e.PropertyName) && FlashEnabled)
                TabActivity.NotifyContentAdded(this);
        };
    }

    /// <summary>Dock calls this on every activation path (tab click, window
    /// cycling, SetActiveDockable) — the tab is now in front, so the unread
    /// flash stops.</summary>
    public override void OnSelected()
    {
        IsModified = false;
        base.OnSelected();
    }
}
