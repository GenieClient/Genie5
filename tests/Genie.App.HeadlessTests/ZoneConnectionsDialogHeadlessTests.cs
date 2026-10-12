using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Genie.App.ViewModels;
using Genie.App.Views;
using Genie.Core.Mapper;
using Xunit;

namespace Genie.App.HeadlessTests;

/// <summary>The Cross-Zone Connections dialog renders its zone dropdowns and room pickers.</summary>
public sealed class ZoneConnectionsDialogHeadlessTests
{
    [AvaloniaFact]
    public void Zone_dropdowns_list_the_maps_and_the_room_boxes_filter_by_title()
    {
        var dir = Path.Combine(Path.GetTempPath(), "g5zcd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var z = new MapZone { Name = "Map1_Crossing" };
            z.Nodes[50] = new MapNode { Id = 50, Title = "The Strand, Sandy Path" };
            new MapZoneRepository().Save(Path.Combine(dir, "Map1_Crossing.xml"), z);

            var vm = new ZoneConnectionsViewModel(
                new ZoneConnectionsRepository(Path.Combine(dir, "ZoneConnections.xml")), dir);
            vm.AddCommand.Execute().Subscribe();

            var dlg = new ZoneConnectionsDialog { DataContext = vm };
            dlg.Show();
            Dispatcher.UIThread.RunJobs();
            try
            {
                var combos = dlg.GetVisualDescendants().OfType<ComboBox>().ToList();
                Assert.Equal(2, combos.Count);                              // From zone + To zone
                Assert.All(combos, c => Assert.Equal(
                    new[] { ZoneConnectionsViewModel.AddZoneItem, "Map1_Crossing" },
                    c.Items.Cast<string>().ToArray()));

                var box = dlg.FindControl<AutoCompleteBox>("ToRoomBox")!;
                var room = new RoomChoice("50", "50  ·  The Strand, Sandy Path");
                Assert.True(box.ItemFilter!("strand", room));
                Assert.True(box.ItemFilter!("50", room));
                Assert.False(box.ItemFilter!("teller", room));
                Assert.NotNull(dlg.FindControl<Button>("ToRoomDrop"));
            }
            finally { dlg.Close(); }
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
