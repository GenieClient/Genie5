using System;
using System.Collections.Generic;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// <see cref="AutoMapperEngine.HasUnsavedChanges"/>: what <c>#mapper reset</c>
/// consults before reloading the zone from disk while Record mode is on.
///
/// <para>Background (Oct 2026 Renucci logs): uber.cmd sends
/// <c>#config automapper true</c> at every launch, which in Genie 5 is the
/// mapper's Record mode. <c>#mapper reset</c> refused to reload the zone for
/// the whole session because "recording may hold unsaved rooms" — even when it
/// held none — so a zone that went stale after a ferry crossing never
/// recovered, and travel.cmd's reset-and-retry loop spun 3,134 times over
/// seven hours. The engine now tracks whether anything is actually unsaved,
/// and the reload goes ahead when nothing is.</para>
/// </summary>
public class AutoMapperUnsavedChangesTests
{
    private sealed class FakeGameState : IMapperGameState
    {
        public string RoomTitle       { get; set; } = "";
        public string RoomDescription { get; set; } = "";
        public IReadOnlyCollection<string> Exits { get; set; } = Array.Empty<string>();
        public string ServerRoomId    { get; set; } = "";
        public event Action? StateChanged;
        public void Enter(string title, string desc, params string[] exits)
        {
            RoomTitle = title; RoomDescription = desc; Exits = exits;
            StateChanged?.Invoke();
        }
    }

    /// <summary>A one-room zone with an unlinked east exit; the character is
    /// placed in that room.</summary>
    private static (AutoMapperEngine engine, FakeGameState fake, MapZone zone) Make(bool record)
    {
        var zone = new MapZone { Name = "Testville", Genie4Id = "7" };
        var r1 = new MapNode { Id = 1, Title = "Town Square", Description = "A square.", PixelX = 100, PixelY = 100 };
        r1.Exits.Add(new MapExit { Direction = Direction.East, MoveCommand = "east" });
        zone.Nodes[1] = r1;

        var engine = new AutoMapperEngine(new MapZone { Name = "placeholder" }) { IsEnabled = record };
        var fake   = new FakeGameState();
        engine.Attach(fake);
        engine.LoadZone(zone);
        fake.Enter("Town Square", "A square.", "east");
        Assert.Equal(1, engine.CurrentNode?.Id);
        return (engine, fake, zone);
    }

    [Fact]
    public void A_freshly_loaded_zone_has_nothing_unsaved()
    {
        var (engine, _, _) = Make(record: true);
        Assert.False(engine.HasUnsavedChanges);
    }

    [Fact]
    public void Record_mode_creating_a_room_marks_the_zone_unsaved()
    {
        var (engine, fake, zone) = Make(record: true);

        engine.OnCommandSent("east");
        fake.Enter("Dark Alley", "An alley.", "west");

        Assert.Equal(2, zone.Nodes.Count);          // the room was recorded …
        Assert.True(engine.HasUnsavedChanges);      // … and nobody has saved it
    }

    [Fact]
    public void Lookup_only_mode_never_marks_the_zone_unsaved()
    {
        var (engine, fake, zone) = Make(record: false);

        engine.OnCommandSent("east");
        fake.Enter("Dark Alley", "An alley.", "west");

        Assert.Single(zone.Nodes);                  // lookup-only creates nothing
        Assert.False(engine.HasUnsavedChanges);
    }

    [Fact]
    public void Editor_mutations_count_as_unsaved_whatever_the_mode()
    {
        var (engine, _, _) = Make(record: false);
        engine.AddNode("Scratch", 0, 0, 0);
        Assert.True(engine.HasUnsavedChanges);
    }

    [Fact]
    public void Loading_a_zone_clears_the_flag()
    {
        var (engine, _, zone) = Make(record: true);
        engine.AddNode("Scratch", 0, 0, 0);
        Assert.True(engine.HasUnsavedChanges);

        engine.LoadZone(zone);                      // what #mapper load / reset do
        Assert.False(engine.HasUnsavedChanges);
    }

    [Fact]
    public void MarkSaved_clears_the_flag()
    {
        var (engine, _, _) = Make(record: true);
        engine.AddNode("Scratch", 0, 0, 0);
        Assert.True(engine.HasUnsavedChanges);

        engine.MarkSaved();                         // what the host does after writing the file
        Assert.False(engine.HasUnsavedChanges);
    }
}
