using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Genie.App.Settings;
using Genie.App.ViewModels;
using Genie.App.Views;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #357 — Layout ▸ Script Bar Position, the top/bottom choice for the
/// running-script strip (Genie 4 docked it at the top by default, which is
/// where long-time users look for it).
///
/// <para>The strip is declared TWICE in MainWindow.axaml — once in the Top dock
/// slot, once in the Bottom one — and gated by
/// <see cref="MainWindowViewModel.ShowScriptBarTop"/> /
/// <see cref="MainWindowViewModel.ShowScriptBarBottom"/>, the same two-slot
/// shape the hands strip uses. Everything here guards a link in that chain that
/// fails silently: a compound flag that forgets the "is anything running" half
/// would pin an empty strip on screen forever, a missing slot would make the
/// menu item do nothing, and a setting that doesn't persist would snap back to
/// the default at the next launch with no error anywhere.</para>
/// </summary>
public class ScriptBarPositionHeadlessTests : IDisposable
{
    private readonly string _dir;

    public ScriptBarPositionHeadlessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie_scriptbar_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private MainWindowViewModel NewVm() => new(startup: null, dataDirectoryOverride: _dir);

    private static void Run(ReactiveUI.ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> cmd)
        => cmd.Execute().Subscribe();

    [AvaloniaFact]
    public void Bottom_is_the_default_so_the_strip_does_not_move_under_existing_users()
    {
        var vm = NewVm();
        Assert.True(vm.Display.ScriptBarAtBottom);
        Assert.False(vm.Display.ScriptBarAtTop);
    }

    [AvaloniaFact]
    public void Strip_is_hidden_in_both_slots_while_nothing_is_running()
    {
        var vm = NewVm();
        Assert.False(vm.ScriptBar.HasScripts);
        Assert.False(vm.ShowScriptBarTop);
        Assert.False(vm.ShowScriptBarBottom);

        // …and moving it does not conjure an empty strip into the other slot.
        Run(vm.ScriptBarToTopCommand);
        Assert.False(vm.ShowScriptBarTop);
        Assert.False(vm.ShowScriptBarBottom);
    }

    [AvaloniaFact]
    public void A_running_script_lights_exactly_the_chosen_slot()
    {
        var vm = NewVm();
        vm.ScriptBar.RunningScripts.Add(new ScriptBarItem("hunt", isJavaScript: false));
        Assert.True(vm.ScriptBar.HasScripts);

        Assert.False(vm.ShowScriptBarTop);
        Assert.True(vm.ShowScriptBarBottom);

        Run(vm.ScriptBarToTopCommand);
        Assert.True(vm.ShowScriptBarTop);
        Assert.False(vm.ShowScriptBarBottom);

        Run(vm.ScriptBarToBottomCommand);
        Assert.False(vm.ShowScriptBarTop);
        Assert.True(vm.ShowScriptBarBottom);

        vm.ScriptBar.RunningScripts.Clear();
        Assert.False(vm.ShowScriptBarTop);
        Assert.False(vm.ShowScriptBarBottom);
    }

    [AvaloniaFact]
    public void Choosing_top_persists_to_display_json()
    {
        var vm = NewVm();
        Run(vm.ScriptBarToTopCommand);

        var path = Directory.EnumerateFiles(_dir, "display.json", SearchOption.AllDirectories).Single();
        Assert.False(DisplaySettings.Load(path).ScriptBarAtBottom);

        Run(vm.ScriptBarToBottomCommand);
        Assert.True(DisplaySettings.Load(path).ScriptBarAtBottom);
    }

    [AvaloniaFact]
    public void Position_rides_on_a_saved_layout_and_old_layouts_keep_the_bottom_slot()
    {
        // A layout written before #357 has no key at all — it must not silently
        // move a user's strip when they load it.
        var legacy = SavedLayout.FromJson("{\"Name\":\"Legacy\"}");
        Assert.NotNull(legacy);
        Assert.True(legacy!.ScriptBarAtBottom);

        var round = SavedLayout.FromJson(new SavedLayout { ScriptBarAtBottom = false }.ToJson());
        Assert.NotNull(round);
        Assert.False(round!.ScriptBarAtBottom);
    }

    [AvaloniaFact]
    public void The_extracted_strip_still_renders_its_chips_off_the_inherited_data_context()
    {
        // The markup moved out of MainWindow.axaml into its own UserControl so
        // it could be hosted in two slots. Every binding in it resolves against
        // the DataContext it INHERITS from the window, which is the thing the
        // move could quietly have broken: Avalonia reports a failed binding path
        // to the trace log and renders an empty strip.
        //
        // Hosted in a DockPanel the same way MainWindow does it, so the Dock
        // attached property and the visibility flags are exercised too. The real
        // window can't be built here — its enhanced hands strip recolours the
        // compass sprites through Bitmap.CopyPixels, which the headless
        // rendering platform does not implement.
        var vm  = NewVm();
        var top = new ScriptBarView();
        var bot = new ScriptBarView();
        DockPanel.SetDock(top, Avalonia.Controls.Dock.Top);
        DockPanel.SetDock(bot, Avalonia.Controls.Dock.Bottom);
        top.Bind(Visual.IsVisibleProperty, new Binding(nameof(vm.ShowScriptBarTop)));
        bot.Bind(Visual.IsVisibleProperty, new Binding(nameof(vm.ShowScriptBarBottom)));

        var win = new Window
        {
            Width = 900, Height = 600,
            DataContext = vm,
            Content = new DockPanel { LastChildFill = true, Children = { top, bot, new Border() } },
        };
        try
        {
            win.Show();
            vm.ScriptBar.RunningScripts.Add(new ScriptBarItem("hunt", isJavaScript: false));
            Pump(win);

            Assert.False(top.IsVisible);
            Assert.True(bot.IsVisible);

            var chip = bot.GetVisualDescendants().OfType<TextBlock>()
                          .Select(t => t.Text).ToArray();
            Assert.Contains("Scripts:", chip);
            Assert.Contains("hunt", chip);

            Run(vm.ScriptBarToTopCommand);
            Pump(win);
            Assert.True(top.IsVisible);
            Assert.False(bot.IsVisible);
            Assert.Contains("hunt", top.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        }
        finally { try { win.Close(); } catch { } }
    }

    private static void Pump(Window w)
    {
        Dispatcher.UIThread.RunJobs();
        w.Measure(new Size(w.Width, w.Height));
        w.Arrange(new Rect(0, 0, w.Width, w.Height));
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Source guard: both slots must bind IsVisible to their own
    /// compound flag. Binding either one straight to ScriptBar.HasScripts (the
    /// pre-#357 shape) would render the strip in BOTH places at once.</summary>
    [Fact]
    public void Both_slots_bind_their_own_visibility_flag()
    {
        var xaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "MainWindow.axaml"));
        var slots = Regex.Matches(xaml, @"<views:ScriptBarView\b((?:[^<>""]|""[^""]*"")*?)/>",
                                  RegexOptions.Singleline)
                         .Select(m => m.Groups[1].Value)
                         .ToArray();

        Assert.Equal(2, slots.Length);
        Assert.Single(slots, a => a.Contains("DockPanel.Dock=\"Top\"")
                               && a.Contains("{Binding ShowScriptBarTop}"));
        Assert.Single(slots, a => a.Contains("DockPanel.Dock=\"Bottom\"")
                               && a.Contains("{Binding ShowScriptBarBottom}"));
    }
}
