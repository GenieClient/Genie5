using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Genie.App.ViewModels;
using Genie.Core;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>
/// Public #273 — two subscribers to the same <c>CurrentNodeChanged</c> event
/// disagreed about the browse-hold.
///
/// <para><c>GenieCore.SyncMapperGlobals</c> bails while the user is browsing
/// another map (GenieCore.cs:1978), so <c>$roomid</c> / <c>$zoneid</c> keep
/// answering "where is my character". <c>MapperViewModel.Refresh</c> had no
/// such guard and wrote straight from the displayed zone — yet its two
/// properties are documented as mirrors of those variables and they feed the
/// status-bar location line. So during any legitimate browse the status bar
/// told the user one room and <c>#goto</c> acted on another, with nothing on
/// screen indicating the view was held.</para>
///
/// <para>The split these tests hold down: the CANVAS follows the browsed map
/// (that is what the user asked to look at), the CHARACTER fields do not.</para>
/// </summary>
public class MapperBrowseHoldHeadlessTests : IAsyncLifetime
{
    private string           _dir   = "";
    private GenieCore        _core  = null!;
    private MapperViewModel  _vm    = null!;

    public Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie_browsehold_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_core is not null) await _core.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void Build()
    {
        _core = new GenieCore(dataDirectoryOverride: _dir, gameThreadOverride: false);
        _vm   = new MapperViewModel();
        _vm.Attach(_core);
    }

    private static MapZone Zone(string name, string genie4Id) =>
        new() { Name = name, Genie4Id = genie4Id };

    /// <summary>Refresh is posted to the dispatcher, so the test has to let it run.</summary>
    private static void Pump() => Dispatcher.UIThread.RunJobs();

    [AvaloniaFact]
    public void Not_browsing_the_character_fields_follow_the_loaded_zone()
    {
        Build();
        _core.AutoMapper.LoadZone(Zone("Crossing", "1"));
        Pump();

        Assert.Equal("Crossing", _vm.ZoneName);
        Assert.Equal("1",        _vm.CurrentZoneId);
        Assert.Equal("1",        _vm.DisplayedZoneId);
        Assert.Equal("Crossing", _vm.CurrentZoneName);
    }

    [AvaloniaFact]
    public void Browsing_holds_the_character_fields_while_the_canvas_moves()
    {
        Build();
        _core.AutoMapper.LoadZone(Zone("Crossing", "1"));
        Pump();

        // The user opens a different map to look at it. This is exactly the
        // state SyncMapperGlobals refuses to write through.
        _core.AutoMapper.ViewIsBrowsing = true;
        _core.AutoMapper.LoadZone(Zone("Shard", "33"));
        Pump();

        // Canvas + panel header follow the browsed map…
        Assert.Equal("Shard", _vm.ZoneName);
        Assert.Equal("33",    _vm.DisplayedZoneId);

        // …the status-bar/$roomid fields stay on the character.
        Assert.Equal("1",        _vm.CurrentZoneId);
        Assert.Equal("Crossing", _vm.CurrentZoneName);
    }

    /// <summary>The zone NAME has to be held with the ids, or the status line
    /// would name one place and number another — which is what made the
    /// original report so hard to read.</summary>
    [AvaloniaFact]
    public void The_status_line_never_mixes_a_browsed_zone_with_a_held_room()
    {
        Build();
        _core.AutoMapper.LoadZone(Zone("Crossing", "1"));
        Pump();

        _core.AutoMapper.ViewIsBrowsing = true;
        _core.AutoMapper.LoadZone(Zone("Shard", "33"));
        Pump();

        Assert.Equal("Crossing", _vm.CurrentZoneName);
        Assert.NotEqual(_vm.CurrentZoneName, _vm.ZoneName);
        Assert.NotEqual(_vm.CurrentZoneId,   _vm.DisplayedZoneId);
    }

    [AvaloniaFact]
    public void Ending_the_browse_lets_the_character_fields_catch_up()
    {
        Build();
        _core.AutoMapper.LoadZone(Zone("Crossing", "1"));
        Pump();

        _core.AutoMapper.ViewIsBrowsing = true;
        _core.AutoMapper.LoadZone(Zone("Shard", "33"));
        Pump();

        // Back to following the character — the next event writes through.
        _core.AutoMapper.ViewIsBrowsing = false;
        _core.AutoMapper.LoadZone(Zone("Shard", "33"));
        Pump();

        Assert.Equal("33",    _vm.CurrentZoneId);
        Assert.Equal("Shard", _vm.CurrentZoneName);
    }
}
