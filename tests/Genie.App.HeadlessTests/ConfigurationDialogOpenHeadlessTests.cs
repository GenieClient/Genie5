using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Genie.App.Controls;
using Genie.App.ViewModels;
using Genie.App.Views;
using Genie.Core.Dialogs;
using Genie.Core.Layout;
using Genie.Core.Profiles;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #402 — Edit ▸ Configuration was slow to open. The window now shows
/// before its tabs are filled; only the tab on screen is built and filled then,
/// the rest in idle time or when picked; and a profile switch refills every
/// tab, whether it had been built yet or not.
/// </summary>
public sealed class ConfigurationDialogOpenHeadlessTests : IDisposable
{
    private readonly string       _root = Path.Combine(Path.GetTempPath(), "genie_cfgopen_" + Guid.NewGuid().ToString("N"));
    private readonly ProfileStore _store = new();
    private int _dialogFills;

    public ConfigurationDialogOpenHeadlessTests() =>
        Directory.CreateDirectory(Path.Combine(_root, "Config"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── setup ────────────────────────────────────────────────────────────────

    /// <summary>Each profile gets one "beside another window" server-dialog
    /// answer (so every fill of the Server Dialogs tab asks for the window titles
    /// exactly once) and one trigger of its own.</summary>
    private ConfigurationViewModel Build(params string[] names)
    {
        foreach (var n in names)
        {
            _store.Add(n, "host", 11024, "ACCT", "", characterName: n);
            var m = new ServerDialogMappings();
            m.Set(new ServerDialogMapping { Id = "dlg_" + n, Mode = ServerDialogMode.ExistingWindow, Target = "inv" });
            Assert.True(m.Save(Path.Combine(Genie.Core.Config.GenieConfig.ProfileDirFor(_root, n, "ACCT"),
                                            ServerDialogMappings.FileName)));
        }

        var vm = NewVm();
        foreach (var n in names)   // each profile's trigger, saved to its own triggers.json
        {
            vm.SelectedProfile = Profile(vm, n);
            vm.TriggerEngine!.AddTrigger("^trig_" + n + "$", "say hi");
            vm.OnTriggersChanged();
        }
        return NewVm();            // start clean, on the first profile
    }

    private ConfigurationViewModel NewVm() => new(
        core:             null,
        configRoot:       Path.Combine(_root, "Config"),
        profiles:         _store,
        connectedProfile: null,
        windowSettings:   new WindowSettingsStore())
    {
        DialogTargetWindows = _ =>
        {
            _dialogFills++;
            return new List<(string, string)> { ("inv", "Inventory") };
        },
    };

    private static ConnectionProfile Profile(ConfigurationViewModel vm, string name) =>
        vm.Profiles.Single(p => p.CharacterName == name);

    private static void PumpUntil(Func<bool> done)
    {
        for (int i = 0; i < 200 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            System.Threading.Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Show the dialog and wait for the tab on screen to be filled.</summary>
    private static ConfigurationDialog Open(ConfigurationViewModel vm, bool fillInBackground = true)
    {
        var dialog = new ConfigurationDialog { DataContext = vm, FillInBackground = fillInBackground };
        dialog.Show();
        var tabs = dialog.FindControl<TabControl>("Tabs")!;
        PumpUntil(() => tabs.IsEnabled);
        Assert.True(tabs.IsEnabled);
        return dialog;
    }

    private static void Pick(ConfigurationDialog d, string header, string? subHeader = null)
    {
        var top  = d.FindControl<TabControl>("Tabs")!;
        var item = top.Items.OfType<TabItem>().Single(t => (string?)t.Header == header);
        top.SelectedItem = item;
        if (subHeader is not null)
        {
            var sub = item.GetLogicalDescendants().OfType<TabControl>().First();
            sub.SelectedItem = sub.Items.OfType<TabItem>().Single(t => (string?)t.Header == subHeader);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static List<string> DialogRows(ServerDialogsPanel p) =>
        (p.FindControl<DataGrid>("ItemsList")!.ItemsSource as IEnumerable<ServerDialogsPanel.MappingRow>)?
          .Select(r => r.Id).ToList() ?? new List<string>();

    private static List<string> TriggerRows(TriggersPanel p) =>
        (p.FindControl<DataGrid>("ItemsList")!.ItemsSource as IEnumerable<TriggersPanel.TriggerRow>)?
          .Select(r => r.Pattern).ToList() ?? new List<string>();

    // ── opening ──────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void The_window_shows_before_any_tab_is_filled()
    {
        var vm = Build("Renucci");
        var dialog = new ConfigurationDialog { DataContext = vm };
        try
        {
            Assert.False(dialog.FindControl<TabControl>("Tabs")!.IsEnabled);
            Assert.True(dialog.FindControl<TextBlock>("LoadingText")!.IsVisible);
            Assert.Null(dialog.BuiltPanel<LayoutPanel>());

            dialog.Show();
            PumpUntil(() => dialog.FindControl<TabControl>("Tabs")!.IsEnabled);

            Assert.False(dialog.FindControl<TextBlock>("LoadingText")!.IsVisible);
            Assert.NotNull(dialog.BuiltPanel<LayoutPanel>());   // Layout ▸ Windows is on screen
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void Only_the_tab_on_screen_is_built_at_first()
    {
        var dialog = Open(Build("Renucci"), fillInBackground: false);
        try
        {
            Assert.NotNull(dialog.BuiltPanel<LayoutPanel>());
            Assert.Null(dialog.BuiltPanel<ServerDialogsPanel>());
            Assert.Null(dialog.BuiltPanel<TriggersPanel>());
            Assert.Null(dialog.BuiltPanel<TtsPanel>());
            Assert.Equal(0, _dialogFills);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void Picking_a_tab_builds_and_fills_it()
    {
        var dialog = Open(Build("Renucci"), fillInBackground: false);
        try
        {
            Pick(dialog, "Triggers");
            Assert.Equal(new[] { "^trig_Renucci$" }, TriggerRows(dialog.BuiltPanel<TriggersPanel>()!));

            Pick(dialog, "Layout", "Server Dialogs");
            Assert.Equal(new[] { "dlg_Renucci" }, DialogRows(dialog.BuiltPanel<ServerDialogsPanel>()!));
            Assert.Equal(1, _dialogFills);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void The_other_tabs_are_built_and_filled_once_in_the_background()
    {
        var dialog = Open(Build("Renucci"));
        try
        {
            PumpUntil(() => dialog.BuiltPanel<ClassesPanel>() is not null);   // the last tab

            Assert.NotNull(dialog.BuiltPanel<TtsPanel>());
            Assert.Equal(new[] { "^trig_Renucci$" }, TriggerRows(dialog.BuiltPanel<TriggersPanel>()!));
            Assert.Equal(new[] { "dlg_Renucci" }, DialogRows(dialog.BuiltPanel<ServerDialogsPanel>()!));

            // Picking a tab the background pass already filled doesn't fill it again.
            Pick(dialog, "Layout", "Server Dialogs");
            for (int i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); System.Threading.Thread.Sleep(5); }
            Assert.Equal(1, _dialogFills);
        }
        finally { dialog.Close(); }
    }

    // ── switching profile ────────────────────────────────────────────────────

    [AvaloniaFact]
    public void A_profile_switch_refills_tabs_that_were_already_built()
    {
        var vm = Build("Renucci", "Kzin");
        var dialog = Open(vm, fillInBackground: false);
        try
        {
            Pick(dialog, "Triggers");
            Pick(dialog, "Layout", "Server Dialogs");   // on screen when the profile changes
            Assert.Equal(new[] { "^trig_Renucci$" }, TriggerRows(dialog.BuiltPanel<TriggersPanel>()!));

            vm.SelectedProfile = Profile(vm, "Kzin");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { "dlg_Kzin" }, DialogRows(dialog.BuiltPanel<ServerDialogsPanel>()!));

            // Triggers was built but not on screen: it is refilled when picked.
            Pick(dialog, "Triggers");
            Assert.Equal(new[] { "^trig_Kzin$" }, TriggerRows(dialog.BuiltPanel<TriggersPanel>()!));
            Assert.Equal(2, _dialogFills);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void A_profile_switch_before_a_tab_is_built_fills_it_for_the_new_profile()
    {
        var vm = Build("Renucci", "Kzin");
        var dialog = Open(vm, fillInBackground: false);
        try
        {
            vm.SelectedProfile = Profile(vm, "Kzin");
            Dispatcher.UIThread.RunJobs();
            Assert.Null(dialog.BuiltPanel<TriggersPanel>());

            Pick(dialog, "Triggers");
            Pick(dialog, "Layout", "Server Dialogs");

            Assert.Equal(new[] { "^trig_Kzin$" }, TriggerRows(dialog.BuiltPanel<TriggersPanel>()!));
            Assert.Equal(new[] { "dlg_Kzin" }, DialogRows(dialog.BuiltPanel<ServerDialogsPanel>()!));
            Assert.Equal(1, _dialogFills);   // never filled for Renucci
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void A_profile_switch_during_the_background_pass_ends_up_on_the_new_profile_everywhere()
    {
        var vm = Build("Renucci", "Kzin");
        var dialog = Open(vm);
        try
        {
            vm.SelectedProfile = Profile(vm, "Kzin");   // the first pass has barely started
            PumpUntil(() => dialog.BuiltPanel<ClassesPanel>() is not null);
            for (int i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); System.Threading.Thread.Sleep(5); }

            Assert.Equal(new[] { "^trig_Kzin$" }, TriggerRows(dialog.BuiltPanel<TriggersPanel>()!));
            Assert.Equal(new[] { "dlg_Kzin" }, DialogRows(dialog.BuiltPanel<ServerDialogsPanel>()!));
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void Closing_the_dialog_stops_the_background_pass()
    {
        var dialog = new ConfigurationDialog { DataContext = Build("Renucci") };
        var tabs = dialog.FindControl<TabControl>("Tabs")!;
        // Close the moment the first fill is done, while the pass is queued.
        tabs.PropertyChanged += (_, e) =>
        {
            if (e.Property == Avalonia.Input.InputElement.IsEnabledProperty && tabs.IsEnabled) dialog.Close();
        };
        dialog.Show();
        for (int i = 0; i < 80; i++) { Dispatcher.UIThread.RunJobs(); System.Threading.Thread.Sleep(5); }

        Assert.True(tabs.IsEnabled);
        Assert.NotNull(dialog.BuiltPanel<LayoutPanel>());
        Assert.Null(dialog.BuiltPanel<ClassesPanel>());
    }

    // ── the pattern boxes' monospace font ────────────────────────────────────

    [Theory]
    [InlineData(new[] { "Consolas", "DejaVu Sans Mono" },  "Consolas")]
    [InlineData(new[] { "courier new", "DejaVu Sans" },    "Courier New")]   // case doesn't matter
    [InlineData(new[] { "DejaVu Sans Mono" },              "monospace")]     // none installed: the generic name
    public void The_font_chain_resolves_to_the_first_installed_family(string[] installed, string expected) =>
        Assert.Equal(expected, EditorFonts.Resolve(EditorFonts.Chain, installed));

    [AvaloniaFact]
    public void The_pattern_boxes_use_the_shared_font()
    {
        var dialog = new ConfigurationDialog();
        try
        {
            var font = Avalonia.Application.Current!.Resources[EditorFonts.ResourceKey];
            Assert.IsType<Avalonia.Media.FontFamily>(font);

            dialog.Show();
            var box = dialog.Panel<TriggersPanel>().FindControl<TextBox>("PatternBox")!;
            Assert.Same(font, box.FontFamily);
        }
        finally { dialog.Close(); }
    }
}
