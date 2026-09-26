using System.Linq;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

/// <summary>
/// <c>#goto @tag</c> routing (<see cref="AutoMapperEngine.FindNearestByTag"/>)
/// and the tag index behind it. The feature shipped in 253f2ba with no tests
/// and no write path; now that the Details panel and <c>#mapper tag</c> can
/// write <see cref="MapNode.Tags"/>, the index must follow an edit at once.
/// </summary>
public class AutoMapperTagRoutingTests
{
    private static MapNode Room(int id, params string[] tags)
    {
        var n = new MapNode { Id = id, Title = $"room {id}", PixelX = id * 20, PixelY = 0 };
        n.Tags.AddRange(tags);
        return n;
    }

    private static void Link(MapZone z, int a, int b)
    {
        z.Nodes[a].Exits.Add(new MapExit { Direction = Direction.East, MoveCommand = "east", DestinationId = b });
        z.Nodes[b].Exits.Add(new MapExit { Direction = Direction.West, MoveCommand = "west", DestinationId = a });
    }

    /// <summary>A street 1—2—3—4—5 with banks at 1 and 5, a moongate at 4, and
    /// an island room 9 (no exits) tagged "island".</summary>
    private static MapZone Street()
    {
        var z = new MapZone { Name = "test" };
        foreach (var n in new[] { Room(1, "Bank"), Room(2), Room(3), Room(4, "moongate"), Room(5, "bank"), Room(9, "island") })
            z.Nodes[n.Id] = n;
        Link(z, 1, 2); Link(z, 2, 3); Link(z, 3, 4); Link(z, 4, 5);
        return z;
    }

    [Fact]
    public void Nearest_tagged_room_wins_by_walking_distance()
    {
        var z = Street();
        var engine = new AutoMapperEngine(z);
        // From 3: bank@1 is 2 steps, bank@5 is 2 steps — tie; from 2 it's 1 vs 3.
        Assert.Equal(1, engine.FindNearestByTag(z.Nodes[2], "bank")!.Id);
        Assert.Equal(5, engine.FindNearestByTag(z.Nodes[4], "bank")!.Id);
    }

    [Fact]
    public void Tag_match_is_case_insensitive_both_ways()
    {
        var z = Street();
        var engine = new AutoMapperEngine(z);
        Assert.Equal(1, engine.FindNearestByTag(z.Nodes[2], "BANK")!.Id);     // room 1 stores "Bank"
        Assert.Equal(4, engine.FindNearestByTag(z.Nodes[3], "MoonGate")!.Id);
    }

    [Fact]
    public void A_tagged_start_room_is_its_own_nearest()
    {
        var z = Street();
        var engine = new AutoMapperEngine(z);
        Assert.Equal(5, engine.FindNearestByTag(z.Nodes[5], "bank")!.Id);
    }

    [Fact]
    public void Unknown_or_unreachable_tags_return_null()
    {
        var z = Street();
        var engine = new AutoMapperEngine(z);
        Assert.Null(engine.FindNearestByTag(z.Nodes[3], "tavern"));   // no such tag
        Assert.Null(engine.FindNearestByTag(z.Nodes[3], "island"));   // tagged, but no path
        Assert.Null(engine.FindNearestByTag(z.Nodes[3], ""));
        Assert.Null(engine.FindNearestByTag(z.Nodes[3], "   "));
    }

    [Fact]
    public void KnownTags_lists_every_tag_in_the_zone_lowercased()
    {
        var engine = new AutoMapperEngine(Street());
        Assert.Equal(new[] { "bank", "island", "moongate" }, engine.KnownTags.OrderBy(t => t));
    }

    [Fact]
    public void Editing_a_rooms_tags_then_NotifyStructureChanged_makes_them_routable()
    {
        var z = Street();
        var engine = new AutoMapperEngine(z);
        Assert.Null(engine.FindNearestByTag(z.Nodes[3], "tavern"));

        // What the Details panel Apply / `#mapper tag add tavern` do.
        z.Nodes[3].Tags.Add("tavern");
        engine.NotifyStructureChanged();
        Assert.Equal(3, engine.FindNearestByTag(z.Nodes[1], "tavern")!.Id);
        Assert.Contains("tavern", engine.KnownTags);

        // …and removing it un-routes it.
        z.Nodes[3].Tags.Clear();
        engine.NotifyStructureChanged();
        Assert.Null(engine.FindNearestByTag(z.Nodes[1], "tavern"));
        Assert.DoesNotContain("tavern", engine.KnownTags);
    }

    [Fact]
    public void Tags_survive_the_zone_file_round_trip()
    {
        var z = Street();
        var xml  = Genie4MapExporter.Serialize(z);
        var back = Genie4MapImporter.ImportFromContent(xml, "test");
        Assert.Contains("Bank", back.Nodes[1].Tags);
        Assert.Contains("moongate", back.Nodes[4].Tags);
        Assert.Empty(back.Nodes[2].Tags);
        Assert.Equal(1, new AutoMapperEngine(back).FindNearestByTag(back.Nodes[2], "bank")!.Id);
    }
}
