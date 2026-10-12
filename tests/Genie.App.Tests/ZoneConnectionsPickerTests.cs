using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Genie.App.ViewModels;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.App.Tests;

/// <summary>The Cross-Zone Connections editor's zone and room pickers.</summary>
public sealed class ZoneConnectionsPickerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "g5zc-" + Guid.NewGuid().ToString("N"));

    public ZoneConnectionsPickerTests()
    {
        Directory.CreateDirectory(_dir);
        Save("Map1_Crossing", new MapNode { Id = 50, Title = "The Strand, Sandy Path", ServerRoomId = "" },
                              new MapNode { Id = 51, Title = "The Strand, Crystalline Beach", ServerRoomId = "777" });
        Save("Map150_Fang_Cove", new MapNode { Id = 85, Title = "Fang Cove, Fate's Fortune Lane" });
        File.WriteAllText(Path.Combine(_dir, "ZoneConnections.xml"), "<connections/>");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private void Save(string name, params MapNode[] nodes)
    {
        var z = new MapZone { Name = name };
        foreach (var n in nodes) z.Nodes[n.Id] = n;
        new MapZoneRepository().Save(Path.Combine(_dir, name + ".xml"), z);
    }

    private ZoneConnectionsViewModel NewVm() =>
        new(new ZoneConnectionsRepository(Path.Combine(_dir, "ZoneConnections.xml")), _dir);

    [Fact]
    public void ZoneChoices_ListsTheMapsFolderAfterTheAddZoneEntry_AndSkipsTheConnectionsFile()
    {
        var vm = NewVm();
        Assert.Equal(ZoneConnectionsViewModel.AddZoneItem, vm.ZoneChoices[0]);
        Assert.Equal(new[] { "Map150_Fang_Cove", "Map1_Crossing" }, vm.ZoneChoices.Skip(1).ToArray());
    }

    [Fact]
    public async Task PickingAZone_SuggestsItsRooms_PreferringServerIds()
    {
        var vm = NewVm();
        vm.AddCommand.Execute().Subscribe();
        vm.ToZoneSel = "Map1_Crossing";
        await vm.RefreshRoomsAsync(isFrom: false);

        Assert.Equal(new[] { "50", "#777" }, vm.ToRooms.Select(r => r.Value).ToArray());
        Assert.Contains("The Strand, Sandy Path", vm.ToRooms[0].Display);
        Assert.Equal("Map1_Crossing", vm.Selected!.ToZone);       // written back to the connection
    }

    [Fact]
    public void EditingARoomWritesBackToTheConnection()
    {
        var vm = NewVm();
        vm.AddCommand.Execute().Subscribe();
        vm.FromRoomText = "85";
        vm.ToRoomText   = "#777";
        Assert.Equal("85",   vm.Selected!.FromRoom);
        Assert.Equal("#777", vm.Selected.ToRoom);
    }

    [Fact]
    public void AddZoneEntry_OpensTheRowAndLeavesTheOldZoneSelected_ThenConfirmAddsIt()
    {
        var vm = NewVm();
        vm.AddCommand.Execute().Subscribe();
        vm.FromZoneSel = "Map150_Fang_Cove";

        vm.FromZoneSel = ZoneConnectionsViewModel.AddZoneItem;
        Assert.True(vm.AddingZone);
        Assert.Equal("Map150_Fang_Cove", vm.FromZoneSel);           // picker restored, not left on the sentinel
        Assert.Equal("Map150_Fang_Cove", vm.Selected!.FromZone);

        vm.NewZoneName = " Map999_Brand_New.xml ";
        vm.ConfirmAddZoneCommand.Execute().Subscribe();

        Assert.False(vm.AddingZone);
        Assert.Equal("Map999_Brand_New", vm.FromZoneSel);
        Assert.Equal("Map999_Brand_New", vm.Selected.FromZone);
        Assert.Contains("Map999_Brand_New", vm.ZoneChoices);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    public void AddZone_RejectsBlankAndPathyNames(string name)
    {
        var vm = NewVm();
        vm.AddCommand.Execute().Subscribe();
        vm.ToZoneSel = ZoneConnectionsViewModel.AddZoneItem;
        vm.NewZoneName = name;
        vm.ConfirmAddZoneCommand.Execute().Subscribe();

        Assert.True(vm.AddingZone);
        Assert.NotEmpty(vm.AddZoneError);
    }

    [Fact]
    public void ASavedConnectionsZoneIsListedEvenWithoutAMapFile()
    {
        File.WriteAllText(Path.Combine(_dir, "ZoneConnections.xml"),
            "<connections><connection id=\"x\" from-zone=\"Map77_Missing\" from-room=\"1\" to-zone=\"Map1_Crossing\" to-room=\"50\" verb=\"go portal\" transit-type=\"portal\"/></connections>");
        var vm = NewVm();
        Assert.Contains("Map77_Missing", vm.ZoneChoices);
        vm.Selected = vm.Connections[0];
        Assert.Equal("Map77_Missing", vm.FromZoneSel);
    }

    [Fact]
    public async Task RoomCaption_NamesTheResolvedRoom_OrSaysItIsMissing()
    {
        var vm = NewVm();
        vm.AddCommand.Execute().Subscribe();
        vm.ToZoneSel = "Map1_Crossing";
        await vm.RefreshRoomsAsync(isFrom: false);

        vm.ToRoomText = "50";
        Assert.Contains("The Strand, Sandy Path", vm.ToRoomLabel);
        vm.ToRoomText = "#777";                       // matched by server id
        Assert.Contains("Crystalline Beach", vm.ToRoomLabel);
        vm.ToRoomText = "51";                         // matched by room number even though stored as #777
        Assert.Contains("Crystalline Beach", vm.ToRoomLabel);
        vm.ToRoomText = "999";
        Assert.Contains("No room", vm.ToRoomLabel);
        vm.ToRoomText = "";
        Assert.Equal("", vm.ToRoomLabel);
    }

    [Fact]
    public void Save_WarnsAboutAZoneWithNoMapFile()
    {
        var vm = NewVm();
        vm.AddCommand.Execute().Subscribe();
        vm.FromZoneSel = "Map1_Crossing";
        vm.ToZoneSel   = "Map999_Nowhere";
        vm.SaveCommand.Execute().Subscribe();
        Assert.Contains("Map999_Nowhere", vm.Status);
        Assert.DoesNotContain("Map1_Crossing,", vm.Status);
    }

    [Fact]
    public void ReturnPortalCheckbox_WritesBack_ShowsInTheListLabel_AndLoadsFromASavedConnection()
    {
        var vm = NewVm();
        vm.AddCommand.Execute().Subscribe();
        vm.FromZoneSel = "Map150_Fang_Cove";
        Assert.False(vm.Selected!.ReturnsToOrigin);

        vm.ReturnsToOrigin = true;
        Assert.True(vm.Selected.ReturnsToOrigin);
        Assert.Equal("(where you came from)", vm.Selected.ToLabel);

        vm.SaveCommand.Execute().Subscribe();
        var reloaded = NewVm();
        reloaded.Selected = reloaded.Connections.Single();
        Assert.True(reloaded.ReturnsToOrigin);
    }
}
