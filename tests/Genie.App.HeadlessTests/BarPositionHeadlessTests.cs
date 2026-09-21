using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Genie.App.ViewModels;
using Genie.App.Views;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #349 — Genie 4 gave THREE bars a Dock Top / Dock Bottom submenu
/// (Icon Bar, Script Bar, Health Bar) and made the Script Bar's parent item a
/// checkable show/hide. #357 delivered the Script Bar's position; this covers
/// the rest: the Icon Bar and vitals strip positions, and the Script Bar
/// visibility toggle.
///
/// <para>Each bar is declared in BOTH dock slots and gated by a compound flag,
/// the shape the hands strip established. The failure that shape is guarding
/// against is silent in both directions: a flag that drops the "is it shown at
/// all" half pins an empty bar on screen forever, and one that drops the slot
/// half draws the bar twice.</para>
/// </summary>
public class BarPositionHeadlessTests : IDisposable
{
    private readonly string _dir;

    public BarPositionHeadlessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie_barpos_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private MainWindowViewModel NewVm() => new(startup: null, dataDirectoryOverride: _dir);

    private static void Run(ReactiveUI.ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> cmd)
        => cmd.Execute().Subscribe();

    // ── Defaults: nobody's window moves on upgrade ───────────────────────────

    [AvaloniaFact]
    public void Both_bars_default_to_the_bottom_where_they_have_always_been()
    {
        var vm = NewVm();

        Assert.True(vm.Display.IconBarAtBottom);
        Assert.False(vm.Display.IconBarAtTop);
        Assert.True(vm.Display.StatusBarAtBottom);
        Assert.False(vm.Display.StatusBarAtTop);
    }

    [AvaloniaFact]
    public void The_script_bar_is_shown_by_default_so_the_toggle_changes_nothing_on_upgrade()
    {
        Assert.True(NewVm().Display.ShowScriptBar);
    }

    // ── Exactly one slot is ever realized ────────────────────────────────────

    [AvaloniaFact]
    public void The_icon_bar_realizes_exactly_one_slot_and_only_while_shown()
    {
        var vm = NewVm();

        // Default: shown, bottom.
        Assert.True(vm.Display.ShowIconBarBottom);
        Assert.False(vm.Display.ShowIconBarTop);

        Run(vm.IconBarToTopCommand);
        Assert.True(vm.Display.ShowIconBarTop);
        Assert.False(vm.Display.ShowIconBarBottom);

        // Hidden: neither slot, whichever position is selected.
        vm.Display.ShowIconBar = false;
        Assert.False(vm.Display.ShowIconBarTop);
        Assert.False(vm.Display.ShowIconBarBottom);
    }

    [AvaloniaFact]
    public void The_vitals_strip_realizes_exactly_one_slot_and_only_while_shown()
    {
        var vm = NewVm();

        Assert.True(vm.Display.ShowStatusBarBottom);
        Assert.False(vm.Display.ShowStatusBarTop);

        Run(vm.StatusBarToTopCommand);
        Assert.True(vm.Display.ShowStatusBarTop);
        Assert.False(vm.Display.ShowStatusBarBottom);

        vm.Display.ShowStatusBar = false;
        Assert.False(vm.Display.ShowStatusBarTop);
        Assert.False(vm.Display.ShowStatusBarBottom);
    }

    // ── The Script Bar's third condition ─────────────────────────────────────

    /// <summary>The toggle has to beat "a script is running" — otherwise
    /// hiding the strip would do nothing exactly when the user wants the row
    /// back, which is during a long script.</summary>
    [AvaloniaFact]
    public void Hiding_the_script_bar_wins_over_a_running_script()
    {
        var vm = NewVm();
        vm.ScriptBar.RunningScripts.Add(new ScriptBarItem("hunt", isJavaScript: false));
        Assert.True(vm.ShowScriptBarBottom);

        Run(vm.ToggleScriptBarCommand);

        Assert.False(vm.Display.ShowScriptBar);
        Assert.False(vm.ShowScriptBarTop);
        Assert.False(vm.ShowScriptBarBottom);
    }

    /// <summary>…and showing it again does not pin an empty strip: the
    /// "something is running" half still applies.</summary>
    [AvaloniaFact]
    public void Showing_the_script_bar_still_requires_a_running_script()
    {
        var vm = NewVm();
        Run(vm.ToggleScriptBarCommand);      // off
        Run(vm.ToggleScriptBarCommand);      // on again

        Assert.True(vm.Display.ShowScriptBar);
        Assert.False(vm.ShowScriptBarTop);
        Assert.False(vm.ShowScriptBarBottom);
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void The_positions_and_the_toggle_persist()
    {
        var vm = NewVm();
        Run(vm.IconBarToTopCommand);
        Run(vm.StatusBarToTopCommand);
        Run(vm.ToggleScriptBarCommand);

        var reloaded = NewVm();

        Assert.True(reloaded.Display.IconBarAtTop);
        Assert.True(reloaded.Display.StatusBarAtTop);
        Assert.False(reloaded.Display.ShowScriptBar);
    }

    // ── The window actually hosts both slots ─────────────────────────────────

    /// <summary>
    /// The extracted views construct and bind against the real view-model.
    ///
    /// <para>What this does NOT assert is that MainWindow declares both slots —
    /// constructing the real window pulls in CompassView, whose static
    /// initializer decodes a bitmap the headless platform cannot
    /// (Bitmap.CopyPixels), and the compiled AXAML is not kept as a resource to
    /// read instead. That half is covered at BUILD time: MainWindow sets
    /// x:CompileBindings, so a slot bound to a misspelled or missing flag fails
    /// compilation rather than shipping as a dead binding.</para>
    /// </summary>
    [AvaloniaFact]
    public void The_extracted_bar_views_construct_and_bind()
    {
        var vm = NewVm();

        var icon   = new IconBarView   { DataContext = vm };
        var status = new StatusBarView { DataContext = vm };

        Assert.NotNull(icon.Content);
        Assert.NotNull(status.Content);

        // StatusBarView owns the Magic Panels column flip now, so its grid has
        // to be reachable by name from inside the control.
        Assert.NotNull(status.FindControl<Grid>("StatusBarGrid"));
    }
}
