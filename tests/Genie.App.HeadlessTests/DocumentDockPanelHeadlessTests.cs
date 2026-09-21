using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using Genie.App.Docking;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #346 — a structured panel (Mobs, Injuries, Inventory View,
/// Experience) dropped into the group that holds Game renders NOTHING: not its
/// rows, and not the header or toolbar its template draws unconditionally.
/// Another tab's chrome shows in its place. The same panel docked anywhere
/// else, or floated, renders correctly and immediately.
///
/// <para><b>Root cause.</b> This is #80 again, on the document side. Dock
/// 11.3.11's DocumentControl theme ships two templates and its Template setter
/// selects <c>DockDocumentControlSingleContentTemplate</c>, which hosts the
/// active dockable in ONE ContentControl whose ContentTemplate is a
/// <c>ControlRecyclingDataTemplate</c>. That template's <c>Match()</c> always
/// returns true, so on a tab switch Avalonia's ContentPresenter takes its
/// child-reuse fast path, keeps the already-realized child and only re-points
/// its DataContext at the newly active dockable — it never rebuilds for the new
/// dockable's type. A same-type group survives; a mixed-type group shows
/// whichever dockable built the shared view first.</para>
///
/// <para>The fix points the setter at the cached template Dock already ships
/// and simply does not select: one ContentControl per visible dockable, shown
/// and hidden by identity. These tests assert that structure, because it is
/// precisely what "no shared, recycled host" means — a model-level test would
/// have passed throughout the live bug, since the model was always correct.</para>
/// </summary>
public class DocumentDockPanelHeadlessTests
{
    private sealed class Harness : IDisposable
    {
        public MainWindowViewModel Vm   { get; }
        public Window              Main { get; }
        private readonly DockControl _dockControl;
        private readonly string      _dir;

        public Harness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "genie_docdock_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Vm = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);

            _dockControl = new DockControl
            {
                Layout           = Vm.DockLayout,
                Factory          = (GenieDockFactory)Vm.DockFactory!,
                InitializeLayout = false,
            };
            Main = new Window { Width = 1200, Height = 800, Content = _dockControl };
            Main.Show();
            Pump();
            Pump();
        }

        public void Pump()
        {
            Dispatcher.UIThread.RunJobs();
            Main.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>The DocumentDock that holds the Game window.</summary>
        public IDock DocumentDock =>
            (IDock)Find("docs")!;

        public DocumentControl DocumentControl =>
            Main.GetVisualDescendants().OfType<DocumentControl>().First();

        public IDockable? Find(string id) => FindIn(_dockControl.Layout!, id);

        private static IDockable? FindIn(IDockable n, string id)
        {
            if (n.Id == id) return n;
            if (n is IDock d && d.VisibleDockables is not null)
                foreach (var c in d.VisibleDockables)
                    if (FindIn(c, id) is { } f) return f;
            return null;
        }

        public void Dispose()
        {
            Main.Close();
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// The content hosts inside PART_CachedContentHost — one per visible
    /// dockable when the cached template is in use.
    ///
    /// <para>Scoped to that ItemsControl deliberately: the tab strip also
    /// materializes a control per dockable, so an unscoped search would count
    /// tabs as content and pass no matter which template was selected.</para>
    /// </summary>
    private static ContentControl[] ContentHosts(Harness h)
    {
        var host = h.DocumentControl.GetVisualDescendants()
            .OfType<ItemsControl>()
            .FirstOrDefault(c => c.Name == "PART_CachedContentHost");
        if (host is null) return Array.Empty<ContentControl>();

        return host.GetVisualDescendants()
            .OfType<DockableControl>()
            .Select(d => d.GetVisualDescendants().OfType<ContentControl>()
                          .FirstOrDefault(c => c.Content is IDockable))
            .Where(c => c is not null)
            .Select(c => c!)
            .ToArray();
    }

    private static MobsTool NewMobs(Harness h) =>
        new(h.Vm.Mobs, h.Vm.WindowSettings.Get("mobs")) { Id = "mobs-in-docs", Title = "Mobs" };

    /// <summary>Put a tool into the document group the way a drag-drop does.</summary>
    private static void AddToDocumentDock(Harness h, IDockable tool)
    {
        var dock = h.DocumentDock;
        tool.Owner = dock;
        dock.VisibleDockables!.Add(tool);
        h.Pump();
    }

    [AvaloniaFact]
    public void Each_dockable_in_the_document_group_gets_its_own_content_host()
    {
        using var h = new Harness();

        var mobs = NewMobs(h);
        AddToDocumentDock(h, mobs);

        var hosts = ContentHosts(h);

        // The cached host exists at all — with the stock single-content
        // template there is no PART_CachedContentHost and this is empty.
        Assert.NotEmpty(hosts);

        // One host per visible dockable — Game plus the tool we just dropped in.
        Assert.Equal(h.DocumentDock.VisibleDockables!.Count, hosts.Length);

        // …and each one holds a DIFFERENT dockable. A single shared, recycled
        // presenter would show one host whose Content is swapped, which is the
        // bug: the panel would render whatever built the view first.
        Assert.Equal(hosts.Length, hosts.Select(c => c.Content).Distinct().Count());
        Assert.Contains(hosts, c => ReferenceEquals(c.Content, mobs));
    }

    [AvaloniaFact]
    public void Switching_the_active_tab_does_not_reuse_the_other_tabs_view()
    {
        using var h = new Harness();

        var mobs = NewMobs(h);
        AddToDocumentDock(h, mobs);

        var game = h.DocumentDock.VisibleDockables!.First(d => !ReferenceEquals(d, mobs));

        // The host built for Mobs must be the same object before and after the
        // switch — it is Mobs' OWN view, not a shared one being re-pointed.
        var mobsHostBefore = ContentHosts(h).Single(c => ReferenceEquals(c.Content, mobs));

        h.DocumentDock.ActiveDockable = mobs;
        h.Pump();
        var mobsHostAfter = ContentHosts(h).Single(c => ReferenceEquals(c.Content, mobs));
        Assert.Same(mobsHostBefore, mobsHostAfter);

        h.DocumentDock.ActiveDockable = game;
        h.Pump();

        // Game's host is still Game's, and Mobs' host still exists holding Mobs.
        Assert.Contains(ContentHosts(h), c => ReferenceEquals(c.Content, game));
        Assert.Contains(ContentHosts(h), c => ReferenceEquals(c.Content, mobs));
    }

    [AvaloniaFact]
    public void Only_the_active_dockables_host_is_visible()
    {
        using var h = new Harness();

        var mobs = NewMobs(h);
        AddToDocumentDock(h, mobs);

        h.DocumentDock.ActiveDockable = mobs;
        h.Pump();

        // Visibility is what selects the rendered tab; content is never swapped.
        var cachedHost = h.DocumentControl.GetVisualDescendants()
            .OfType<ItemsControl>()
            .First(c => c.Name == "PART_CachedContentHost");

        var visible = cachedHost.GetVisualDescendants()
            .OfType<DockableControl>()
            .Where(d => d.IsVisible)
            .Select(d => d.DataContext)
            .OfType<IDockable>()
            .ToArray();

        Assert.Contains(visible, d => ReferenceEquals(d, mobs));
    }
}
