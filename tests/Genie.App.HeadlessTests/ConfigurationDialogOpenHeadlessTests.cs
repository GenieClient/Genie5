using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
/// before its tabs are filled, the tabs are filled once rather than twice, and
/// a profile switch still refills them.
/// </summary>
public sealed class ConfigurationDialogOpenHeadlessTests : IDisposable
{
    private readonly string       _root = Path.Combine(Path.GetTempPath(), "genie_cfgopen_" + Guid.NewGuid().ToString("N"));
    private readonly ProfileStore _store = new();
    private int _fills;

    public ConfigurationDialogOpenHeadlessTests() =>
        Directory.CreateDirectory(Path.Combine(_root, "Config"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Each profile gets one "beside another window" answer, so every
    /// fill of the Server Dialogs tab asks for the window titles exactly once.</summary>
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
        return new ConfigurationViewModel(
            core:             null,
            configRoot:       Path.Combine(_root, "Config"),
            profiles:         _store,
            connectedProfile: null,
            windowSettings:   new WindowSettingsStore())
        {
            DialogTargetWindows = _ =>
            {
                _fills++;
                return new List<(string, string)> { ("inv", "Inventory") };
            },
        };
    }

    private static void PumpUntil(Func<bool> done)
    {
        for (int i = 0; i < 200 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            System.Threading.Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static List<string> DialogRows(ConfigurationDialog d) =>
        (d.FindControl<ServerDialogsPanel>("ServerDialogsPanelCtrl")!
          .FindControl<DataGrid>("ItemsList")!.ItemsSource as IEnumerable<ServerDialogsPanel.MappingRow>)?
          .Select(r => r.Id).ToList() ?? new List<string>();

    [AvaloniaFact]
    public void The_window_shows_before_its_tabs_are_filled_and_fills_them_once()
    {
        var vm = Build("Renucci");
        var dialog = new ConfigurationDialog { DataContext = vm };
        var tabs    = dialog.FindControl<TabControl>("Tabs")!;
        var loading = dialog.FindControl<TextBlock>("LoadingText")!;
        try
        {
            // Nothing is filled until the window is up.
            Assert.Equal(0, _fills);
            Assert.False(tabs.IsEnabled);
            Assert.True(loading.IsVisible);

            dialog.Show();
            PumpUntil(() => tabs.IsEnabled);

            Assert.True(tabs.IsEnabled);
            Assert.False(loading.IsVisible);
            Assert.Equal(new[] { "dlg_Renucci" }, DialogRows(dialog));

            // Let the frame and the timer fallback both fire: still one fill.
            for (int i = 0; i < 60; i++) { Dispatcher.UIThread.RunJobs(); System.Threading.Thread.Sleep(5); }
            Assert.Equal(1, _fills);
        }
        finally { dialog.Close(); }
    }

    [AvaloniaFact]
    public void Picking_another_profile_still_refills_the_tabs()
    {
        var vm = Build("Renucci", "Kzin");
        var dialog = new ConfigurationDialog { DataContext = vm };
        var tabs = dialog.FindControl<TabControl>("Tabs")!;
        try
        {
            dialog.Show();
            PumpUntil(() => tabs.IsEnabled);
            Assert.Equal(new[] { "dlg_Renucci" }, DialogRows(dialog));

            vm.SelectedProfile = vm.Profiles.Single(p => p.CharacterName == "Kzin");
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new[] { "dlg_Kzin" }, DialogRows(dialog));
            Assert.Equal(2, _fills);
        }
        finally { dialog.Close(); }
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
            var box = dialog.FindControl<TriggersPanel>("TriggersPanelCtrl")!.FindControl<TextBox>("PatternBox")!;
            Assert.Same(font, box.FontFamily);
        }
        finally { dialog.Close(); }
    }
}
