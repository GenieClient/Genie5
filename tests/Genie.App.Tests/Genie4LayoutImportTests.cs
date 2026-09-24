using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Genie.App.Settings;
using Genie.App.ViewModels;
using Genie.Core;
using Genie.Core.Import;
using Xunit;

namespace Genie.App.Tests;

/// <summary>
/// Public #319 — Genie 4 saved layouts (<c>Config/Layout/*.layout</c>) import as
/// windowed-mode Genie 5 layouts. The fixture follows the shape of the shipped
/// Genie 4 <c>default.layout</c>, including its custom windows and a hidden one.
/// </summary>
public sealed class Genie4LayoutImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "genie_g4layout_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private const string Hunting = """
        <Genie>
          <Windows WindowCount="6">
            <Main Maximized="False" Height="1088" Width="977" Left="951" Top="12" />
            <MonoFont Family="Lucida Console" Size="10" Style="Regular" />
            <Game ID="main" Name="Game" Height="660" Width="471" Left="0" Top="0" TimeStamp="False" Colors="WhiteSmoke, Black" NameListOnly="False">
              <Font Family="Verdana" Size="9" Style="Regular" />
            </Game>
            <Window1 ID="thoughts" Name="Thoughts" IfClosed="game" Height="278" Width="382" Left="471" Top="0" Visible="True" TimeStamp="True" Colors="White, Black" NameListOnly="False" />
            <Window2 ID="familiar" Name="Familiar" IfClosed="game" Height="140" Width="427" Left="471" Top="520" Visible="False" TimeStamp="True" Colors="White, Black" NameListOnly="False" />
            <Window3 ID="percwindow" Name="percWindow" Height="200" Width="300" Left="10" Top="700" Visible="True" TimeStamp="False" Colors="White, Black" NameListOnly="False" />
            <Window4 ID="chatter" Name="Chatter" IfClosed="log" Height="247" Width="400" Left="196" Top="0" Visible="True" TimeStamp="True" Colors="#AAFFAA, Black" NameListOnly="False" />
            <Window5 ID="inv" Name="Inventory" Height="0" Width="-5" Left="-40" Top="10" Visible="True" TimeStamp="False" Colors="White, Black" NameListOnly="False" />
          </Windows>
          <ScriptBar Visible="True" Dock="Top" />
          <StatusBar Visible="False" />
        </Genie>
        """;

    [Fact]
    public void Open_windows_become_mdi_bounds_at_their_genie4_geometry()
    {
        var (layout, report) = Genie4LayoutConverter.Convert(Hunting, "G4 Hunting");

        Assert.NotNull(layout);
        Assert.True(layout!.WindowedMode);
        Assert.Null(layout.DockTree);
        Assert.Equal(new MdiWindowBounds(0, 0, 471, 660, "Normal"), layout.MdiBounds!["game-text"]);
        Assert.Equal(new MdiWindowBounds(471, 0, 382, 278, "Normal"), layout.MdiBounds["thoughts"]);
        Assert.Equal(new MdiWindowBounds(10, 700, 300, 200, "Normal"), layout.MdiBounds["active-spells"]);
        Assert.Equal(new[] { "game-text", "thoughts", "active-spells", "backpack" }, layout.VisibleTools);
        Assert.Equal(4, report.Converted);
    }

    [Fact]
    public void Closed_windows_stay_closed_and_unknown_windows_are_reported()
    {
        var (layout, report) = Genie4LayoutConverter.Convert(Hunting, "G4 Hunting");

        // MdiBounds keys are exactly the windows a windowed restore opens.
        Assert.False(layout!.MdiBounds!.ContainsKey("familiar"));
        Assert.Equal(1, report.Closed);
        Assert.Equal(new[] { "Chatter" }, report.Skipped);
    }

    [Fact]
    public void Bad_sizes_and_negative_positions_are_clamped()
    {
        var (layout, _) = Genie4LayoutConverter.Convert(Hunting, "G4 Hunting");

        Assert.Equal(new MdiWindowBounds(0, 10, 300, 200, "Normal"), layout!.MdiBounds!["backpack"]);
    }

    [Fact]
    public void Main_window_geometry_and_the_bars_carry_over()
    {
        var (layout, _) = Genie4LayoutConverter.Convert(Hunting, "G4 Hunting");

        Assert.True(layout!.HasWindowGeometry);
        Assert.False(layout.WindowMaximized);
        Assert.Equal((977d, 1088d, 951, 12), (layout.WindowWidth, layout.WindowHeight, layout.WindowX, layout.WindowY));
        Assert.False(layout.ScriptBarAtBottom);   // Dock="Top"
        Assert.False(layout.ShowStatusBar);
    }

    [Fact]
    public void A_maximized_main_window_needs_no_rect()
    {
        var (layout, _) = Genie4LayoutConverter.Convert(
            """<Genie><Windows><Main Maximized="True" /></Windows></Genie>""", "G4 x");

        Assert.True(layout!.WindowMaximized);
        Assert.True(layout.HasWindowGeometry);
    }

    [Theory]
    [InlineData("not xml at all")]
    [InlineData("<Other><Windows/></Other>")]
    [InlineData("<Genie><Settings/></Genie>")]
    public void Anything_that_is_not_a_genie4_layout_converts_to_null(string xml)
    {
        Assert.Null(Genie4LayoutConverter.Convert(xml, "x").Layout);
    }

    [Fact]
    public void The_converted_layout_survives_a_save_and_load()
    {
        var (layout, _) = Genie4LayoutConverter.Convert(Hunting, "G4 Hunting");

        var reloaded = SavedLayout.FromJson(layout!.ToJson());

        Assert.NotNull(reloaded);
        Assert.Equal(layout.MdiBounds!["thoughts"], reloaded!.MdiBounds!["thoughts"]);
    }

    [Fact]
    public void Every_mapped_tool_id_is_a_real_windowed_mode_panel()
    {
        var known = KnownPanelIds();
        foreach (var id in Genie4LayoutConverter.MappedToolIds)
            Assert.True(known.Contains(id), $"'{id}' is not in GenieDockFactory's MDI panel table.");
    }

    [Fact]
    public void Layout_files_are_found_in_the_config_folder_and_its_layout_subfolder()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Layout"));
        File.WriteAllText(Path.Combine(_root, "default.layout"), Hunting);
        File.WriteAllText(Path.Combine(_root, "Layout", "mini.layout"), Hunting);
        File.WriteAllText(Path.Combine(_root, "Layout", "notes.txt"), "");

        var files = Genie4LayoutConverter.FindLayoutFiles(_root).Select(Path.GetFileName);

        Assert.Equal(new[] { "default.layout", "mini.layout" }, files);
        Assert.Equal("G4 mini", Genie4LayoutConverter.ImportedName(Path.Combine(_root, "Layout", "mini.layout")));
    }

    [Fact]
    public async System.Threading.Tasks.Task The_import_dialog_saves_layouts_and_add_only_keeps_existing_ones()
    {
        var source = Path.Combine(_root, "g4config");
        Directory.CreateDirectory(Path.Combine(source, "Layout"));
        File.WriteAllText(Path.Combine(source, "Layout", "hunting.layout"), Hunting);
        File.WriteAllText(Path.Combine(source, "Layout", "broken.layout"), "garbage");
        var store = new LayoutStore(Path.Combine(_root, "Layouts"));

        await using var core = new GenieCore(dataDirectoryOverride: Path.Combine(_root, "data"), gameThreadOverride: false);
        var vm = new Genie4ImportViewModel(core, Path.Combine(_root, "cfg"), null, null, globalLayouts: store)
        {
            SourcePath = source,
        };

        var summary = vm.ImportLayoutFiles(store);
        Assert.True(store.Exists("G4 hunting"));
        Assert.Contains("1 imported", summary);
        Assert.Contains("1 unreadable", summary);

        vm.Mode = ImportMode.AddOnly;
        Assert.Contains("1 kept", vm.ImportLayoutFiles(store));
    }

    private static HashSet<string> KnownPanelIds()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src", "Genie.App"))) d = d.Parent;
        Assert.NotNull(d);
        var src = File.ReadAllText(Path.Combine(d!.FullName, "src", "Genie.App", "Docking", "GenieDockFactory.cs"));
        var table = Regex.Match(src, @"var panels = new \(string Id, IDockable Dockable\)\[\]\s*\{(?<body>.*?)\};",
                                RegexOptions.Singleline);
        Assert.True(table.Success);
        return Regex.Matches(table.Groups["body"].Value, @"\(""(?<id>[a-z0-9\-]+)"",")
            .Select(m => m.Groups["id"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
