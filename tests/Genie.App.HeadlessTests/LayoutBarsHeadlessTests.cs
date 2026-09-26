using System;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Genie.App.Settings;
using Genie.App.ViewModels;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Every bar rides on a saved layout, as in Genie 4.
///
/// <para>Genie 4's "save layout" writes the same file as its main config, and
/// that file records the Icon Bar, Health bar and Script Bar — shown or hidden,
/// and docked top or bottom — plus Magic Panels. #349 added the Icon Bar and
/// Health bar positions and the Script Bar toggle to <c>display.json</c> only,
/// so they survived a restart (global) but a layout switch left them behind.
/// Reported live 2026-09-26 during the §34 walk: "Are Bars not part of the
/// Character Layout Save?"</para>
///
/// <para>The contract these pin: a layout captures all five, applying it
/// restores all five and persists them, and a layout saved before they existed
/// changes none of them.</para>
/// </summary>
public class LayoutBarsHeadlessTests : IDisposable
{
    private readonly string _dir;

    public LayoutBarsHeadlessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie_layoutbars_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private MainWindowViewModel NewVm() => new(startup: null, dataDirectoryOverride: _dir);

    /// <summary>Every bar moved OFF its default, so a capture that forgot one
    /// would read back the default and fail.</summary>
    private static void MoveEveryBarOffDefault(DisplaySettings d)
    {
        d.ShowIconBar       = false;
        d.IconBarAtBottom   = false;
        d.StatusBarAtBottom = false;
        d.ShowScriptBar     = false;
        d.ShowMagicPanels   = false;
    }

    private static (bool icon, bool iconBottom, bool hpBottom, bool script, bool magic) Bars(DisplaySettings d) =>
        (d.ShowIconBar, d.IconBarAtBottom, d.StatusBarAtBottom, d.ShowScriptBar, d.ShowMagicPanels);

    [AvaloniaFact]
    public void A_saved_layout_captures_every_bar()
    {
        var vm = NewVm();
        MoveEveryBarOffDefault(vm.Display);

        var layout = vm.CaptureCurrentLayout();

        Assert.False(layout.IconBarVisible);
        Assert.False(layout.IconBarAtBottom);
        Assert.False(layout.HealthBarAtBottom);
        Assert.False(layout.ScriptBarVisible);
        Assert.False(layout.MagicPanels);
    }

    [AvaloniaFact]
    public void Loading_a_layout_puts_every_bar_back_where_it_was_saved()
    {
        var vm = NewVm();
        MoveEveryBarOffDefault(vm.Display);
        var saved = SavedLayout.FromJson(vm.CaptureCurrentLayout().ToJson());   // through disk form
        Assert.NotNull(saved);

        // Put everything back to defaults, as switching to another layout would.
        vm.Display.ShowIconBar = vm.Display.IconBarAtBottom = vm.Display.StatusBarAtBottom = true;
        vm.Display.ShowScriptBar = vm.Display.ShowMagicPanels = true;

        vm.ApplyLayout(saved!);

        Assert.Equal((false, false, false, false, false), Bars(vm.Display));
        // The compound flags the two dock slots bind to follow along.
        Assert.False(vm.Display.ShowIconBarTop);      // icon bar hidden → neither slot
        Assert.False(vm.Display.ShowIconBarBottom);
        Assert.True(vm.Display.ShowStatusBarTop);     // health bar at the top
        Assert.False(vm.Display.ShowStatusBarBottom);
    }

    [AvaloniaFact]
    public void Loading_a_layout_persists_the_bars_so_a_restart_keeps_them()
    {
        var vm = NewVm();
        vm.ApplyLayout(new SavedLayout
        {
            IconBarVisible = true, IconBarAtBottom = false, HealthBarAtBottom = false,
            ScriptBarVisible = false, MagicPanels = false,
        });

        var path = Directory.EnumerateFiles(_dir, "display.json", SearchOption.AllDirectories).Single();
        var onDisk = DisplaySettings.Load(path);
        Assert.Equal((true, false, false, false, false), Bars(onDisk));
    }

    [AvaloniaFact]
    public void A_layout_saved_before_the_bars_existed_leaves_them_alone()
    {
        var vm = NewVm();
        MoveEveryBarOffDefault(vm.Display);

        // No bar keys at all — what every layout on disk today looks like.
        var legacy = SavedLayout.FromJson("{\"Name\":\"Legacy\"}");
        Assert.NotNull(legacy);
        Assert.Null(legacy!.IconBarVisible);
        Assert.Null(legacy.HealthBarAtBottom);

        vm.ApplyLayout(legacy);

        // The user's own arrangement survives; nothing snaps to a default.
        Assert.Equal((false, false, false, false, false), Bars(vm.Display));
    }

    [AvaloniaFact]
    public void A_layout_that_records_only_some_bars_changes_only_those()
    {
        var vm = NewVm();
        MoveEveryBarOffDefault(vm.Display);

        // An imported Genie 4 file with an IconBar element and nothing else.
        vm.ApplyLayout(new SavedLayout { IconBarVisible = true, IconBarAtBottom = true });

        Assert.Equal((true, true, false, false, false), Bars(vm.Display));
    }
}
