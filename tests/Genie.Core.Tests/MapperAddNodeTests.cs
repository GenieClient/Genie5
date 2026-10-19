using Genie.Core.Mapper;
using Xunit;

namespace Genie.Core.Tests;

public class MapperAddNodeTests
{
    [Fact]
    public void AddNode_CreatesUnlinkedRoomAtPositionAndIndexesIt()
    {
        var zone = new MapZone { Name = "z" };
        zone.Nodes[5] = new MapNode { Id = 5, Title = "Existing" };
        var engine = new AutoMapperEngine(zone);
        var changed = 0;
        engine.MapChanged += () => changed++;

        var n = engine.AddNode("  Border Room ", 140, 60, 2, " 9795002 ");

        Assert.Equal(6, n.Id);                       // next free id
        Assert.Equal("Border Room", n.Title);
        Assert.Equal((140, 60, 2), (n.PixelX, n.PixelY, n.Z));
        Assert.Equal("9795002", n.ServerRoomId);
        Assert.Empty(n.Exits);                       // unlinked until the author adds exits
        Assert.Same(n, zone.Nodes[6]);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void AddNode_BlankTitleGetsADefault()
    {
        var engine = new AutoMapperEngine(new MapZone { Name = "z" });
        Assert.Equal("New Room", engine.AddNode("  ", 0, 0, 0).Title);
    }
}
