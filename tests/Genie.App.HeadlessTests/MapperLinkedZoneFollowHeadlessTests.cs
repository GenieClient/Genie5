using System;
using System.Collections.Generic;
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
/// #426: walking onto a boundary stub (Shard's East Bridge, a copy of Map66's
/// room inside Map67) must put <c>$zoneid</c>/<c>$roomid</c> on the new zone in
/// the same room update — before the UI thread runs — and the mapper view must
/// then follow without loading the zone into the engine a second time.
/// Driven over real map files through the production MapperViewModel loader.
/// </summary>
public class MapperLinkedZoneFollowHeadlessTests : IAsyncLifetime
{
    private const string Map66 = "Map66_Shard_to_Gondola_Area";
    private const string Map67 = "Map67_Shard";

    private string          _dir  = "";
    private string          _maps = "";
    private GenieCore       _core = null!;
    private MapperViewModel _vm   = null!;

    private sealed class FakeGameState : IMapperGameState
    {
        public string RoomTitle { get; set; } = "";
        public string RoomDescription { get; set; } = "";
        public IReadOnlyCollection<string> Exits { get; set; } = Array.Empty<string>();
        public string ServerRoomId { get; set; } = "";
        public event Action? StateChanged;
        public void Fire() => StateChanged?.Invoke();
    }

    public Task InitializeAsync()
    {
        _dir  = Path.Combine(Path.GetTempPath(), "genie_linkfollow_" + Guid.NewGuid().ToString("N"));
        _maps = Path.Combine(_dir, "Maps");
        Directory.CreateDirectory(_maps);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_core is not null) await _core.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static MapNode Node(int id, string title, string notes, params (Direction dir, string move, int? dest)[] exits)
    {
        var n = new MapNode { Id = id, Title = title, Notes = notes };
        foreach (var (dir, move, dest) in exits)
            n.Exits.Add(new MapExit { Direction = dir, MoveCommand = move, DestinationId = dest });
        return n;
    }

    private void WriteMaps()
    {
        var shard = new MapZone { Name = "Shard", Genie4Id = "67" };
        shard.Nodes[32]  = Node(32, "Shard, East City Gates", "",
            (Direction.North, "north", null), (Direction.South, "south", null),
            (Direction.West, "west", null), (Direction.None, "go city gate", 132));
        shard.Nodes[132] = Node(132, "Shard, East Bridge", Map66 + ".xml|E Gate|egate",
            (Direction.East, "east", null), (Direction.None, "go city gate", 32));
        Genie4MapExporter.Export(shard, Path.Combine(_maps, Map67 + ".xml"));

        var gondola = new MapZone { Name = "Shard to Gondola Area", Genie4Id = "66" };
        gondola.Nodes[44]  = Node(44, "Shard, East Bridge", "East Bridge|Portal",
            (Direction.East, "east", null), (Direction.None, "go city gate", 216));
        gondola.Nodes[216] = Node(216, "Shard, East City Gates", Map67 + ".xml|egate",
            (Direction.North, "north", null), (Direction.South, "south", null),
            (Direction.West, "west", null), (Direction.None, "go city gate", 44));
        Genie4MapExporter.Export(gondola, Path.Combine(_maps, Map66 + ".xml"));
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    [AvaloniaFact]
    public void Zoneid_is_the_new_zone_before_the_UI_runs_and_the_view_follows_without_a_reload()
    {
        WriteMaps();
        _core = new GenieCore(dataDirectoryOverride: _dir, gameThreadOverride: false);
        _vm   = new MapperViewModel();
        _vm.Attach(_core);
        _vm.MapsDirectory = _maps;

        var state = new FakeGameState { RoomTitle = "Shard, East City Gates", Exits = new[] { "n", "s", "w" } };
        _core.AutoMapper.Attach(state);
        _vm.SelectedZoneFile = Map67;
        Pump();
        Assert.Equal(32, _core.AutoMapper.CurrentNode?.Id);
        Assert.Equal("67", _core.Scripts.Globals["zoneid"]);

        // Through the gate onto the bridge — the stub in Map67.
        _core.AutoMapper.OnCommandSent("go city gate");
        state.RoomTitle = "Shard, East Bridge";
        state.Exits     = new[] { "e" };
        state.Fire();

        // No UI pump yet: a script reacting to this arrival reads the new zone.
        Assert.Equal("66", _core.Scripts.Globals["zoneid"]);
        Assert.Equal("44", _core.Scripts.Globals["roomid"]);
        var engineZone = _core.AutoMapper.ActiveZone;

        Pump();

        // The view caught up to the zone the engine followed into…
        Assert.Equal(Map66, _vm.SelectedZoneFile);
        Assert.Equal("66",  _vm.CurrentZoneId);
        // …without loading it into the engine again.
        Assert.Same(engineZone, _core.AutoMapper.ActiveZone);
        Assert.Equal(44, _core.AutoMapper.CurrentNode?.Id);
        Assert.False(_vm.BrowsingZone);
    }
}
