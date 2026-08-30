using Dock.Model.Core;

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
