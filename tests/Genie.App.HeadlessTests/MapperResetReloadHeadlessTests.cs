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
/// The app half of <c>#mapper reset</c> (<see cref="MapperViewModel.ReloadForReset"/>).
/// Lookup-only: always reload from disk, silently (unchanged). Record mode on:
/// reload when nothing is unsaved, refuse when Record has added rooms that a
/// reload would throw away. Before this, Record on meant "never reload", which
/// left uber.cmd's travel loops stuck on a stale zone for hours (Oct 2026).
/// </summary>
public class MapperResetReloadHeadlessTests : IAsyncLifetime
{
    private string          _dir   = "";
    private GenieCore       _core  = null!;
    private MapperViewModel _vm    = null!;
    private FakeState       _state = null!;

    public Task InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "genie_resetreload_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_core is not null) await _core.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class FakeState : IMapperGameState
    {
        public string RoomTitle       { get; private set; } = "";
        public string RoomDescription { get; private set; } = "";
        public string ServerRoomId    { get; private set; } = "";
        public IReadOnlyCollection<string> Exits { get; private set; } = Array.Empty<string>();
        public event Action? StateChanged;
        public void EnterRoom(string title, string description, params string[] exits)
        {
            RoomTitle = title; RoomDescription = description; Exits = exits;
            StateChanged?.Invoke();
        }
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    /// <summary>Two-room zone saved to disk as Testville.xml, character in room 1,
    /// room 2 with an unlinked north exit for Record mode to extend.</summary>
    private void Build()
    {
        _core = new GenieCore(dataDirectoryOverride: _dir, gameThreadOverride: false);
        _vm   = new MapperViewModel();
        _vm.Attach(_core);

        var zone = new MapZone { Name = "Testville", Genie4Id = "7" };
        var r1 = new MapNode { Id = 1, Title = "Town Square", Description = "A square.", PixelX = 100, PixelY = 100 };
        var r2 = new MapNode { Id = 2, Title = "Bank Lobby",  Description = "A lobby.",  PixelX = 120, PixelY = 100 };
        r1.Exits.Add(new MapExit { Direction = Direction.East,  MoveCommand = "east",  DestinationId = 2 });
        r2.Exits.Add(new MapExit { Direction = Direction.West,  MoveCommand = "west",  DestinationId = 1 });
        r2.Exits.Add(new MapExit { Direction = Direction.North, MoveCommand = "north" });
        zone.Nodes[1] = r1; zone.Nodes[2] = r2;

        _core.AutoMapper.LoadZone(zone);
        _state = new FakeState();
        _core.AutoMapper.Attach(_state);
        _state.EnterRoom("Town Square", "A square.", "east");
        Pump();
        Assert.Equal(1, _core.AutoMapper.CurrentNode?.Id);

        // Give the zone a file so a reload has something to read.
        _vm.MapsDirectory = _dir;
        _vm.SaveMapCommand.Execute().Subscribe();
        Pump();
        Assert.True(File.Exists(Path.Combine(_dir, "Testville.xml")));
        Assert.False(_core.AutoMapper.HasUnsavedChanges);
    }

    private int NodeCount => _core.AutoMapper.ActiveZone.Nodes.Count;

    [AvaloniaFact]
    public void Lookup_only_reset_reloads_from_disk_silently()
    {
        Build();
        _core.AutoMapper.AddNode("Scratch", 0, 0, 0);   // in memory only
        Assert.Equal(3, NodeCount);

        var msg = _vm.ReloadForReset();
        Pump();

        Assert.Null(msg);                                // unchanged behaviour: no echo
        Assert.Equal(2, NodeCount);                      // disk state is back
    }

    [AvaloniaFact]
    public void Record_on_with_nothing_unsaved_reloads_and_says_so()
    {
        Build();
        _vm.AutoCreateEnabled = true;
        Pump();
        Assert.True(_core.AutoMapper.IsEnabled);

        var msg = _vm.ReloadForReset();
        Pump();

        Assert.NotNull(msg);
        Assert.Contains("Reloaded Testville", msg);
        Assert.DoesNotContain("not reloaded", msg);
        Assert.Equal(2, NodeCount);
    }

    [AvaloniaFact]
    public void Record_on_with_an_unsaved_room_refuses_until_saved()
    {
        Build();
        _vm.AutoCreateEnabled = true;
        Pump();

        // Walk east into the known room, then north into one the map lacks:
        // Record creates it in memory.
        _core.AutoMapper.OnCommandSent("east");
        _state.EnterRoom("Bank Lobby", "A lobby.", "west", "north");
        Pump();
        Assert.Equal(2, _core.AutoMapper.CurrentNode?.Id);
        _core.AutoMapper.OnCommandSent("north");
        _state.EnterRoom("Dark Alley", "An alley.", "south");
        Pump();
        Assert.Equal(3, NodeCount);
        Assert.True(_core.AutoMapper.HasUnsavedChanges);

        var refused = _vm.ReloadForReset();
        Pump();
        Assert.NotNull(refused);
        Assert.Contains("not reloaded", refused);
        Assert.Equal(3, NodeCount);                      // the recorded room survived

        _vm.SaveMapCommand.Execute().Subscribe();
        Pump();
        Assert.False(_core.AutoMapper.HasUnsavedChanges);

        var reloaded = _vm.ReloadForReset();
        Pump();
        Assert.NotNull(reloaded);
        Assert.Contains("Reloaded Testville", reloaded);
        Assert.Equal(3, NodeCount);                      // disk now has all three
    }
}
