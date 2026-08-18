using System;
using System.Collections.Generic;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// Auto-create ("record") mode used to short-circuit straight to node
/// creation on ANY zone miss, including the very first room of a session
/// when the engine is still sitting on its pristine, unnamed construction-time
/// placeholder <see cref="MapZone"/> — nobody chose that zone, but
/// <see cref="AutoMapperEngine.OnCommandSent"/>'s miss handling never fired
/// <see cref="AutoMapperEngine.RoomNotFoundInZone"/> to give the app-layer
/// cross-zone auto-detect (<c>MapperViewModel.TryAutoLoadZoneFor</c>) a
/// chance to run. The character was silently seeded into a nameless orphan
/// zone instead of the matching community map — "the map doesn't know which
/// zone to load" even though a local zone file for that exact room exists.
///
/// <para>The fix: <see cref="AutoMapperEngine.LoadZone"/> now flips an
/// internal "a zone was explicitly loaded" flag. Auto-create mode only
/// bypasses the lookup (creating nodes directly, its normal behavior) once
/// that flag is set — by a successful auto-detect, a manual zone pick, OR
/// the player's own "New Zone". Before that, a miss behaves like lookup-only
/// mode: it fires <see cref="AutoMapperEngine.RoomNotFoundInZone"/> instead
/// of creating an orphan.</para>
/// </summary>
public class AutoMapperRecordModeFirstRoomTests
{
    private sealed class FakeGameState : IMapperGameState
    {
        public string RoomTitle { get; set; } = "";
        public string RoomDescription { get; set; } = "";
        public IReadOnlyCollection<string> Exits { get; set; } = Array.Empty<string>();
        public string ServerRoomId { get; set; } = "";
        public event Action? StateChanged;
        public void Fire() => StateChanged?.Invoke();
    }

    [Fact]
    public void RecordMode_FirstRoomOnPristineZone_SignalsMissInsteadOfOrphaning()
    {
        var zone   = new MapZone { Name = "placeholder" };
        var engine = new AutoMapperEngine(zone) { IsEnabled = true };
        var fake   = new FakeGameState();
        engine.Attach(fake);

        (string serverId, string title, IReadOnlyCollection<string> exits)? missed = null;
        engine.RoomNotFoundInZone += (id, title, exits) => missed = (id, title, exits);

        fake.RoomTitle    = "Town Square, Central Plaza";   // IMapperGameState.RoomTitle is already bracket-stripped (MapperGameStateAdapter's job)
        fake.Exits        = new[] { "north", "east" };
        fake.ServerRoomId = "555";
        fake.Fire();

        Assert.Null(engine.CurrentNode);
        Assert.Empty(zone.Nodes);   // no orphan node was silently created
        Assert.NotNull(missed);
        Assert.Equal("555", missed!.Value.serverId);
        Assert.Equal("Town Square, Central Plaza", missed.Value.title);
    }

    [Fact]
    public void RecordMode_AfterAnyZoneIsExplicitlyLoaded_CreatesNodesNormally()
    {
        var placeholder = new MapZone { Name = "placeholder" };
        var engine = new AutoMapperEngine(placeholder) { IsEnabled = true };
        var fake   = new FakeGameState();
        engine.Attach(fake);

        // Simulate the player's own "New Zone" (or a successful auto-detect,
        // or a manual pick) — either way, LoadZone ran at least once.
        engine.NewZone("My Own Map");

        fake.RoomTitle    = "Unmapped Clearing";
        fake.Exits        = new[] { "north" };
        fake.ServerRoomId = "";
        fake.Fire();

        // Normal record-mode behavior is preserved: the miss creates a node
        // in the active zone rather than firing RoomNotFoundInZone.
        Assert.NotNull(engine.CurrentNode);
        Assert.Equal("Unmapped Clearing", engine.CurrentNode!.Title);
        Assert.Single(engine.ActiveZone.Nodes);
    }

    [Fact]
    public void LookupOnlyMode_FirstRoomOnPristineZone_StillSignalsMiss()
    {
        // Unchanged pre-existing behavior — lookup-only mode (IsEnabled =
        // false, the default) never creates nodes; every miss fires
        // RoomNotFoundInZone regardless of whether a zone was ever loaded.
        var zone   = new MapZone { Name = "placeholder" };
        var engine = new AutoMapperEngine(zone);
        var fake   = new FakeGameState();
        engine.Attach(fake);

        var missFired = false;
        engine.RoomNotFoundInZone += (_, _, _) => missFired = true;

        fake.RoomTitle = "[Some Room]";
        fake.Exits     = new[] { "south" };
        fake.Fire();

        Assert.Null(engine.CurrentNode);
        Assert.Empty(zone.Nodes);
        Assert.True(missFired);
    }
}
