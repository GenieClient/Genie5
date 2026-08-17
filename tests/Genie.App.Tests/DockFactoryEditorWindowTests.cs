using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Dock.Model.Core;
using Genie.App.Docking;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Covers the mechanism <c>useeditorrawxmlwindow</c> / <c>useeditorstreamwindow</c>
/// actually introduce: a factory-level type swap that applies to every window
/// instance of a type at once (all 12 Stream tools share one flag).
///
/// <para>Runs <see cref="GenieDockFactory.CreateLayout"/> with no live Avalonia
/// <c>Application</c> — confirmed safe by spike:
/// <see cref="MainWindowViewModel"/>'s constructor only touches
/// <c>Application.Current</c> through null-conditional guards, and
/// <c>CreateLayout</c> only builds Dock.Model POCOs, never an actual
/// <c>Window</c>. This is coverage the existing <c>useeditorgamewindow</c>
/// flag doesn't have today.</para>
///
/// <para>Each test points <see cref="MainWindowViewModel"/> at an isolated
/// temp directory via its Task 10 <c>dataDirectoryOverride</c> seam instead of
/// letting it discover the real per-user Genie5 AppData folder.</para>
/// </summary>
public class DockFactoryEditorWindowTests
{
    private static readonly string[] StreamIds =
    [
        "logons", "talk", "whispers", "thoughts", "combat", "familiar",
        "death", "assess", "atmospherics", "ooc", "log", "itemlog",
    ];

    private static void SetFlag(MainWindowViewModel vm, string propertyName, bool value) =>
        typeof(MainWindowViewModel).GetProperty(propertyName)!.SetValue(vm, value);

    private static IDockable? FindById(IDockable root, string id)
    {
        if (root.Id == id) return root;
        if (root is IDock dock && dock.VisibleDockables is not null)
            foreach (var child in dock.VisibleDockables)
            {
                var found = FindById(child, id);
                if (found is not null) return found;
            }
        return null;
    }

    /// <summary>
    /// Deviation from the plan brief's test, documented in task-11-report.md:
    /// Raw XML, Atmospherics, and OOC are "hidden by default" — the factory
    /// constructs and registers them in its private <c>_tools</c> lookup for
    /// the Window menu, but never attaches them to any dock's
    /// <see cref="IDock.VisibleDockables"/>, so <see cref="FindById"/> alone
    /// can never reach them (pre-existing behavior; not introduced by this
    /// feature). Falls back to the factory's private tool registry via
    /// reflection — the same registry the Window-menu "re-open" toggles read
    /// from — matching this test's existing reflection-based access to
    /// private members (see <see cref="SetFlag"/>).
    /// </summary>
    private static IDockable ResolveDockable(GenieDockFactory factory, IDockable root, string id)
    {
        var found = FindById(root, id);
        if (found is not null) return found;

        var toolsField = typeof(GenieDockFactory)
            .GetField("_tools", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var tools = (IDictionary)toolsField.GetValue(factory)!;
        Assert.True(tools.Contains(id), $"'{id}' was not found in the dock tree or the tool registry.");
        var entry = tools[id]!;
        return (IDockable)entry.GetType().GetField("Item1")!.GetValue(entry)!;
    }

    private sealed class Harness : IDisposable
    {
        public MainWindowViewModel Vm { get; }
        private readonly string _dir;

        public Harness()
        {
            _dir = Path.Combine(Path.GetTempPath(), "genie_app_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Vm = new MainWindowViewModel(startup: null, dataDirectoryOverride: _dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Raw_xml_stays_on_the_legacy_type_by_default()
    {
        using var h = new Harness();
        var factory = new GenieDockFactory(h.Vm);
        var root = factory.CreateLayout();

        Assert.IsType<RawXmlTool>(ResolveDockable(factory, root, "raw-xml"));
    }

    [Fact]
    public void Raw_xml_switches_to_the_editor_type_when_the_flag_is_on()
    {
        using var h = new Harness();
        SetFlag(h.Vm, nameof(MainWindowViewModel.UseEditorRawXmlWindow), true);
        var factory = new GenieDockFactory(h.Vm);
        var root = factory.CreateLayout();

        Assert.IsType<EditorRawXmlTool>(ResolveDockable(factory, root, "raw-xml"));
    }

    [Fact]
    public void Stream_windows_stay_on_the_legacy_type_by_default()
    {
        using var h = new Harness();
        var factory = new GenieDockFactory(h.Vm);
        var root = factory.CreateLayout();

        foreach (var id in StreamIds)
            Assert.IsType<StreamTool>(ResolveDockable(factory, root, id));
    }

    [Fact]
    public void All_twelve_stream_windows_switch_to_the_editor_type_when_the_flag_is_on()
    {
        using var h = new Harness();
        SetFlag(h.Vm, nameof(MainWindowViewModel.UseEditorStreamWindow), true);
        var factory = new GenieDockFactory(h.Vm);
        var root = factory.CreateLayout();

        foreach (var id in StreamIds)
            Assert.IsType<EditorStreamTool>(ResolveDockable(factory, root, id));
    }
}
